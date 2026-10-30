#!/bin/bash
# ============================================================
#  grayrouter-lease.sh —— 静态 DHCP 租约（把设备 IP 钉死）
#
#  用法：
#    grayrouter-lease.sh list                     列出已固定的设备
#    grayrouter-lease.sh set <MAC> <IP> [备注]    新增/修改一条
#    grayrouter-lease.sh del <MAC>                删除一条
#    grayrouter-lease.sh clear                    清空全部
#    grayrouter-lease.sh apply                    让改动立即生效（会断开共享连接几秒）
#
#  原理：NetworkManager 在 IPv4=shared 时起的 dnsmasq 会读
#        /etc/NetworkManager/dnsmasq-shared.d/*.conf
#        写一行 dhcp-host=MAC,IP,名字 即可按 MAC 固定分配。
#
#  限制（dnsmasq 官方语义）：dhcp-host 的 IP 必须落在本实例的
#  dhcp-range 里，否则静默忽略。NM 每个 shared 连接单起一个 dnsmasq，
#  所以跨网段的固定不会生效——本脚本直接拒绝。
#
#  兜底：del / clear 后 apply；或 rm 该 conf 再 apply。
# ============================================================
set -uo pipefail

CONF=/etc/NetworkManager/dnsmasq-shared.d/10-static-leases.conf
DIR=$(dirname "$CONF")
LOG=/var/log/grayrouter-lease.log

need_root() { [ "$(id -u)" = "0" ] || { echo "需要 root：sudo bash $0 $*" >&2; exit 1; }; }
log() { printf '%s %s\n' "$(date '+%F %T')" "$*" >> "$LOG" 2>/dev/null || true; }

valid_mac() { printf '%s' "$1" | grep -Eqi '^([0-9a-f]{2}:){5}[0-9a-f]{2}$'; }
valid_ip()  { printf '%s' "$1" | grep -Eq '^([0-9]{1,3}\.){3}[0-9]{1,3}$'; }

local_nets() {
  ip -o -4 addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | awk -F. '{print $1"."$2"."$3}'
}

shared_conns() {
  nmcli -t -f NAME,UUID connection show 2>/dev/null | while IFS=: read -r name uuid; do
    [ -z "$name" ] && continue
    m=$(nmcli -g ipv4.method connection show "$uuid" 2>/dev/null)
    [ "$m" = "shared" ] || continue
    st=$(nmcli -g GENERAL.STATE connection show "$uuid" 2>/dev/null)
    echo "$st" | grep -qi 'activated\|connected' && echo "$name"
  done
}

do_list() {
  if [ ! -s "$CONF" ]; then echo "（暂无静态租约）"; return 0; fi
  printf '%-18s %-16s %s\n' MAC IP 备注
  grep -E '^dhcp-host=' "$CONF" | sed 's/^dhcp-host=//' | while IFS=, read -r mac ip name; do
    printf '%-18s %-16s %s\n' "$mac" "${ip:--}" "${name:--}"
  done
}

do_set() {
  local mac=${1:-} ip=${2:-} name=${3:-}
  need_root "$@"
  valid_mac "$mac" || { echo "MAC 格式不对：$mac"; exit 1; }
  valid_ip  "$ip"  || { echo "IP 格式不对：$ip"; exit 1; }

  local pre=${ip%.*}
  if ! local_nets | grep -qx "$pre"; then
    echo "× $ip 不在本机任何网段（$(local_nets | tr '\n' ' ')）"
    echo "  dnsmasq 会静默忽略这条固定，请填设备所在网段的地址"
    exit 2
  fi

  name=$(printf '%s' "$name" | tr -d ',' | tr ' ' '-' | cut -c1-32)

  mkdir -p "$DIR"
  [ -f "$CONF" ] || : > "$CONF"
  local tmp; tmp=$(mktemp)

  awk -v mac="$mac" -v ip="$ip" -v nm="$name" '
    BEGIN{FS=","; bad=0}
    /^dhcp-host=/ {
      split(substr($0,11), a, ",")
      if (tolower(a[1])==tolower(mac)) next
      if (a[2]==ip) { bad=1 }
      print; next
    }
    {print}
    END{ if(bad) exit 3 }
  ' "$CONF" > "$tmp"
  local rc=$?
  if [ $rc -eq 3 ]; then rm -f "$tmp"; echo "IP $ip 已被另一台设备固定，先删掉那条" >&2; exit 1; fi
  grep -qi "^dhcp-host=$mac," "$tmp" || printf 'dhcp-host=%s,%s,%s\n' "$mac" "$ip" "$name" >> "$tmp"
  install -m644 "$tmp" "$CONF"; rm -f "$tmp"

  log "set $mac -> $ip ${name:-}"
  echo "✓ 已固定 $mac → $ip${name:+（$name）}"
  echo "  要立即生效请执行：grayrouter-lease.sh apply"
}

do_del() {
  local mac=${1:-}
  need_root "$@"
  valid_mac "$mac" || { echo "MAC 格式不对：$mac"; exit 1; }
  [ -f "$CONF" ] || { echo "没有静态租约文件"; exit 0; }
  local tmp; tmp=$(mktemp)
  grep -vi "^dhcp-host=$mac," "$CONF" > "$tmp" || true
  install -m644 "$tmp" "$CONF"; rm -f "$tmp"
  log "del $mac"
  echo "✓ 已删除 $mac 的固定"
  echo "  要立即生效请执行：grayrouter-lease.sh apply"
}

do_clear() {
  need_root "$@"
  local n=0; [ -s "$CONF" ] && n=$(grep -c '^dhcp-host=' "$CONF" || true)
  rm -f "$CONF"
  log "clear ($n 条)"
  echo "✓ 已清空 $n 条静态租约"
  echo "  要立即生效请执行：grayrouter-lease.sh apply"
}

do_apply() {
  need_root "$@"
  local list; list=$(shared_conns)
  if [ -z "$list" ]; then echo "× 没找到在用的 shared 连接，无法自动生效" >&2; exit 1; fi

  while IFS= read -r c; do
    [ -z "$c" ] && continue
    echo "重启共享连接：$c（会断几秒）"
    nmcli connection down "$c" >/dev/null 2>&1 || true
    sleep 1
    nmcli connection up "$c" >/dev/null 2>&1 || true

    local ok="" i st
    for i in 1 2 3; do
      sleep 2
      st=$(nmcli -g GENERAL.STATE connection show "$c" 2>/dev/null)
      if echo "$st" | grep -qi 'activated\|connected'; then ok=1; break; fi
      nmcli connection up "$c" >/dev/null 2>&1 || true
    done

    if [ -z "$ok" ]; then
      log "apply FAIL $c"
      echo "× 连接「$c」没起来（状态：${st:-未知}）" >&2
      echo "  手动恢复：sudo nmcli connection up \"$c\"" >&2
      echo "  实在不行：桌面「恢复上网.bat」（离线可用）" >&2
      exit 3
    fi
    echo "✓ $c 已恢复"
  done <<< "$list"

  log "apply ok: $(echo "$list" | tr '\n' ' ')"
  echo "✓ 已重新加载，静态租约生效（日志：$LOG）"
}

case "${1:-}" in
  list)  do_list ;;
  set)   shift; do_set "$@" ;;
  del)   shift; do_del "$@" ;;
  clear) do_clear ;;
  apply) do_apply ;;
  *) sed -n '3,16p' "$0" | sed 's/^#  *//' ;;
esac
