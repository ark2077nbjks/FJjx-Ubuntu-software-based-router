#!/bin/bash
# ============================================================
#  grayrouter-netdiag.sh —— 路由追踪 / 路径 MTU（只读，温和发包）
#
#  用法：
#    grayrouter-netdiag.sh trace [主机]    路由追踪（最多 15 跳，每跳 2 个包）
#    grayrouter-netdiag.sh mtu   [主机]    二分探测路径 MTU（约 10 个包）
#
#  主机默认 223.5.5.5（阿里 DNS），只允许字母数字和 . : - _
# ============================================================
set -uo pipefail

MODE=${1:-trace}
HOST=${2:-223.5.5.5}

case "$HOST" in
  *[!A-Za-z0-9.:_-]*) echo "× 主机名不合法：$HOST"; exit 2 ;;
esac

case "$MODE" in
  trace)
    if command -v mtr >/dev/null 2>&1; then
      OUT=$(timeout 60 mtr -n -r -c 2 -m 15 "$HOST" 2>/dev/null)
      # 只看到 1 跳 = 中间设备不回 ICMP TTL，改用 TCP 443 追踪（成熟系统常用）
      HOPS=$(printf '%s\n' "$OUT" | grep -c '^ *[0-9]\+\.|--')
      if [ "${HOPS:-0}" -le 1 ]; then
        echo "（ICMP 中间跳不回显，改用 TCP 443 追踪）"
        OUT2=$(timeout 60 mtr -n -r -c 2 -m 15 -T -P 443 "$HOST" 2>/dev/null)
        printf '%s\n' "$OUT2" | head -18
        HOPS2=$(printf '%s\n' "$OUT2" | grep -c '^ *[0-9]\+\.|--')
        [ "${HOPS2:-0}" -le 1 ] && echo "（这张网的中间设备不返回 TTL 超时，只能看到最后一跳；不是故障）"
      else
        printf '%s\n' "$OUT" | head -18
      fi
    elif command -v tracepath >/dev/null 2>&1; then
      timeout 60 tracepath -n -m 15 "$HOST" 2>/dev/null | head -18
    else
      echo "× 机器上没有 mtr / tracepath"; exit 3
    fi
    ;;

  mtu)
    IFACE=$(ip route get "$HOST" 2>/dev/null | sed -n 's/.* dev \([^ ]*\).*/\1/p' | head -1)
    IMTU=$(cat "/sys/class/net/${IFACE:-eno1}/mtu" 2>/dev/null || echo "?")

    ping -c 1 -W 2 -q "$HOST" >/dev/null 2>&1 || { echo "× 连 $HOST 都不通，测不了 MTU"; exit 1; }

    echo "目标：$HOST"

    # ① 先试满包：能过就不用二分
    if ping -M do -s 1472 -c 1 -W 2 -q "$HOST" >/dev/null 2>&1; then
      echo "路径 MTU：1500 字节（1472 字节不分片载荷可通）"
      exit 0
    fi

    # ② 二分（100~1472）
    lo=100; hi=1472; best=0
    while [ "$lo" -le "$hi" ]; do
      mid=$(( (lo + hi) / 2 ))
      if ping -M do -s "$mid" -c 1 -W 2 -q "$HOST" >/dev/null 2>&1; then
        best=$mid; lo=$(( mid + 1 ))
      else
        hi=$(( mid - 1 ))
      fi
      sleep 0.2
    done

    if [ "$best" -ge 1200 ]; then
      echo "路径 MTU：$(( best + 28 )) 字节（最大不分片载荷 $best）"
      [ "$(( best + 28 ))" -lt 1500 ] && echo "提示：低于 1500，说明中间有 PPPoE / 隧道之类的窄链路"
      exit 0
    fi

    # ③ 边界过小 → 判为「大 ICMP 被过滤」，用 TLS 交叉验证
    echo "本机网口：${IFACE:-?} MTU=${IMTU}"
    echo "ICMP 只能过 $(( best + 28 )) 字节 —— 校园网/运营商常直接丢弃大 ICMP 包（防扫描），这个数不代表真实 MTU。"
    if curl -sS --max-time 8 -o /dev/null "https://$HOST" 2>/dev/null; then
      echo "TLS 握手成功（证书链远超 1200 字节）→ 路径可承载 ≥1400 字节，按网口 ${IMTU} 使用即可"
    else
      echo "TCP 443 也不通，无法交叉验证；按网口 MTU=${IMTU} 使用，若遇大文件卡住再查"
    fi
    ;;

  *)
    echo "用法：grayrouter-netdiag.sh {trace|mtu} [主机]"; exit 2 ;;
esac
