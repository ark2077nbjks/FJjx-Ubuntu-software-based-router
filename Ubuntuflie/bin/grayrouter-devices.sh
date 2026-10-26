#!/usr/bin/env bash
# ============================================================
#  grayrouter-devices.sh —— 设备管理（黑名单 + 流量统计）
#   黑名单：nft 表 inet gray_dev，按 MAC 丢包（forward + input）
#   流量  ：iw dev wlp2s0 station dump 的 rx/tx（内核维护，最准）
#   数据  ：/etc/grayrouter/devices.json
#   用法  ：apply | block <mac> | unblock <mac> | unblock-all | list
# ============================================================
set -uo pipefail

TBL=gray_dev
JSON=/etc/grayrouter/devices.json
LOG=/var/log/grayrouter-devices.log
# LAN 口自动探测：优先 AP 模式网口，其次 192.168.x 地址的口，最后兜底 wlp2s0
IFACE=${GR_LAN_IF:-}
if [ -z "$IFACE" ]; then
  for i in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
    [ "$(iw dev "$i" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ] && { IFACE=$i; break; }
  done
fi
[ -z "$IFACE" ] && IFACE=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\./ {print $2; exit}')
[ -z "$IFACE" ] && IFACE=wlp2s0

log() { printf '%s %s\n' "$(date '+%F %T')" "$*" >> "$LOG" 2>/dev/null; }
norm_mac() { printf '%s' "${1:-}" | tr 'A-Z' 'a-z' | tr -d ' '; }

ensure_json() {
  [ -f "$JSON" ] || { mkdir -p "$(dirname "$JSON")"; printf '{"devices": {}}\n' > "$JSON"; }
}

json_set() {  # $1=mac $2=blocked(true/false) $3=name
  ensure_json
  python3 - "$JSON" "$1" "$2" "${3:-}" <<'PY'
import json, sys, time
path, mac, blocked, name = sys.argv[1], sys.argv[2], sys.argv[3] == "true", sys.argv[4]
d = json.load(open(path))
devs = d.setdefault("devices", {})
e = devs.setdefault(mac, {"name": "", "blocked": False, "added": int(time.time())})
e["blocked"] = blocked
e["changed"] = int(time.time())
if name:
    e["name"] = name
json.dump(d, open(path, "w"), indent=1, ensure_ascii=False)
PY
}

blocked_list() {
  ensure_json
  python3 - "$JSON" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
for mac, e in d.get("devices", {}).items():
    if e.get("blocked"):
        print(mac)
PY
}

ensure_table() {
  if ! nft list table inet $TBL >/dev/null 2>&1; then
    nft add table inet $TBL 2>>"$LOG"
    log "建表 inet $TBL"
  fi
  nft list set inet $TBL blocked >/dev/null 2>&1 ||
    nft add set inet $TBL blocked '{ type ether_addr; }' 2>>"$LOG"

  mkchain() {
    nft list chain inet $TBL "$1" >/dev/null 2>&1 && return 0
    nft -f - 2>>"$LOG" <<EOF
table inet $TBL {
  chain $1 {
    $2
  }
}
EOF
  }
  mkchain gr_fwd "type filter hook forward priority -10; policy accept;"
  mkchain gr_in  "type filter hook input priority -10; policy accept;"

  nft list chain inet $TBL gr_fwd 2>/dev/null | grep -q '@blocked drop' ||
    nft add rule inet $TBL gr_fwd ether saddr @blocked drop 2>>"$LOG"
  nft list chain inet $TBL gr_in 2>/dev/null | grep -q '@blocked drop' ||
    nft add rule inet $TBL gr_in ether saddr @blocked drop 2>>"$LOG"
}

sync_set() {
  local cur want
  cur=$(nft list set inet $TBL blocked 2>/dev/null | tr ',{}' '\n\n\n' \
        | grep -oiE '([0-9a-f]{2}:){5}[0-9a-f]{2}' | tr 'A-Z' 'a-z' | sort -u)
  want=$(blocked_list | tr 'A-Z' 'a-z' | sort -u)
  [ "$cur" = "$want" ] && return 0

  nft flush set inet $TBL blocked 2>>"$LOG"
  local m
  for m in $want; do
    nft add element inet $TBL blocked "{ $m }" 2>>"$LOG" || log "加集合失败 $m"
  done
  log "同步黑名单：$(printf '%s' "$want" | wc -w) 台"
}

do_apply() {
  ensure_table
  sync_set
  log "apply 完成，黑名单 $(blocked_list | wc -w) 台"
}

do_block() {
  local mac; mac=$(norm_mac "${1:-}")
  [[ "$mac" =~ ^([0-9a-f]{2}:){5}[0-9a-f]{2}$ ]] || { echo "MAC 格式不对：$1"; exit 65; }
  ensure_table
  nft add element inet $TBL blocked "{ $mac }" 2>>"$LOG" || true
  json_set "$mac" true
  log "拉黑 $mac"
  echo "已拉黑 $mac"
}

do_unblock() {
  local mac; mac=$(norm_mac "${1:-}")
  [[ "$mac" =~ ^([0-9a-f]{2}:){5}[0-9a-f]{2}$ ]] || { echo "MAC 格式不对：$1"; exit 65; }
  nft delete element inet $TBL blocked "{ $mac }" 2>/dev/null || true
  json_set "$mac" false
  log "解除 $mac"
  echo "已解除 $mac"
}

do_unblock_all() {
  local n=0 m
  for m in $(blocked_list); do
    nft delete element inet $TBL blocked "{ $m }" 2>/dev/null || true
    json_set "$m" false
    n=$((n+1))
  done
  log "全部解除，共 $n 台"
  echo "已全部解除（$n 台）"
}

do_list() {
  ensure_table
  ensure_json
  iw dev "$IFACE" station dump > /run/grayrouter-devices.sta 2>/dev/null || : > /run/grayrouter-devices.sta
  nft list set inet $TBL blocked > /run/grayrouter-devices.set 2>/dev/null || : > /run/grayrouter-devices.set
  python3 - "$JSON" /run/grayrouter-devices.sta /run/grayrouter-devices.set <<'PY'
import json, re, sys

try:
    devs = json.load(open(sys.argv[1])).get("devices", {})
except Exception:
    devs = {}

sta, cur = {}, None
try:
    for line in open(sys.argv[2], errors="replace"):
        line = line.strip()
        if line.startswith("Station "):
            cur = line.split()[1].lower()
            sta[cur] = {"up": 0, "down": 0, "signal": None}
        elif cur and ":" in line:
            k, v = line.split(":", 1)
            k, v = k.strip(), v.strip()
            if k == "rx bytes" and v.isdigit():
                sta[cur]["up"] = int(v)
            elif k == "tx bytes" and v.isdigit():
                sta[cur]["down"] = int(v)
            elif k == "signal":
                m = re.match(r"(-?\d+)", v)
                if m:
                    sta[cur]["signal"] = int(m.group(1))
except Exception:
    pass

blocked = {b.lower() for b in re.findall(r"(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}", open(sys.argv[3], errors="replace").read())}
for mac, e in devs.items():
    if e.get("blocked"):
        blocked.add(mac.lower())

out = []
for mac in sorted(set(devs) | set(sta) | blocked):
    e = devs.get(mac, {})
    s = sta.get(mac, {})
    out.append({
        "mac": mac,
        "name": e.get("name", ""),
        "blocked": mac in blocked,
        "online": mac in sta,
        "up": s.get("up", 0),
        "down": s.get("down", 0),
        "signal": s.get("signal"),
    })
out.sort(key=lambda x: (not x["online"], -(x["up"] + x["down"])))
print(json.dumps({"devices": out}, ensure_ascii=False))
PY
}

ensure_json
case "${1:-}" in
  apply)       do_apply ;;
  block)       do_block "${2:-}" ;;
  unblock)     do_unblock "${2:-}" ;;
  unblock-all) do_unblock_all ;;
  list)        do_list ;;
  *) echo "用法: $0 apply|block <mac>|unblock <mac>|unblock-all|list"; exit 1 ;;
esac
