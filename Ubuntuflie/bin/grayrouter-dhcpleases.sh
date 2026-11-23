#!/usr/bin/env bash
# ============================================================
#  grayrouter-dhcpleases.sh —— 列出当前 DHCP 租约（到期时间 IP MAC 主机名）
#  用法：grayrouter-dhcpleases.sh（无参数，只读）
#  依赖：date、NetworkManager 租约文件
#  退出码：0=成功（无租约也是 0）
#  注：租约文件 root 600，必须 root 跑；面板经 ctl 以 root 调用；只读不改任何东西
# ============================================================
# 列出 NetworkManager(dnsmasq) 当前的 DHCP 租约：到期时间 IP MAC 主机名
# 租约文件是 root 600，必须由 root 跑（面板通过 grayrrouter-ctl 以 root 调）
set -u
FILES=$(ls /var/lib/NetworkManager/*.leases /var/lib/misc/*.leases 2>/dev/null)
if [ -z "$FILES" ]; then echo "没有租约文件"; exit 0; fi
now=$(date +%s)
found=0
for f in $FILES; do
  while read -r exp mac ip name _; do
    case "${exp:-}" in ''|*[!0-9]*) continue ;; esac
    [ "$exp" -le "$now" ] && continue
    found=1
    ts=$(date -d "@$exp" '+%m-%d %H:%M' 2>/dev/null || echo "$exp")
    printf '%s\t%s\t%s\t%s\n' "$ip" "$mac" "${name:-*}" "$ts"
  done < "$f"
done
[ "$found" = "1" ] || echo "当前没有有效租约"
