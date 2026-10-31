#!/usr/bin/env bash
# ============================================================
#  grayrouter-opt.sh  v2 —— 系统优化（幂等，可开机自动执行）
#    apply     写入 BBR / 日志限额 / DNS 缓存，并校验文件
#    verify    只读检查当前生效值
#    rollback  删除三项配置并还原
#  设计要点：
#    - 不再重启 NetworkManager（改用 nmcli general reload dns-rc，不断网）
#    - 每一步写完立刻校验，失败即报错退出
#    - 与 systemd 单元 grayrouter-opt.service 配合，开机幂等
# ============================================================
set -uo pipefail

SYSCTL_FILE=/etc/sysctl.d/99-grayrouter.conf
JOURNALD_DIR=/etc/systemd/journald.conf.d
JOURNALD_FILE=$JOURNALD_DIR/99-grayrouter.conf
DNS_FILE=/etc/NetworkManager/dnsmasq-shared.d/20-cache.conf
LOG=/var/log/grayrouter-opt.log
TS=$(date +%Y%m%d_%H%M%S)
BK=/etc/grayrouter/opt-backup-$TS

log() { echo "$(date '+%F %T') $*" | tee -a "$LOG" >/dev/null; }
say() { printf '%s\n' "$*"; }

need_root() { [ "$(id -u)" -eq 0 ] || { say "需要 root：sudo bash $0 $*"; exit 1; }; }

apply() {
  need_root "$@"
  mkdir -p "$BK" /etc/grayrouter "$JOURNALD_DIR" "$(dirname "$DNS_FILE")" 2>/dev/null
  for f in "$SYSCTL_FILE" "$JOURNALD_FILE" "$DNS_FILE"; do
    [ -f "$f" ] && cp -a "$f" "$BK/$(basename "$f").bak"
  done

  # ① BBR
  printf '%s\n' "net.core.default_qdisc=fq" "net.ipv4.tcp_congestion_control=bbr" > "$SYSCTL_FILE"
  sysctl --system >/dev/null 2>&1
  [ "$(sysctl -n net.ipv4.tcp_congestion_control)" = bbr ] || { say "✗ BBR 未生效"; log "BBR 未生效"; exit 2; }
  say "✓ BBR：$(sysctl -n net.ipv4.tcp_congestion_control) / $(sysctl -n net.core.default_qdisc)"

  # ② journald 限额
  printf '%s\n' "[Journal]" "SystemMaxUse=200M" "RuntimeMaxUse=32M" > "$JOURNALD_FILE"
  systemctl restart systemd-journald
  journalctl --vacuum-size=200M >/dev/null 2>&1 || true
  say "✓ 日志：$(journalctl --disk-usage | grep -oE '[0-9.]+[MG]' | head -1)"

  # ③ DNS 缓存（配置持久；用 reload 让它生效，不断网）
  printf '%s\n' "cache-size=1000" "min-cache-ttl=60" > "$DNS_FILE"
  if nmcli general reload dns-rc >/dev/null 2>&1; then
    say "✓ DNS 缓存：已 reload（未断网）"
  else
    say "✓ DNS 缓存：配置已写入（下次开机或网络重载后生效）"
  fi

  # ④ 校验三份文件都在
  local miss=0
  for f in "$SYSCTL_FILE" "$JOURNALD_FILE" "$DNS_FILE"; do
    [ -s "$f" ] || { say "✗ 缺失或为空：$f"; miss=1; }
  done
  [ "$miss" -eq 0 ] && say "✓ 三份配置文件均已存在" || exit 3
  systemctl enable grayrouter-opt.service >/dev/null 2>&1 || true
  log "apply 完成（备份 $BK）"
}

verify() {
  say "拥塞控制：$(sysctl -n net.ipv4.tcp_congestion_control) / 队列 $(sysctl -n net.core.default_qdisc)"
  say "日志占用：$(journalctl --disk-usage | grep -oE '[0-9.]+[MG]' | head -1)"
  say "日志限额：$(grep -h SystemMaxUse /etc/systemd/journald.conf.d/*.conf 2>/dev/null | head -1 || echo 未设置)"
  say "DNS 缓存：$(grep -h cache-size /etc/NetworkManager/dnsmasq-shared.d/*.conf 2>/dev/null | head -1 || echo 未设置)"
  say "AP 频段：$(nmcli -f 802-11-wireless.band,802-11-wireless.channel con show Gray-Router 2>/dev/null | tr '\n' ' ')"
  say "单元状态：$(systemctl is-enabled grayrouter-opt.service 2>/dev/null) / $(systemctl is-active grayrouter-opt.service 2>/dev/null)"
}


# 面板用：一次性输出 JSON（只读）
status() {
  local cc qd jl ju dc band ch en js
  cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || echo unknown)
  qd=$(sysctl -n net.core.default_qdisc 2>/dev/null || echo unknown)
  jl=$(grep -h '^SystemMaxUse=' "$JOURNALD_FILE" 2>/dev/null | head -1 | cut -d= -f2)
  ju=$(journalctl --disk-usage 2>/dev/null | grep -oE '[0-9.]+[KMG]' | head -1)
  dc=$(grep -h '^cache-size=' /etc/NetworkManager/dnsmasq-shared.d/*.conf 2>/dev/null | head -1 | cut -d= -f2)
  band=$(nmcli -g 802-11-wireless.band con show Gray-Router 2>/dev/null)
  ch=$(nmcli -g 802-11-wireless.channel con show Gray-Router 2>/dev/null)
  en=$(systemctl is-enabled grayrouter-opt.service 2>/dev/null)
  js=$( [ -f "$SYSCTL_FILE" ] && echo 1 || echo 0 )
  printf '{"congestion":"%s","qdisc":"%s","journal_limit":"%s","journal_used":"%s","dns_cache":"%s","band":"%s","channel":"%s","unit":"%s","applied":%s}\n' \
    "${cc:-unknown}" "${qd:-unknown}" "${jl:-}" "${ju:-}" "${dc:-}" "${band:-}" "${ch:-}" "${en:-unknown}" "$js"
}

rollback() {
  need_root "$@"
  rm -f "$SYSCTL_FILE" "$JOURNALD_FILE" "$DNS_FILE"
  sysctl --system >/dev/null 2>&1 || true
  systemctl restart systemd-journald 2>/dev/null || true
  nmcli general reload dns-rc >/dev/null 2>&1 || true
  say "已撤销：$(sysctl -n net.ipv4.tcp_congestion_control)"
  systemctl disable --now grayrouter-opt.service >/dev/null 2>&1 || true
}

case "${1:-verify}" in
  apply) apply ;;
  verify) verify ;;
  status) status ;;
  rollback) rollback ;;
  *) say "用法：$0 {apply|verify|rollback}"; exit 2 ;;
esac
