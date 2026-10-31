#!/bin/bash
# ============================================================
#  grayrouter-restore.sh —— 从备份包恢复配置
#
#  用法：grayrouter-restore.sh <备份包.tar.gz>
#
#  安全设计：
#   1. 只接受白名单路径（etc-grayrouter/、dnsmasq-shared.d/、connections/、meta/）
#      包里有任何别的路径 → 整包拒绝，不解包
#   2. 含 .. 的路径直接拒绝（防目录穿越）
#   3. 恢复前自动把当前配置再备份一份（pre-restore）
#   4. 恢复后跑自检：SQM 服务 + 整形状态 + 网卡清单
#
#  回滚：恢复前的备份包路径会打印出来，再恢复它即回到原状
# ============================================================
set -uo pipefail

SRC=${1:-}
LOG=/var/log/grayrouter-restore.log
log() { printf '%s %s\n' "$(date '+%F %T')" "$*" >> "$LOG" 2>/dev/null || true; }

[ -n "$SRC" ] || { echo "用法：grayrouter-restore.sh <备份包.tar.gz>"; exit 1; }
[ -f "$SRC" ] || { echo "× 文件不存在：$SRC"; exit 1; }
[ "$(id -u)" = "0" ] || { echo "需要 root：sudo bash $0 $SRC"; exit 1; }

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT

tar tzf "$SRC" > "$TMP/list.txt" 2>/dev/null || { echo "× 不是合法的 tar.gz"; exit 2; }

if grep -q '\.\.' "$TMP/list.txt"; then
  echo "× 包里含 .. 路径，拒绝恢复（防目录穿越）"; log "拒绝 .. : $SRC"; exit 3
fi

BAD=$(grep -Ev '^\./?(etc-grayrouter(/|$)|dnsmasq-shared\.d(/|$)|connections(/|$)|meta(/|$))|^\./?$' "$TMP/list.txt" || true)
if [ -n "$BAD" ]; then
  echo "× 包里出现白名单之外的路径，拒绝恢复："
  printf '%s\n' "$BAD" | head -5
  log "拒绝白名单外路径: $(printf '%s' "$BAD" | head -3 | tr '\n' ' ')"
  exit 3
fi

tar xzf "$SRC" -C "$TMP" 2>/dev/null || { echo "× 解包失败"; exit 4; }

echo "① 恢复前先备份当前配置…"
# 备份脚本可能只在用户家目录（面板「导出配置备份」用的就是它）
BACKUP_BIN=""
for c in /usr/local/bin/grayrouter-backup.sh /usr/bin/grayrouter-backup.sh; do
  [ -x "$c" ] && { BACKUP_BIN=$c; break; }
done
if [ -z "$BACKUP_BIN" ]; then
  BACKUP_BIN=$(ls -1t /home/*/grayrouter-backup.sh 2>/dev/null | head -1)
fi
PRE=""; BMSG=""
if [ -n "$BACKUP_BIN" ]; then
  mkdir -p /var/lib/grayrouter/backups
  BMSG=$(bash "$BACKUP_BIN" /var/lib/grayrouter/backups 2>&1 | tail -2)
  case "$BMSG" in
    */grayrouter-backup-*.tar.gz) PRE=$(printf '%s' "$BMSG" | tail -1) ;;
  esac
fi
if [ -n "$PRE" ]; then
  echo "  已备份：$PRE"
else
  echo "  ! 恢复前备份失败：${BMSG:-找不到 grayrouter-backup.sh}"
  echo "    继续恢复，但回滚只能靠 /var/lib/grayrouter/snapshots 里的旧快照"
fi

if [ -d "$TMP/etc-grayrouter" ]; then
  mkdir -p /etc/grayrouter
  cp -a "$TMP/etc-grayrouter/." /etc/grayrouter/ 2>/dev/null || true
  chown -R root:root /etc/grayrouter 2>/dev/null || true
  find /etc/grayrouter -type f -exec chmod 644 {} \; 2>/dev/null || true
  echo "② /etc/grayrouter 已恢复（$(ls -1 "$TMP/etc-grayrouter" | wc -l) 项，属主已校正为 root）"
else
  echo "② 包里没有 etc-grayrouter，跳过"
fi

if [ -d "$TMP/dnsmasq-shared.d" ]; then
  mkdir -p /etc/NetworkManager/dnsmasq-shared.d
  cp -a "$TMP/dnsmasq-shared.d/." /etc/NetworkManager/dnsmasq-shared.d/ 2>/dev/null || true
  chown -R root:root /etc/NetworkManager/dnsmasq-shared.d 2>/dev/null || true
  find /etc/NetworkManager/dnsmasq-shared.d -type f -exec chmod 644 {} \; 2>/dev/null || true
  echo "③ 静态租约已恢复"
else
  echo "③ 包里没有静态租约，跳过"
fi

echo "④ 重启 SQM 服务…"
if systemctl restart grayrouter-sqm.service >/dev/null 2>&1; then
  echo "  ✓ grayrouter-sqm.service 已重启"
else
  echo "  ! SQM 服务重启失败（可能本来没装，不影响上网）"
fi

echo "--- 自检 ---"
if systemctl is-active grayrouter-sqm.service >/dev/null 2>&1; then echo "SQM 服务：active"; else echo "SQM 服务：未运行"; fi
bash /usr/local/bin/grayrouter-sqm.sh status 2>/dev/null | head -2
tc qdisc show dev eno1 2>/dev/null | head -1 | cut -c1-90
ip -br addr show 2>/dev/null | awk '$1!="lo"{print "网卡："$0}' | head -4

echo
echo "完成。要回到恢复前的状态，用下面这个包再恢复一次："
if [ -n "$PRE" ]; then echo "  sudo bash /usr/local/bin/grayrouter-restore.sh $PRE";
else echo "  （刚才的备份没成功，请手动检查 /etc/grayrouter）"; fi
log "restore ok: $SRC (pre=$PRE)"
