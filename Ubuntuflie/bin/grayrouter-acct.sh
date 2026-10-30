#!/usr/bin/env bash
# ============================================================
#  grayrouter-acct.sh —— 每设备流量计数表（nft 动态集合 + 计数器）
#    apply    按当前 LAN 网段重建计数表（开机/安装时跑）
#    status   看当前各 IP 的累计字节
#    reset    清零计数器（不动历史 CSV）
#  原理：forward 链上一条规则，动态集合里每个 IP 自带 counter；
#        由 traffic.py 每小时读一次算差值，写 CSV。只读计数，不改转发行为。
# ============================================================
set -uo pipefail
TBL=gray_acct

# 当前所有 LAN 网段（NM 里 ipv4.method=shared 的连接）
lan_cidrs() {
  local name dev type m addr cidr out=""
  while IFS=: read -r name dev type; do
    [ -n "${dev:-}" ] || continue
    case "$type" in 802-11-wireless|802-3-ethernet) ;; *) continue ;; esac
    m=$(nmcli -g ipv4.method con show "$name" 2>/dev/null | head -1)
    [ "$m" = "shared" ] || continue
    addr=$(ip -4 -o addr show dev "$dev" 2>/dev/null | awk '{print $4; exit}')
    [ -n "$addr" ] || continue
    cidr=$(python3 -c "import ipaddress,sys;print(ipaddress.ip_interface(sys.argv[1]).network)" "$addr" 2>/dev/null)
    [ -n "$cidr" ] && out="$out $cidr"
  done < <(nmcli -t -f NAME,DEVICE,TYPE con show --active 2>/dev/null)
  echo "$out"
}

do_apply() {
  local cidrs list
  cidrs=$(lan_cidrs)
  [ -z "${cidrs// /}" ] && cidrs="192.168.50.0/24"
  list=$(echo $cidrs | tr ' ' ',')
  # 表已存在且网段没变 → 不重建，避免计数器归零（丢掉一段流量）
  if nft list table inet "$TBL" >/dev/null 2>&1; then
    cur=$(nft list table inet "$TBL" 2>/dev/null | grep -oE '192\.168\.[0-9]+\.0/[0-9]+' | sort -u | paste -sd, -)
    if [ "$cur" = "$list" ]; then
      echo "√ 计数表已存在、网段一致，未重建（计数保持连续）"
      return 0
    fi
  fi
  nft delete table inet "$TBL" 2>/dev/null || true
  nft -f - <<EOF || { echo "× 计数表建失败"; exit 1; }
table inet $TBL {
  set up4   { type ipv4_addr; flags dynamic; size 1024; }
  set down4 { type ipv4_addr; flags dynamic; size 1024; }
  chain acctfwd {
    type filter hook forward priority 10; policy accept;
    ip saddr { $list } ip daddr != { $list } update @up4   { ip saddr counter }
    ip daddr { $list } ip saddr != { $list } update @down4 { ip daddr counter }
  }
}
EOF
  echo "√ 计数表已建，网段：$list"
}

case "${1:-status}" in
  apply)  [ "$(id -u)" -eq 0 ] || { echo "需要 root"; exit 1; }; do_apply ;;
  status) nft list table inet "$TBL" 2>/dev/null || echo "（还没有计数表，先跑 apply）" ;;
  reset)  nft reset table inet "$TBL" >/dev/null 2>&1 && echo "计数器已清零" || echo "清零失败" ;;
  *) echo "用法：sudo bash $0 apply|status|reset"; exit 1 ;;
esac
