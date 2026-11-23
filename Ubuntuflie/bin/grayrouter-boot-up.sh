#!/usr/bin/env bash
# ============================================================
#  grayrouter-boot-up.sh —— 开机自动恢复软路由（由 systemd 在开机时调用）
#  用法：grayrouter-boot-up.sh（无参数，由 grayrrouter-boot.service 触发）
#  依赖：NetworkManager / iproute2 / router-up.sh
#  退出码：恒返回 0（失败只写日志，不阻塞开机）
#  注：只在 /etc/grayrouter/boot-mode=router 时动作；已有默认路由则跳过；会动网络
# ============================================================
# 开机自动恢复软路由：只有 /etc/grayrouter/boot-mode = router 时才动作
set -u
MARK=/etc/grayrouter/boot-mode
LOG=/var/log/grayrouter-boot.log
log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

mode=$(cat "$MARK" 2>/dev/null || echo "")
if [ "$mode" != "router" ]; then
    log "boot-mode=${mode:-空}，跳过（热点模式不碰网络）"
    exit 0
fi

# 等 NetworkManager 就绪
for _ in $(seq 1 20); do
    nmcli -t general status 2>/dev/null | grep -q 'connected' && break
    sleep 2
done

# 已经有默认路由就不重复折腾（NM 自己连上了）
if ip route show default 2>/dev/null | grep -q .; then
    log "已有默认路由，跳过 router-up"
    exit 0
fi

# 找 router-up.sh：优先安装位置，其次任一用户家目录里的副本（不写死用户名）
RUP=""
for c in /opt/grayrouter/bin/router-up.sh /usr/local/bin/router-up.sh; do
    [ -x "$c" ] && RUP="$c" && break
done
if [ -z "$RUP" ]; then
    for h in /home/*; do
        if [ -x "$h/router-up.sh" ]; then
            RUP="$h/router-up.sh"
            HOME="$h"
            break
        fi
    done
fi
if [ -z "$RUP" ]; then
    log "找不到 router-up.sh，跳过"
    exit 0
fi

log "boot-mode=router，执行 $RUP"
HOME="${HOME:-/root}" bash "$RUP" >>"$LOG" 2>&1
rc=$?
log "$RUP 退出码 $rc（0=正常，非 0 见 /var/log/grayrouter-boot.log 与软路由日志）"
exit 0
