#!/usr/bin/env bash
# ============================================================
#  grayrouter-sys.sh —— 系统运维只读/受限操作
#    info                      输出 JSON：服务/更新/进程/负载
#    restart <unit>            重启白名单内的单元
#    power reboot|shutdown|net-restart   受限电源操作
#  由 grayrouter-ctl.sh 以 root 调用
# ============================================================
set -uo pipefail

CMD="${1:-info}"
ARG="${2:-}"
TMP="$(mktemp -d /tmp/grsys.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT

log() { echo "$(date '+%F %T') $*" >> /var/log/grayrouter-sys.log; }

ALLOW_UNITS='^(grayrouter-[a-z0-9-]+\.service|ssh\.service|NetworkManager\.service|docker\.service)$'

do_info() {
  : > "$TMP/svc"
  for u in $(systemctl list-units --type=service --all --no-legend --plain 'grayrouter-*.service' 2>/dev/null | awk '{print $1}') \
           ssh.service NetworkManager.service docker.service; do
    systemctl list-unit-files "$u" >/dev/null 2>&1 || continue
    a=$(systemctl is-active "$u" 2>/dev/null || true)
    e=$(systemctl is-enabled "$u" 2>/dev/null || true)
    printf '%s\t%s\t%s\n' "$u" "${a:-unknown}" "${e:-unknown}" >> "$TMP/svc"
  done

  apt list --upgradable 2>/dev/null | sed '1d' | cut -d/ -f1 | sed '/^$/d' > "$TMP/upd" || true
  ps -eo pcpu,pmem,comm --sort=-pcpu --no-headers 2>/dev/null | grep -vE '^ *[0-9.]+ +[0-9.]+ +(ps|head|grep|awk|sed)$' | head -5 > "$TMP/procs" || true
  uptime | sed 's/.*load average: //' > "$TMP/load" || true
  free -m | awk '/Mem|内存/{print $2, $3}' > "$TMP/mem" || true
  uname -r > "$TMP/kernel" || true

  python3 - "$TMP" <<'PY'
import json, sys, os
d = sys.argv[1]
def lines(n):
    p = os.path.join(d, n)
    return [l.rstrip("\n") for l in open(p, encoding="utf-8", errors="replace")] if os.path.exists(p) else []
svc = []
for l in lines("svc"):
    f = l.split("\t")
    if len(f) == 3:
        svc.append({"unit": f[0], "active": f[1], "enabled": f[2]})
svc.sort(key=lambda s: (0 if s["unit"].startswith("grayrouter") else 1, s["unit"]))
upd = lines("upd")
procs = []
for l in lines("procs"):
    f = l.split()
    if len(f) >= 3:
        try:
            procs.append({"cpu": float(f[0]), "mem": float(f[1]), "name": " ".join(f[2:])})
        except ValueError:
            pass
mem = lines("mem")
out = {
    "services": svc,
    "updates": {"count": len(upd), "pkgs": upd[:10],
                "kernel": (lines("kernel") or [""])[0],
                "kernel_update": any(p.startswith("linux-image") or p.startswith("linux-generic") for p in upd)},
    "procs": procs,
    "load": (lines("load") or [""])[0].strip(),
    "mem_total": int(mem[0].split()[0]) if mem else 0,
    "mem_used": int(mem[0].split()[1]) if mem and len(mem[0].split()) > 1 else 0,
    "ok": True,
}
print(json.dumps(out, ensure_ascii=False))
PY
}

do_restart() {
  local u="$ARG"
  if ! printf '%s' "$u" | grep -qE "$ALLOW_UNITS"; then
    echo "{\"ok\":false,\"msg\":\"不允许重启该单元：$u\"}"; log "拒绝 restart $u"; exit 0
  fi
  log "重启 $u"
  if systemctl restart "$u" 2>&1; then
    sleep 1
    echo "{\"ok\":true,\"unit\":\"$u\",\"active\":\"$(systemctl is-active "$u" 2>/dev/null)\"}"
  else
    echo "{\"ok\":false,\"msg\":\"重启失败：$u\"}"
  fi
}

do_power() {
  case "$ARG" in
    net-restart)
      log "重启 NetworkManager"
      systemd-run --on-active=2 --unit=grayrouter-net-restart --collect systemctl restart NetworkManager >/dev/null 2>&1 \
        && echo '{"ok":true,"msg":"2 秒后重启网络（断网 1-2 秒）"}' \
        || echo '{"ok":false,"msg":"排程失败"}'
      ;;
    reboot)
      log "收到重启请求"
      systemd-run --on-active=3 --unit=grayrouter-reboot --collect systemctl reboot >/dev/null 2>&1 \
        && echo '{"ok":true,"msg":"3 秒后重启设备"}' || echo '{"ok":false,"msg":"排程失败"}'
      ;;
    shutdown)
      log "收到关机请求"
      systemd-run --on-active=3 --unit=grayrouter-poweroff --collect systemctl poweroff >/dev/null 2>&1 \
        && echo '{"ok":true,"msg":"3 秒后关机"}' || echo '{"ok":false,"msg":"排程失败"}'
      ;;
    *) echo "{\"ok\":false,\"msg\":\"未知电源动作：$ARG\"}" ;;
  esac
}

case "$CMD" in
  info) do_info ;;
  restart) do_restart ;;
  power) do_power ;;
  *) echo "{\"ok\":false,\"msg\":\"未知子命令：$CMD\"}" ;;
esac
