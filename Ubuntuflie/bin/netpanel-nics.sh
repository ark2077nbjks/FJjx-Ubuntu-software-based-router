#!/usr/bin/env bash
# netpanel-nics.sh —— 远端网卡清单（给 Windows 面板读）
# 输出：NIC|名字|状态|速率Mb/s|驱动|总线(pci/usb)|型号|IPv4|角色(WAN/LAN/空)
# 只读，不改任何系统设置；不需要 sudo
set -u

wan_if=$(ip -4 route show default 2>/dev/null | awk '{print $5; exit}')
lan_if=$(ip -4 -o addr show 2>/dev/null | awk '$4 ~ /^192\.168\.50\.1\//{print $2; exit}')

for d in /sys/class/net/*; do
    n=$(basename "$d")
    case "$n" in
        lo|docker*|veth*|br-*|virbr*|tun*|tap*) continue ;;
    esac

    st=$(cat "$d/operstate" 2>/dev/null || echo unknown)
    spd=$(cat "$d/speed" 2>/dev/null || echo "")
    case "$spd" in ''|*[!0-9]*) spd="" ;; *) [ "$spd" -le 0 ] && spd="" ;; esac

    drv=$(basename "$(readlink -f "$d/device/driver" 2>/dev/null)" 2>/dev/null || true)

    bus=""
    model=""
    props=$(udevadm info -q property -p "$d" 2>/dev/null || true)
    bus=$(printf '%s\n' "$props" | sed -n 's/^ID_BUS=//p' | head -1)
    model=$(printf '%s\n' "$props" | sed -n 's/^ID_MODEL_FROM_DATABASE=//p' | head -1)
    if [ -z "$model" ]; then
        model=$(printf '%s\n' "$props" | sed -n 's/^ID_MODEL=//p' | head -1)
    fi

    ip4=$(ip -4 -o addr show dev "$n" 2>/dev/null | awk '{print $4}' | paste -sd, -)
    [ -z "$ip4" ] && ip4="-"

    role=""
    [ "$n" = "$wan_if" ] && role="WAN"
    [ "$n" = "$lan_if" ] && role="LAN"

    printf 'NIC|%s|%s|%s|%s|%s|%s|%s|%s\n' "$n" "$st" "$spd" "$drv" "$bus" "$model" "$ip4" "$role"
done

# 虚拟网卡数量（docker / veth 之类，界面只显示个数，不列表）
vc=$(ls -d /sys/class/net/* 2>/dev/null | wc -l)
pc=$(( vc - 1 ))
vcount=$(ls /sys/class/net 2>/dev/null | grep -cE '^(docker|veth|br-|virbr|tun|tap)' || true)
printf 'VIRTUAL|%s\n' "$vcount"
