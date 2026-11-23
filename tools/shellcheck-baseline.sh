#!/usr/bin/env bash
# ============================================================
#  shellcheck-baseline.sh —— 阶段 3 的验证工具：改注释前后各跑一次，比对结果
#
#  用途：给「只改注释」这件事提供证据 —— 证明除了注释，可执行代码一行没动。
#
#  用法：
#    bash shellcheck-baseline.sh save   [目录]   记基线（默认仓库 bin/）
#    bash shellcheck-baseline.sh check  [目录]   复查，与基线比对
#
#  比对两层：
#    1. bash -n 语法结果（全部应为 OK）
#    2. 剥离注释后的代码指纹（sha256），必须与基线完全一致
# ============================================================
set -uo pipefail

MODE=${1:-check}
DIR=${2:-/d/Users/adminzyfy/Desktop/connectinternetplan/Ubuntuflie/bin}
BASE=${3:-/tmp/shellcheck-baseline.txt}

[ -d "$DIR" ] || { echo "目录不存在：$DIR"; exit 2; }

# 剥离注释和空行，只留可执行代码（粗粒度但足够证明"代码没动"）
code_fingerprint() {
  local f="$1"
  # 去掉整行注释、行尾注释（保守：不处理字符串内的 #），再去空行
  sed -E 's/[[:space:]]+#.*$//; s/^[[:space:]]*#.*$//' "$f" \
    | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' \
    | grep -v '^$' \
    | sha256sum | cut -d' ' -f1
}

run_one() {
  local f="$1"
  local syntax="OK"
  bash -n "$f" 2>/dev/null || syntax="FAIL"
  printf '%s|%s|%s\n' "$(basename "$f")" "$syntax" "$(code_fingerprint "$f")"
}

case "$MODE" in
  save)
    : > "$BASE"
    for f in "$DIR"/grayrouter-*.sh; do
      [ -f "$f" ] || continue
      run_one "$f" >> "$BASE"
    done
    echo "✓ 基线已保存：$BASE（$(wc -l < "$BASE") 个脚本）"
    ;;
  check)
    [ -f "$BASE" ] || { echo "先跑 save 建基线"; exit 2; }
    : > /tmp/shellcheck-now.txt
    for f in "$DIR"/grayrouter-*.sh; do
      [ -f "$f" ] || continue
      run_one "$f" >> /tmp/shellcheck-now.txt
    done

    echo "=== 语法检查 ==="
    if grep -q '|FAIL|' /tmp/shellcheck-now.txt; then
      echo "✗ 有脚本语法错误："
      grep '|FAIL|' /tmp/shellcheck-now.txt
    else
      echo "✓ 全部通过（$(wc -l < /tmp/shellcheck-now.txt) 个）"
    fi

    echo ""
    echo "=== 代码指纹比对（注释剥离后）==="
    CHANGED=0
    while IFS='|' read -r name syn fp; do
      old=$(grep "^${name}|" "$BASE" | cut -d'|' -f3)
      if [ -n "$old" ] && [ "$old" != "$fp" ]; then
        echo "  ⚠ $name：代码有变化（不只是注释）"
        CHANGED=$((CHANGED+1))
      fi
    done < /tmp/shellcheck-now.txt
    if [ "$CHANGED" -eq 0 ]; then
      echo "✓ 全部一致：注释之外的可执行代码没有任何改动"
    else
      echo "✗ $CHANGED 个脚本的代码有变化，需要核对"
    fi
    ;;
  *)
    echo "用法：bash $0 {save|check} [目录] [基线文件]"; exit 2 ;;
esac
