#!/usr/bin/env bash
# 校园网重新认证（面板按钮用）：以面板配置的那个用户的身份跑 bitsrun
set -uo pipefail
U=""
[ -f /etc/grayrouter/ctl.conf ] && U=$(sed -n 's/^GR_USER="\{0,1\}\([^"]*\)"\{0,1\}$/\1/p' /etc/grayrouter/ctl.conf | head -1)
[ -z "$U" ] && U=root
WAN=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null | sed -n 's/^WAN_IF=//p')
[ -z "$WAN" ] && WAN=eno1
H=$(getent passwd "$U" | cut -d: -f6)
if [ ! -x "$H/bitsrun" ]; then echo "没找到 bitsrun（$H/bitsrun）"; exit 1; fi
echo "以 $U 身份认证，网口 $WAN"
runuser -u "$U" -- bash -lc "cd '$H' && ./bitsrun -config Config.yaml -interface $WAN" 2>&1 | tail -6
