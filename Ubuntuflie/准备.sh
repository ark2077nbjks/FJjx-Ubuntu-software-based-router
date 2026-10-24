#!/usr/bin/env bash
# 解压后先跑一次这个：给脚本加执行权限，把三个图标标成"可信"，双击就能用
cd "$(dirname "$0")" || exit 1
chmod +x ./*.sh ./*.desktop bin/*.sh lib/*.sh 2>/dev/null

if command -v gio >/dev/null 2>&1; then
  for f in ./*.desktop; do
    gio set -t string "$f" metadata::trusted true 2>/dev/null || true
  done
fi

echo "√ 已准备完成。"
echo
echo "接下来双击："
echo "  GrayRouter 体检  — 先看这台机器能不能装（只读）"
echo "  GrayRouter 安装  — 图形向导安装"
echo
echo "如果双击还是提示'不受信任'，右键图标 → 允许运行 / 允许启动，即可。"
