#!/bin/bash
# ctl 统一用 bash 调脚本，这里是 Python 汇总器的薄包装
exec /usr/bin/python3 /usr/local/bin/grayrouter-usage.py "${1:-30}"
