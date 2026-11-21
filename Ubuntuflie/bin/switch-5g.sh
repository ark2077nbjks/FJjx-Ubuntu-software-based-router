#!/usr/bin/env bash
set -uo pipefail   # 出错别静默；只读脚本不用 -e，避免单条查询失败整段退出
# 兼容旧入口：switch-5g.sh [信道]
SELF=$(cd "$(dirname "$0")" && pwd)
exec bash "$SELF/set-band.sh" 5g "${1:-36}"
