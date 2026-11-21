#!/usr/bin/env bash
# ============================================================
#  pre-push-check.sh —— 推送前自检
#
#  用法：bash tools/pre-push-check.sh
#
#  查六项：
#    1. 硬编码密码/密钥（排除变量拼接，那是正常代码）
#    2. 无关项目提及（QQ bot / 灰风 / NapCat / OneBot）
#    3. 真实本地路径
#    4. 公网 IP（排除私有段、特殊段、已知公共服务 IP）
#    5. 大文件（>1MB，白名单内的二进制/部署包不算问题）
#    6. 工作区未提交改动
#
#  退出码：0 = 可以推，1 = 有 FAIL
# ============================================================
set -uo pipefail

cd "$(dirname "$0")/.." || exit 1
FAIL=0
WARN=0

pass() { printf '  [PASS] %s\n' "$1"; }
fail() { printf '  [FAIL] %s\n' "$1"; FAIL=$((FAIL+1)); }
warn() { printf '  [WARN] %s\n' "$1"; WARN=$((WARN+1)); }
sec()  { printf '\n===== %s =====\n' "$1"; }

echo "============================================================"
echo "  推送前自检"
echo "============================================================"

# ---------- 1. 硬编码凭据 ----------
sec "1/6 硬编码密码 · 密钥"
HITS=$(git grep -inE "(password|passwd|secret|api[_-]?key|access[_-]?token)[[:space:]]*=[[:space:]]*\"[^\"]{6,}\"" -- '*.sh' '*.md' '*.cs' '*.json' '*.yml' 2>/dev/null \
    | grep -viE "Append\(|placeholder|example|your_|xxx" | head -5)
if [ -n "$HITS" ]; then
    fail "发现疑似硬编码凭据（字面量）："
    echo "$HITS" | sed 's/^/         /'
else
    pass "无硬编码凭据（变量拼接/参数传递不算）"
fi

# ---------- 2. 无关项目 ----------
sec "2/6 无关项目提及"
HITS=$(git grep -inE "(mybot|napcat|qq[._-]?bot|灰风|onebot|go-cqhttp)" -- . 2>/dev/null | grep -v "^Binary" | head -8)
if [ -n "$HITS" ]; then
    fail "发现无关项目提及（QQ bot 属独立项目）："
    echo "$HITS" | sed 's/^/         /'
else
    pass "无 QQ bot / 灰风相关字样"
fi

# ---------- 3. 本地路径 ----------
sec "3/6 真实本地路径"
HITS=$(git grep -inE "(C:\\\\Users\\\\[A-Za-z0-9_.-]+|/d/Users/[A-Za-z0-9_.-]+)" -- . 2>/dev/null | grep -v "^Binary" | head -8)
if [ -n "$HITS" ]; then
    warn "发现本机绝对路径："
    echo "$HITS" | sed 's/^/         /'
else
    pass "无本机绝对路径（/home/xxx 这类部署账号路径不算）"
fi

# ---------- 4. 公网 IP ----------
sec "4/6 公网 IP"
HITS=$(git grep -ohE "\b([0-9]{1,3}\.){3}[0-9]{1,3}\b" -- '*.md' '*.sh' '*.cs' 2>/dev/null \
    | grep -vE "^(192\.168\.|10\.|172\.(1[6-9]|2[0-9]|3[01])\.|127\.|0\.0\.0\.0|255\.255|169\.254\.|224\.|239\.|240\.|223\.5\.5\.5|223\.6\.6\.6|1\.1\.1\.1|8\.8\.8\.8|114\.114\.114\.114|103\.28\.54\.136|103\.10\.124\.84)" \
    | sort -u | head -6)
if [ -n "$HITS" ]; then
    warn "发现非私有网段 IP（确认是否为公共服务地址）："
    echo "$HITS" | sed 's/^/         /'
else
    pass "无敏感公网 IP（游戏服务器/DNS 等公开地址已排除）"
fi

# ---------- 5. 大文件 ----------
sec "5/6 大文件"
WHITELIST="ua3f|GrayRouter.zip|\.exe$|\.dll$"
BIG=$(git ls-files | while read -r f; do
    [ -f "$f" ] || continue
    sz=$(stat -c%s "$f" 2>/dev/null || echo 0)
    [ "$sz" -gt 1048576 ] || continue
    echo "$sz $f"
done | sort -rn)
if [ -n "$BIG" ]; then
    COUNT=$(echo "$BIG" | grep -c . || true)
    UNKNOWN=$(echo "$BIG" | grep -vE "$WHITELIST" | head -5)
    if [ -n "$UNKNOWN" ]; then
        warn "有 >1MB 且不在白名单的文件："
        echo "$UNKNOWN" | awk '{printf "         %.1f MB  %s\n", $1/1048576, $2}'
    else
        pass "$COUNT 个 >1MB 文件，都是方案必需的二进制/部署包"
    fi
else
    pass "无 >1MB 文件"
fi

# ---------- 6. 工作区 ----------
sec "6/6 工作区状态"
TOTAL=$(git status --porcelain | wc -l | tr -d ' ')
STAGED=$(git status --porcelain | grep -cE '^[MA]' || true)
echo "  未提交改动：$TOTAL 个（已暂存 $STAGED 个）"
if [ "$STAGED" -gt 0 ]; then
    warn "有已暂存未提交的改动"
else
    pass "无已暂存改动（可直接推 HEAD）"
fi
if [ "$TOTAL" -gt 0 ]; then
    echo "  清单（前 5）："
    git status --porcelain | head -5 | sed 's/^/         /'
fi

# ---------- 汇总 ----------
echo ""
echo "============================================================"
printf "  FAIL = %d    WARN = %d\n" "$FAIL" "$WARN"
if [ "$FAIL" -eq 0 ]; then
    echo "  ✓ 可以推送"
else
    echo "  ✗ 有 $FAIL 项必须先处理"
fi
echo "============================================================"
exit "$([ "$FAIL" -eq 0 ] && echo 0 || echo 1)"
