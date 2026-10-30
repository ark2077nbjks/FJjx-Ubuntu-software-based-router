#!/bin/bash
# ============================================================
#  grayrouter-drift.sh —— 配置漂移检测
#
#  对比「最近一次快照」与「当前配置」，列出被改动/新增的文件。
#  只读，不改任何东西。
#
#  用法：grayrouter-drift.sh [快照名]
# ============================================================
set -uo pipefail

DIR=/var/lib/grayrouter/snapshots
WANT=${1:-}
if [ -n "$WANT" ]; then
  case "$WANT" in */*) F="$WANT" ;; *) F="$DIR/$WANT.tar.gz" ;; esac
else
  F=$(ls -1t "$DIR"/*.tar.gz 2>/dev/null | head -1)
fi

[ -n "${F:-}" ] && [ -f "$F" ] || { echo "× 还没有快照。先在面板点「保存快照」，之后就能比对漂移"; exit 1; }

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
tar xzf "$F" -C "$TMP" 2>/dev/null || { echo "× 快照解包失败：$F"; exit 2; }

echo "对比基准：$(basename "$F" .tar.gz)"
n=0

while IFS= read -r p; do
  case "$p" in */) continue ;; esac          # 目录跳过
  [ -f "$TMP$p" ] || continue
  if [ ! -e "$p" ]; then
    echo "已删除：$p"; n=$((n+1))
  elif ! cmp -s "$TMP$p" "$p" 2>/dev/null; then
    echo "已改动：$p"; n=$((n+1))
  fi
done < <(tar tzf "$F" 2>/dev/null)

# 当前多出来的文件（快照里没有）——只看我们自己的配置目录
for d in /etc/grayrouter /etc/NetworkManager/dnsmasq-shared.d; do
  [ -d "$d" ] || continue
  for p in "$d"/*; do
    [ -f "$p" ] || continue
    [ -e "$TMP$p" ] || { echo "新增：$p"; n=$((n+1)); }
  done
done

if [ "$n" -eq 0 ]; then
  echo "✓ 当前配置与快照一致，没有漂移"
else
  echo "---"
  echo "共 $n 处差异。要回滚可以点「恢复快照」（会在切换前自动再保存一份）"
fi
