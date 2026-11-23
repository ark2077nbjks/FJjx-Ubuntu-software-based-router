#!/usr/bin/env bash
# ============================================================
#  grayrouter-auth.sh —— 校园网重新认证（面板按钮用）
#  用法：grayrouter-auth.sh（无参数）
#  依赖：bitsrun（在用户家目录，Config.yaml 同目录）
#  退出码：0=成功 1=找不到 bitsrun
#  注：以 ctl.conf 里 GR_USER 的身份跑 bitsrun；不带参数、没有子命令；跑一次=检查+登录一次
# ============================================================
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
