#!/usr/bin/env bash
# ============================================================
#  grayrouter-wanfail.sh —— 多 WAN 故障转移（N 条上线，按优先级自动选）
#    自动发现所有「能当出口」的网口：默认路由口 → 有线插线的 → USB 网卡 → 无线 STA
#    每 15 秒探测一次；当前出口连续失败 → 换下一条；更高优先级恢复 → 切回去
#
#    get                    输出状态（KEY=VALUE）
#    tick                   一次探测 + 决策（systemd timer）
#    switch <iface|auto>    立即切到指定网口 / 交回自动
#    set <k> <v>            enable / disable / prefer <iface|auto> / wans "a b c" / failn / okn
# ============================================================
set -uo pipefail

CONF=/etc/grayrouter/wanfail.env
STATE=/run/grayrouter/wanfail.state
CNT=/run/grayrouter/wanfail.counters
LOG=/var/log/grayrouter-wanfail.log
ROLES=/usr/local/bin/grayrouter-roles.sh

ENABLE=0
WANS=""             # 空 = 自动发现（空格分隔，顺序即优先级）
PREFER=""           # 空/auto = 自动选；填网口 = 钉死这条
PRIO=""             # 面板调整的顺序（只影响先后，不影响自动发现）
CHECK_IP=223.5.5.5
STATIC_IP=192.168.42.100
STATIC_GW=192.168.42.129
FAIL_N=3
OK_N=2
[ -f "$CONF" ] && . "$CONF" 2>/dev/null
mkdir -p /run/grayrouter 2>/dev/null

log() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null; }
role_wan() { [ -x "$ROLES" ] && "$ROLES" get 2>/dev/null | sed -n 's/^WAN_IF=//p'; }
role_lan() { [ -x "$ROLES" ] && "$ROLES" get 2>/dev/null | sed -n 's/^LAN_IF=//p'; }

gw_of() {
  local g
  [ -z "${1:-}" ] && { echo ""; return; }
  g=$(ip -4 route show default dev "$1" 2>/dev/null | awk '{print $3; exit}')
  [ -n "$g" ] && { echo "$g"; return; }
  g=$(nmcli -g IP4.GATEWAY dev show "$1" 2>/dev/null | head -1)
  echo "${g%% *}"
}
carrier() { cat "/sys/class/net/$1/carrier" 2>/dev/null || echo 0; }
is_wifi() { [ -e "/sys/class/net/$1/wireless" ]; }
is_ap() { [ "$(iw dev "$1" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ]; }

# ---------- 自动发现候选出口 ----------
iface_kind() {  # $1=网口 → phone | usb-eth | wired | wifi | other
  local c="$1" drv=""
  case "$c" in rndis*|wwan*) echo phone; return ;; esac
  drv=$(readlink -f "/sys/class/net/$c/device/driver" 2>/dev/null | xargs -r basename)
  case "$drv" in
    rndis_host|cdc_ether|cdc_ncm|cdc_mbim) echo phone; return ;;
    r8152|ax88179_178a|asix|mt76xx|lan78xx|mos7720) echo usb-eth; return ;;
  esac
  if is_wifi "$c"; then echo wifi; return; fi
  case "$c" in enx*|usb*) echo usb-eth; return ;; esac
  echo wired
}

discover() {
  local p l n out=""
  p=$(role_wan); l=$(role_lan)
  add() {
    local c="$1"
    [ -z "$c" ] && return
    [ -d "/sys/class/net/$c" ] || return
    [ "$c" = "lo" ] && return
    [ "$c" = "$l" ] && return
    [ -n "$(gw_of "$c")" ] || return
    # 被面板配成共享 LAN 的网口（ipv4.method=shared）不能当出口
    local _cn _mm
    _cn=$(nmcli -g GENERAL.CONNECTION dev show "$c" 2>/dev/null | head -1)
    [ -n "$_cn" ] && _mm=$(nmcli -g ipv4.method con show "$_cn" 2>/dev/null | head -1)
    [ "${_mm:-}" = "shared" ] && return
    # USB 有线网卡（RTL8153/AX88179 这类）默认当 LAN，不抢出口；要当 WAN 请在面板里手动指定出口网口
    if [ "$c" != "$(role_wan)" ] && [ "$(iface_kind "$c")" = "usb-eth" ]; then return; fi
    case " $out " in *" $c "*) return ;; esac
    out="$out $c"
  }
  add "$p"
  # 有线（插了线、有网关）
  for n in $(ls /sys/class/net 2>/dev/null); do
    case "$n" in lo|docker*|veth*|br-*|virbr*) continue ;; esac
    is_wifi "$n" && continue
    [ "$(carrier "$n")" = "1" ] || continue
    add "$n"
  done
  # USB 网卡 / 手机共享（名字特征）
  for n in $(ls /sys/class/net 2>/dev/null); do
    case "$n" in usb*|enx*|rndis*|wwan*) add "$n" ;; esac
  done
  # 无线 STA（不是 AP 的那个）
  for n in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
    is_ap "$n" && continue
    add "$n"
  done
  echo "${out# }"
}

resolve_wans() {  # → ALL（空格分隔，按优先级）
  if [ -n "${WANS// /}" ]; then ALL="$WANS"; else ALL=$(discover); fi
  # 面板里调过顺序：只把 PRIO 里的网口提到前面，其余保持发现顺序（新网口仍会自动加入）
  if [ -n "${PRIO// /}" ]; then
    local _tmp="$ALL" _o=""
    for _c in $PRIO; do
      case " $_tmp " in *" $_c "*)
        _o="$_o $_c"
        _tmp=$(echo "$_tmp" | tr ' ' '\n' | grep -vx "$_c" | tr '\n' ' ')
        ;;
      esac
    done
    ALL=$(echo "$_o $_tmp" | tr -s ' ' | sed 's/^ //;s/ $//')
  fi
  [ -n "${PREFER// /}" ] && [ "$PREFER" != "auto" ] && [ -d "/sys/class/net/$PREFER" ] && \
    ALL="$PREFER $(echo "$ALL" | tr ' ' '\n' | grep -vx "$PREFER" | tr '\n' ' ')"
}

probe() {  # $1=网口 → "ok lat"
  local ifc="$1" gw lip code t0 t1 ms
  [ -z "$ifc" ] && { echo "0 -"; return; }
  [ -d "/sys/class/net/$ifc" ] || { echo "0 -"; return; }
  [ "$(cat "/sys/class/net/$ifc/operstate" 2>/dev/null)" = "down" ] && { echo "0 -"; return; }
  gw=$(gw_of "$ifc")
  lip=$(ip -4 -o addr show dev "$ifc" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)
  [ -z "$lip" ] && { echo "0 -"; return; }
  t0=$(date +%s%N)
  # 必须绑“网口名”而不是源地址：绑源地址时内核仍然按默认路由选路，会假阳性
  if ! ping -I "$ifc" -c 1 -W 2 -q "$CHECK_IP" >/dev/null 2>&1; then
    code=$(curl -s -m 4 -o /dev/null -w '%{http_code}' --interface "$ifc" \
           http://connect.rom.miui.com/generate_204 2>/dev/null)
    [ "$code" = "204" ] || { echo "0 -"; return; }
  fi
  t1=$(date +%s%N); ms=$(( (t1 - t0) / 1000000 ))
  echo "1 $ms"
}

# ---------- 状态 ----------
load_state() { ACTIVE=""; LAST_SWITCH=""; LAST_REASON=""; [ -f "$STATE" ] && . "$STATE" 2>/dev/null; }
save_state() {
  printf 'ACTIVE=%s\nLAST_SWITCH=%s\nLAST_REASON=%s\n' "$ACTIVE" "${LAST_SWITCH:-}" "${LAST_REASON:-}" > "$STATE" 2>/dev/null
}
cnt_get() { awk -v i="$1" '$1==i{print $2" "$3}' "$CNT" 2>/dev/null | head -1; }
cnt_set() {
  local i="$1" f="$2" o="$3" tmp="${CNT}.tmp"
  touch "$CNT" 2>/dev/null
  awk -v i="$i" '$1!=i' "$CNT" 2>/dev/null > "$tmp"; echo "$i $f $o" >> "$tmp"; mv "$tmp" "$CNT" 2>/dev/null
}

resqm() {  # 出口变了：把 cake 挪到新出口（只在已启用时；配置不动）
  local s=/usr/local/bin/grayrouter-sqm.sh
  [ -x "$s" ] || return 0
  [ -r /etc/grayrouter/sqm.env ] || return 0
  ( . /etc/grayrouter/sqm.env 2>/dev/null; [ "${SQM_ENABLED:-0}" = "1" ] || exit 0 )
  "$s" flush >/dev/null 2>&1
  "$s" restore >>"$LOG" 2>&1
  log "已把智能队列挪到新出口"
}

set_active() {  # $1=网口 $2=原因
  local ifc="$1" gw; gw=$(gw_of "$ifc")
  [ -z "$gw" ] && { log "切换失败：$ifc 没有网关"; return 1; }
  ip route replace default via "$gw" dev "$ifc" metric 50 2>/dev/null || { log "切换失败：写路由被拒"; return 1; }
  log "出口 → $ifc（网关 $gw，原因：$2）"
  ACTIVE="$ifc"; LAST_SWITCH=$(date '+%F %T'); LAST_REASON="$2"; save_state
  resqm
}

restore_system() {  # 删掉我们自己加的 50 号默认路由，交回 NetworkManager
  local gw
  for i in $(echo "${ALL:-}" | tr ' ' '\n'); do
    gw=$(gw_of "$i"); [ -z "$gw" ] && continue
    ip route del default via "$gw" dev "$i" metric 50 2>/dev/null
  done
  # 主线上如果也残留了我们加的 50 号路由（同网关），一并清掉
  p=$(role_wan); pgw=$(gw_of "$p")
  [ -n "$pgw" ] && ip route del default via "$pgw" dev "$p" metric 50 2>/dev/null
  resqm
}


# ---------- USB 网卡 / 手机共享：没有 IP 就自动要一次 ----------
ensure_usb_net() {
  local n name
  for n in $(ls /sys/class/net 2>/dev/null | grep -E '^(enx|usb|rndis|wwan)'); do
    ip -4 addr show dev "$n" 2>/dev/null | grep -q 'inet ' && continue
    # 配成有线 LAN 的网口不参与「手机共享自动要地址」
    case "$(nmcli -g GENERAL.CONNECTION dev show "$n" 2>/dev/null | head -1)" in LAN-*) continue ;; esac
    # USB 有线网卡（RTL8153/AX88179 等）默认当 LAN，不当手机共享
    [ "$(iface_kind "$n")" = "usb-eth" ] && continue
    [ "$(cat "/sys/class/net/$n/carrier" 2>/dev/null)" = "1" ] || continue
    name="USB共享-$n"
    if ! nmcli -t -f NAME con show 2>/dev/null | grep -qx "$name"; then
      nmcli con add type ethernet con-name "$name" ifname "$n" \
            ipv4.method auto ipv4.never-default yes ipv4.route-metric 700 \
            ipv6.method ignore connection.autoconnect yes >/dev/null 2>&1
      log "为 $n 建连接（never-default，不会抢默认路由）"
    fi
    nmcli con up "$name" >/dev/null 2>&1
    sleep 1
    if ip -4 addr show dev "$n" 2>/dev/null | grep -q 'inet '; then
      log "$n 已拿到 $(ip -4 -o addr show dev "$n" | awk '{print $4}' | head -1)"
      rm -f "/run/grayrouter/usbnet.fail-$n"
      continue
    fi
    # DHCP 拿不到：累计 3 次后试一次静态（安卓共享常见网段 192.168.42.0/24，网关 .129）
    f="/run/grayrouter/usbnet.fail-$n"
    cnt=$(( $(cat "$f" 2>/dev/null || echo 0) + 1 ))
    echo "$cnt" > "$f" 2>/dev/null
    if [ "$cnt" -lt 3 ]; then
      log "$n 还没有地址（第 $cnt 次，手机没开共享 / 手机没网）"
      continue
    fi
    if ! ip -4 addr show dev "$n" 2>/dev/null | grep -q '192.168.42.100'; then
      ip addr add "${STATIC_IP}/24" dev "$n" 2>/dev/null
      ip route replace default via "$STATIC_GW" dev "$n" metric 700 2>/dev/null
      log "$n DHCP 连续 $cnt 次失败 → 试静态 ${STATIC_IP}/24 网关 $STATIC_GW（metric 700，不会抢主线路）"
    fi
  done
}

tick() {
  [ "$ENABLE" = "1" ] || { echo disabled; return; }
  ensure_usb_net
  resolve_wans
  [ -z "${ALL// /}" ] && { echo "no-wan"; return; }
  load_state

  local best="" best_ok=0 order=0 line ifc res ok lat f o
  for ifc in $ALL; do
    order=$((order+1)); [ "$order" -gt 4 ] && break     # 最多探 4 条，别乱发包
    res=$(probe "$ifc"); ok=${res%% *}; lat=${res##* }
    read -r f o <<< "$(cnt_get "$ifc")"; f=${f:-0}; o=${o:-0}
    if [ "$ok" = "1" ]; then f=0; o=$((o+1)); else f=$((f+1)); o=0; fi
    cnt_set "$ifc" "$f" "$o"
    if [ "$ok" = "1" ] && [ -z "$best" ]; then best="$ifc"; best_ok=1; fi
  done

  if [ -z "$ACTIVE" ]; then
    [ -n "$best" ] && set_active "$best" "首次选路"
  else
    res=$(probe "$ACTIVE"); ok=${res%% *}
    if [ "$ok" = "0" ]; then
      read -r f o <<< "$(cnt_get "$ACTIVE")"; f=${f:-0}
      if [ "$f" -ge "$FAIL_N" ] && [ -n "$best" ] && [ "$best" != "$ACTIVE" ]; then
        set_active "$best" "当前出口连续 $f 次失败"
      fi
    elif [ -n "$best" ] && [ "$best" != "$ACTIVE" ]; then
      # 更高优先级的线恢复了：连续 OK_N 次成功就切回去
      read -r f o <<< "$(cnt_get "$best")"; o=${o:-0}
      if [ "$o" -ge "$OK_N" ]; then set_active "$best" "更高优先级恢复（连续 $o 次成功）"; fi
    fi
  fi
  save_state
  echo "active=$ACTIVE wans=$(echo $ALL | tr ' ' ',')"
}

case "${1:-get}" in
  get)
    resolve_wans; load_state
    echo "ENABLE=$ENABLE"
    echo "MODE=$([ -n "${PREFER// /}" ] && [ "$PREFER" != auto ] && echo prefer || echo auto)"
    echo "PREFER=${PREFER:-auto}"
    # ACTIVE 为空时（例如刚交回系统路由）用真实默认路由口兜底，别让面板显示“跟随系统”
    if [ -z "$ACTIVE" ]; then
      dflt=$(ip route show default 2>/dev/null | awk '{for(i=1;i<NF;i++) if($i=="dev"){print $(i+1); exit}}')
      [ -n "$dflt" ] && ACTIVE="$dflt"
    fi
    echo "ACTIVE=$ACTIVE"
    n=0
    for ifc in $ALL; do
      n=$((n+1)); [ "$n" -gt 4 ] && break
      res=$(probe "$ifc")
      cidr=$(ip -4 -o addr show dev "$ifc" 2>/dev/null | awk '{print $4}' | head -1)
      case "$ifc" in
        enx*|usb*|rndis*|wwan*) typ=USB共享 ;;
        *) if is_wifi "$ifc"; then typ=无线; else typ=有线; fi ;;
      esac
      echo "WAN$n=$ifc|${res%% *}|${res##* }|$cidr|$typ|$n"
    done
    echo "COUNT=$n"
    echo "LAST_SWITCH=${LAST_SWITCH:-}"; echo "LAST_REASON=${LAST_REASON:-}"
    echo "STATIC_IP=$STATIC_IP"; echo "STATIC_GW=$STATIC_GW"
    echo "FAIL_N=$FAIL_N"; echo "OK_N=$OK_N"; echo "CHECK_IP=$CHECK_IP"
    ;;
  tick) tick ;;
  switch)
    if [ "${2:-auto}" = "auto" ]; then
      ENABLE=0; resolve_wans; restore_system; ACTIVE=""; LAST_SWITCH=$(date '+%F %T'); LAST_REASON="交回系统"; save_state
      echo "已交回系统路由"
    else
      [ -d "/sys/class/net/${2:-}" ] || { echo "没这个网口：${2:-}"; exit 1; }
      resolve_wans
      cidr_t=$(ip -4 -o addr show dev "$2" 2>/dev/null | awk '{print $4}' | head -1)
      chk=$(probe "$2")
      # 校园网那条：探不通可能只是掉认证了，先认证一次再重探
      if [ "${chk%% *}" != "1" ] && [ "${cidr_t#10.149.}" != "$cidr_t" ]; then
        log "切到 $2 探不通，先尝试重新认证"
        [ -x /usr/local/bin/grayrouter-auth.sh ] && /usr/local/bin/grayrouter-auth.sh >/dev/null 2>&1
        sleep 3; chk=$(probe "$2")
      fi
      # 硬约束：手动切换前必须先探通，探不通直接拒绝，绝不把默认路由指到死路上
      if [ "${chk%% *}" != "1" ]; then
        log "拒绝手动切换到 $2（探测不通）"
        echo "拒绝切换：$2 现在探不通（ICMP 与 HTTP 204 都没通过），切过去会断网。"
        echo "先确认这条线真的能上网（手机开移动数据、热点开着），再切。"
        exit 3
      fi
      prev="$ACTIVE"
      set_active "$2" "手动指定"
      sleep 3
      vfy=$(probe "$2")
      if [ "${vfy%% *}" != "1" ]; then
        log "切到 $2 后验证失败，回滚"
        if [ -n "$prev" ] && [ "$prev" != "$2" ] && [ -d "/sys/class/net/$prev" ] && \
           [ "$(probe "$prev" | cut -d' ' -f1)" = "1" ]; then
          set_active "$prev" "切换失败回滚"; echo "切换后验证失败，已回滚到 $prev"; exit 4
        fi
        ENABLE=0; restore_system; ACTIVE=""; LAST_SWITCH=$(date '+%F %T'); LAST_REASON="切换失败回滚"; save_state
        echo "切换后验证失败，已交回系统路由"; exit 4
      fi
      echo "已切到 $2（延迟 ${chk##* } ms）"
    fi ;;
  set)
    [ "$(id -u)" -eq 0 ] || { echo "需要 root"; exit 1; }
    k="${2:-}"; v="${3:-}"; shift 2 2>/dev/null || true; v="${*:-$v}"
    mkdir -p /etc/grayrouter; [ -f "$CONF" ] || : > "$CONF"
    case "$k" in
      enable) ENABLE=1 ;; disable) ENABLE=0 ;;
      prefer) PREFER="${v:-auto}" ;;
      staticip) STATIC_IP="${v:-192.168.42.100}" ;;
      staticgw) STATIC_GW="${v:-192.168.42.129}" ;;
      wans)   WANS="$v" ;;
      prio)   PRIO="$v" ;;
      failn)  FAIL_N="${v:-3}" ;; okn) OK_N="${v:-2}" ;;
      checkip) CHECK_IP="${v:-223.5.5.5}" ;;
      *) echo "不认识的配置项：$k"; exit 1 ;;
    esac
    {
      echo "ENABLE=$ENABLE"
      echo "WANS=\"$WANS\""
      echo "PREFER=\"$PREFER\""
      echo "CHECK_IP=$CHECK_IP"
      echo "STATIC_IP=$STATIC_IP"
      echo "STATIC_GW=$STATIC_GW"
      echo "FAIL_N=$FAIL_N"
      echo "OK_N=$OK_N"
    } > "$CONF"
    echo "已更新 $k=${v:-$ENABLE}"
    ;;
  *) echo "用法: $0 get|tick|switch <iface|auto>|set <k> <v>"; exit 1 ;;
esac
