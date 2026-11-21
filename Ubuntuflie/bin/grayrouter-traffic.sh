#!/usr/bin/env bash
set -uo pipefail   # 出错别静默；只读脚本不用 -e
# ============================================================
#  grayrouter-traffic.sh —— 流量记录汇总（ctl 薄包装，转调 Python）
#  用法：grayrouter-traffic.sh [参数透传给 grayrrouter-traffic.py]
#  依赖：python3、grayrouter-traffic.py
#  退出码：跟随被调用脚本
#  注：本文件只是 exec 转调，不含业务逻辑
# ============================================================
# 每设备流量统计入口（ctl 白名单调用，root 运行）
exec python3 /usr/local/bin/grayrouter-traffic.py "$@"
