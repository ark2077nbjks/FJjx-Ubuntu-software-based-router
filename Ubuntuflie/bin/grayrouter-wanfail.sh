#!/usr/bin/env bash
# ============================================================
#  grayrrouter-wanfail.sh —— 双 WAN 故障转移（主挂切备，恢复切回）
#    设计参考 fidpa/linux-dual-wan-failover 的思路：
#      多目标探测 / 连续失败才判挂（防抖）/ 连续成功才切回 / 状态落盘 / 留日志
#
#    get              输出当前状态（KEY=VALUE，给面板读）
#    tick             一次探测 + 决策（systemd timer 每 15 秒跑）
#    set <k> <v>      改配置（enable / disable / prefer auto|primary|backup / primary <if> / backup <if>）
#    switch primary|backup   手动强制切换
# ============================================================
set -uo pipefail

CONF=/etc/grayrouter/wanfail.env
STATE=/run/grayrouter/wanfail.state
LOG=/var/log/grayrouter-wanfail.log
ROLES=/usr/local/bin/grayrouter-roles.sh

# ---------- 默认配置 ----------
ENABLE=0            # 默认关：要用户显式开
PRIMARY=""          # 空=自动（roles.sh 的 WAN_IF）
BACKUP=""           # 空=自动（找 usb*/enx*，其次其它 STA 无线口）
CHECK_IP=223.5.5.5  # 公网探测目标（阿里 DNS，国内稳）
GW_CHECK=0          # 探网关（校园网/部分网关不答 ICMP，默认关，只看公网通不通）
FAIL_N=3            # 连续失败几次判定挂了
OK_N=2              # 连续成功几次判定恢复
[ -f "$CONF" ] && . "$CONF" 2>/dev/null
mkdir -p /run/grayrouter 2>/dev/null

log() { echo "$(date '+%F %T') $*" >> "$LOG" 2>/dev/null; }

role_wan() { [ -x "$ROLES" ] && "$ROLES" get 2>/dev/null | sed -n 's/^WAN_IF=//p'; }

gw_of() {  # $1=网口 → 网关
  local g
  g=$(ip -4 route show default dev "$1" 2>/dev/null | awk '{print $3; exit}')
  [ -n "$g" ] && { echo "$g"; return; }
  g=$(nmcli -g IP4.GATEWAY dev show "$1" 2>/dev/null | head -1)
  echo "${g%% *}"
}

is_up() { [ -d "/sys/class/net/$1" ] && [ "$(cat "/sys/class/net/$1/operstate" 2>/dev/null)" != "down" ]; }

pick_backup() {  # 自动挑备线：手机 USB 共享优先，其次其它无线 STA
  local p="$1" n
  for n in $(ls /sys/class/net 2>/dev/null); do
    [ "$n" = "lo" ] && continue
    [ "$n" = "$p" ] && continue
    case "$n" in docker*|veth*|br-*|virbr*|wlp*) continue ;; esac
    if [ -e "/sys/class/net/$n/wireless" ]; then continue; fi
    [ -n "$(gw_of "$n")" ] && { echo "$n"; return; }
  done
  # 其次：非 AP 状态的无线口（USB 网卡当 STA 用）
  for n in $(iw dev 2>/dev/null | sed -n 's/^Interface //p'); do
    [ "$n" = "$p" ] && continue
    [ "$(iw dev "$n" info 2>/dev/null | awk '/type/{print $2}')" = "AP" ] && continue
    [ -n "$(gw_of "$n")" ] && { echo "$n"; return; }
  done
}

probe() {  # $1=网口 → 输出 "ok latency" ；只读探测，不改任何路由
  local ifc="$1" gw t0 t1 ms lip
  [ -z "$ifc" ] && { echo "0 -"; return; }
  is_up "$ifc" || { echo "0 -"; return; }
  gw=$(gw_of "$ifc")
  if [ "$GW_CHECK" = "1" ] && [ -n "$gw" ]; then
    if ! ping -I "$ifc" -c 1 -W 2 -q "$gw" >/dev/null 2>&1; then
      lip=$(ip -4 -o addr show dev "$ifc" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)
      [ -z "$lip" ] && { echo "0 -"; return; }
      ping -I "$lip" -c 1 -W 2 -q "$gw" >/dev/null 2>&1 || { echo "0 -"; return; }
    fi
  fi
  # 绑定网口需要 root；非 root 时退回「绑定源 IP」，效果一样
  lip=$(ip -4 -o addr show dev "$ifc" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)
  t0=$(date +%s%N)
  if ! ping -I "$ifc" -c 1 -W 2 -q "$CHECK_IP" >/dev/null 2>&1; then
    if ! ping -I "$lip" -c 1 -W 2 -q "$CHECK_IP" >/dev/null 2>&1; then
      # ICMP 被封时用 HTTP 204 兜底（204 无正文，最省流量）
      code=$(curl -s -m 4 -o /dev/null -w '%{http_code}' --interface "${lip:-$ifc}" \
                  http://connect.rom.miui.com/generate_204 2>/dev/null)
      [ "$code" = "204" ] || { echo "0 -"; return; }
    fi
  fi
  t1=$(date +%s%N)
  ms=$(( (t1 - t0) / 1000000 ))
  echo "1 $ms"
}

# ---------- 状态 ----------
load_state() {
  ACTIVE=primary; P_FAIL=0; B_FAIL=0; P_OK=0; B_OK=0; LAST_SWITCH=""; LAST_REASON=""
  [ -f "$STATE" ] && . "$STATE" 2>/dev/null
}
save_state() {
  printf 'ACTIVE=%s\nP_FAIL=%s\nB_FAIL=%s\nP_OK=%s\nB_OK=%s\nLAST_SWITCH=%s\nLAST_REASON=%s\n' \
    "$ACTIVE" "$P_FAIL" "$B_FAIL" "$P_OK" "$B_OK" "${LAST_SWITCH:-}" "${LAST_REASON:-}" > "$STATE" 2>/dev/null
}

resolve() {
  P_IF=${PRIMARY:-$(role_wan)}
  [ -z "${P_IF:-}" ] && P_IF=eno1
  B_IF=${BACKUP:-$(pick_backup "$P_IF")}
}

do_switch() {  # $1=primary|backup
  local want="$1" ifc gw
  resolve
  if [ "$want" = "backup" ]; then
    ifc="$B_IF"; gw=$(gw_of "$ifc")
    [ -z "$ifc" ] || [ -z "$gw" ] && { log "切备用失败：没有可用备线"; return 1; }
    ip route replace default via "$gw" dev "$ifc" metric 50 2>/dev/null || { log "切备用失败：写路由被拒"; return 1; }
    log "切到备用 $ifc（网关 $gw，metric 50）"
  else
    ifc="$P_IF"; gw=$(gw_of "$ifc")
    # 删掉我们自己加的备用默认路由，让 NM 的主线路由重新生效
    [ -n "$B_IF" ] && [ -n "$(gw_of "$B_IF")" ] && \
      ip route del default via "$(gw_of "$B_IF")" dev "$B_IF" metric 50 2>/dev/null
    log "切回主用 $ifc（网关 ${gw:-?}）"
  fi
  ACTIVE="$want"; LAST_SWITCH=$(date '+%F %T'); LAST_REASON="${2:-manual}"; save_state
}

tick() {
  [ "$ENABLE" = "1" ] || { echo "disabled"; return; }
  resolve
  [ -z "${B_IF:-}" ] && { echo "no-backup"; return; }

  local pr br
  pr=$(probe "$P_IF"); br=$(probe "$B_IF")
  P_OK_NOW=${pr%% *}; P_LAT=${pr##* }
  B_OK_NOW=${br%% *}; B_LAT=${br##* }

  if [ "$P_OK_NOW" = "1" ]; then P_FAIL=0; P_OK=$((P_OK+1)); else P_FAIL=$((P_FAIL+1)); P_OK=0; fi
  if [ "$B_OK_NOW" = "1" ]; then B_FAIL=0; B_OK=$((B_OK+1)); else B_FAIL=$((B_FAIL+1)); B_OK=0; fi

  if [ "$ACTIVE" = "primary" ] && [ "$P_FAIL" -ge "$FAIL_N" ] && [ "$B_OK" -ge "$OK_N" ]; then
    do_switch backup "主用连续 ${P_FAIL} 次探测失败"
  elif [ "$ACTIVE" = "backup" ] && [ "$P_OK" -ge "$OK_N" ]; then
    do_switch primary "主用恢复（连续 ${P_OK} 次成功）"
  fi
  save_state
  echo "primary=$P_OK_NOW/$P_LAT backup=$B_OK_NOW/$B_LAT active=$ACTIVE p_fail=$P_FAIL b_ok=$B_OK"
}

case "${1:-get}" in
  get)
    resolve; load_state
    pr=$(probe "$P_IF"); br=$(probe "${B_IF:-}")
    echo "ENABLE=$ENABLE"
    echo "PRIMARY=$P_IF"
    echo "BACKUP=${B_IF:-}"
    echo "ACTIVE=$ACTIVE"
    echo "P_OK=${pr%% *}"; echo "P_LAT=${pr##* }"
    echo "B_OK=${br%% *}"; echo "B_LAT=${br##* }"
    echo "LAST_SWITCH=${LAST_SWITCH:-}"
    echo "LAST_REASON=${LAST_REASON:-}"
    echo "FAIL_N=$FAIL_N"; echo "OK_N=$OK_N"; echo "CHECK_IP=$CHECK_IP"
    ;;
  tick) tick ;;
  switch)
    case "${2:-}" in
      primary|backup) do_switch "${2}" "manual" && echo "已切换到 ${2}" ;;
      *) echo "用法: $0 switch primary|backup"; exit 1 ;;
    esac ;;
  set)
    [ "$(id -u)" -eq 0 ] || { echo "需要 root"; exit 1; }
    k="${2:-}"; v="${3:-}"
    mkdir -p /etc/grayrouter
    [ -f "$CONF" ] || : > "$CONF"
    case "$k" in
      enable)  ENABLE=1 ;;
      disable) ENABLE=0 ;;
      primary) PRIMARY="$v" ;;
      backup)  BACKUP="$v" ;;
      failn)   FAIL_N="${v:-3}" ;;
      okn)     OK_N="${v:-2}" ;;
      checkip) CHECK_IP="${v:-223.5.5.5}" ;;
      *) echo "不认识的配置项：$k"; exit 1 ;;
    esac
    # 重写整份配置（简单可靠）
    [ "$k" = "enable" ] && ENABLE=1
    [ "$k" = "disable" ] && ENABLE=0
    {
      echo "ENABLE=$ENABLE"
      echo "PRIMARY=\"$PRIMARY\""
      echo "BACKUP=\"$BACKUP\""
      echo "CHECK_IP=$CHECK_IP"
      echo "GW_CHECK=$GW_CHECK"
      echo "FAIL_N=$FAIL_N"
      echo "OK_N=$OK_N"
    } > "$CONF"
    echo "已更新 $k=$v"
    ;;
  *) echo "用法: $0 get|tick|switch primary|backup|set <k> <v>"; exit 1 ;;
esac
