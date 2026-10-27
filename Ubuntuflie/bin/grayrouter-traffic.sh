#!/usr/bin/env bash
# 每设备流量统计入口（ctl 白名单调用，root 运行）
exec python3 /usr/local/bin/grayrouter-traffic.py "$@"
