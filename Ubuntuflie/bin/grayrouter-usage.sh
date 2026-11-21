#!/bin/bash
set -uo pipefail   # 出错别静默；只读脚本不用 -e
# ============================================================
#  grayrouter-usage.sh —— 用量汇总（ctl 薄包装，转调 Python）
#  用法：grayrouter-usage.sh [天数，默认 30]
#  依赖：python3、grayrouter-usage.py
#  退出码：跟随被调用脚本
#  注：本文件只是 exec 转调，不含业务逻辑
# ============================================================
# ctl 统一用 bash 调脚本，这里是 Python 汇总器的薄包装
exec /usr/bin/python3 /usr/local/bin/grayrouter-usage.py "${1:-30}"
