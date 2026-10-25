#!/usr/bin/env bash
# ============================================================
#  ua3f-rules.sh —— 伪装层引流规则（TPROXY）
#  把 LAN 客户端的 TCP 流量导到本机 1080，交给 UA3F 改 IPID
#  用法：sudo bash ua3f-rules.sh apply | clear | status
#  只碰自己的表 inet gray_ua3f，不动 gray_ttl
# ============================================================
set -uo pipefail

TABLE=gray_ua3f
FWMARK=0x1c9
ROUTE_TABLE=457
PORT=1080
CFG=/etc/grayrouter/config.env

[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash $0 $*"; exit 1; }

do_clear() {
  nft delete table ip "$TABLE" 2>/dev/null || true
  # 可能有多条同名规则，循环删干净
  for _ in 1 2 3; do
    ip rule del fwmark "$FWMARK" lookup "$ROUTE_TABLE" 2>/dev/null || break
  done
  ip route flush table "$ROUTE_TABLE" 2>/dev/null || true
}

do_apply() {
  command -v nft >/dev/null || { echo "没装 nftables"; exit 1; }
  do_clear

  nft -f - <<EOF
table ip $TABLE {
  chain prerouting {
    type filter hook prerouting priority mangle; policy accept;
    iifname "lo" return
    ip daddr { 0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4, 240.0.0.0/4 } return
    tcp dport $PORT return
    meta l4proto tcp meta mark set $FWMARK tproxy to :$PORT accept
  }
}
EOF
  if [ $? -ne 0 ]; then
    echo "规则加载失败（内核可能缺 nf_tproxy）；跳过伪装层，不影响上网"
    exit 2
  fi

  ip rule add fwmark "$FWMARK" lookup "$ROUTE_TABLE" 2>/dev/null || true
  ip route add local 0.0.0.0/0 dev lo table "$ROUTE_TABLE" 2>/dev/null || true
  echo "伪装规则已加载（mark $FWMARK → 表 $ROUTE_TABLE → 127.0.0.1:$PORT）"
}

do_status() {
  if nft list table ip "$TABLE" >/dev/null 2>&1; then
    R=$(nft list table ip "$TABLE" 2>/dev/null | grep -c "tproxy")
    echo "规则表: 存在（tproxy 规则 $R 条）"
  else
    echo "规则表: 不存在"
  fi
  ip rule show 2>/dev/null | grep -q "fwmark $FWMARK" && echo "策略路由: 已配" || echo "策略路由: 未配"
  ss -lnt 2>/dev/null | grep -q ":$PORT " && echo "1080 监听: 在" || echo "1080 监听: 无"
}

case "${1:-apply}" in
  apply)  do_apply ;;
  clear)  do_clear; echo "伪装规则已清除" ;;
  status) do_status ;;
  *) echo "用法: $0 apply|clear|status"; exit 2 ;;
esac
