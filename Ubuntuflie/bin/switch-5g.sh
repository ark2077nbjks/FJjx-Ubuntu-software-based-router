#!/usr/bin/env bash
# 兼容旧入口：switch-5g.sh [信道]
SELF=$(cd "$(dirname "$0")" && pwd)
exec bash "$SELF/set-band.sh" 5g "${1:-36}"
