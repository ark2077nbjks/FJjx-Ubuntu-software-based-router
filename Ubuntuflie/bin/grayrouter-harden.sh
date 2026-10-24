#!/usr/bin/env bash
# ------------------------------------------------------------
# 通用版：参数全部从 /etc/grayrouter/config.env 读取
# 可用环境变量覆盖，便于调试：GR_ETC=/tmp/x ./router-status.sh
# ------------------------------------------------------------
set -u
GR_ETC=${GR_ETC:-/etc/grayrouter}
GR_OPT=${GR_OPT:-/opt/grayrouter}
[ -r "$GR_ETC/config.env" ] && . "$GR_ETC/config.env"

TOPO=${TOPO:-A}
WAN_IF=${WAN_IF:-}
LAN_IF=${LAN_IF:-}
LAN_TYPE=${LAN_TYPE:-wifi-ap}
LAN_IP=${LAN_IP:-192.168.50.1}
SSID=${SSID:-Gray-Router}
LAN_CON=${LAN_CON:-GrayRouter-LAN}

# 可选加固：TTL 统一为 128（校园网共享常用，普通宽带不需要）
C_G=$'\033[32m'; C_R=$'\033[31m'; C_N=$'\033[0m'
[ "$(id -u)" -ne 0 ] && [ "${GR_SANDBOX:-0}" != "1" ] && { echo "需要 root：sudo $0"; exit 2; }
command -v nft >/dev/null || { printf "${C_R}没装 nftables${C_N}\n"; exit 1; }
cat >"$GR_ETC/ttl.nft" <<'EOF'
table ip grayrouter
delete table ip grayrouter
table ip grayrouter {
  chain post {
    type filter hook postrouting priority mangle; policy accept;
    ip ttl set 128
  }
}
EOF
nft -f "$GR_ETC/ttl.nft" && printf "${C_G}TTL 已统一为 128${C_N}（撤销：sudo %s/unharden.sh）\n" "$GR_OPT/bin"
