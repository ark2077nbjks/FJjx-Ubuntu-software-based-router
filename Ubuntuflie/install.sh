#!/usr/bin/env bash
# ============================================================
#  GrayRouter 一键部署：把 Ubuntu 变成软路由
#  自动探测网卡与出口，只需选拓扑、填 SSID/密码
#  不动系统原有网络配置；出错自动回滚
#
#  用法：
#    sudo bash install.sh                 交互向导
#    sudo bash install.sh --dry-run       只看会做什么，不改任何东西
#    sudo bash install.sh --yes --topology=A --ssid=MyAP --password=12345678 --lan-ip=192.168.50.1
#    sudo bash install.sh --verify        只自检，不改任何东西（输出 CHECK|名字|ok/fail|说明）
# ============================================================
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
. "$HERE/lib/common.sh"
. "$HERE/lib/detect.sh"

DRY=0; GR_ASSUME_YES=0; VERIFY=0; GR_NONINT=0
T_TOPOLOGY=""; T_SSID=""; T_PASS=""; T_LANIP=""; T_UP_SSID=""; T_UP_PASS=""; T_HARDEN="none"

for a in "$@"; do
  case "$a" in
    --dry-run)     DRY=1 ;;
    --yes|-y)      GR_ASSUME_YES=1 ;;
    --topology=*)  T_TOPOLOGY=${a#*=} ;;
    --ssid=*)      T_SSID=${a#*=} ;;
    --password=*)  T_PASS=${a#*=} ;;
    --lan-ip=*)    T_LANIP=${a#*=} ;;
    --up-ssid=*)   T_UP_SSID=${a#*=} ;;
    --up-pass=*)   T_UP_PASS=${a#*=} ;;
    --harden=*)    T_HARDEN=${a#*=} ;;
    --verify)      VERIFY=1 ;;
    --noninteractive) GR_NONINT=1 ;;
    -h|--help)     sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "未知参数：$a"; exit 1 ;;
  esac
done

# 非交互判定：显式 --noninteractive / --yes / 没有终端（面板通过 ssh 管道调用）
[ -t 0 ] || GR_NONINT=1
[ "$GR_ASSUME_YES" = 1 ] && GR_NONINT=1
# 非交互输出不带颜色码，方便面板逐行解析
if [ "$GR_NONINT" = 1 ]; then C_B=""; C_N=""; C_G=""; C_D=""; C_R=""; fi

printf "${C_B}"
cat <<'BANNER'
  ┌──────────────────────────────────────────┐
  │   GrayRouter 一键部署 · 把 Ubuntu 变软路由  │
  └──────────────────────────────────────────┘
BANNER
printf "${C_N}"

[ "$DRY" = 1 ] && warn "演练模式（--dry-run）：只检查和打印计划，不会改动任何东西"
[ "$DRY" = 0 ] && need_root

# ---------- --verify：只自检，不改东西 ----------
if [ "$VERIFY" = 1 ]; then
  CFG="$GR_ETC/config.env"
  [ -f "$CFG" ] && . "$CFG"
  LAN_CON=${LAN_CON:-GrayRouter-LAN}
  FAILN=0
  chk() { # 名称 状态 说明
    printf 'CHECK|%s|%s|%s\n' "$1" "$2" "$3"
    [ "$2" = "ok" ] || FAILN=$((FAILN+1))
  }

  if [ -f "$CFG" ]; then chk "配置文件" ok "$CFG"; else chk "配置文件" fail "缺 $CFG（没装过？）"; fi

  if command -v nmcli >/dev/null 2>&1 && [ "$(systemctl is-active NetworkManager 2>/dev/null)" = "active" ]; then
    chk "NetworkManager" ok "active"
  else
    chk "NetworkManager" fail "没装或没运行"
  fi

  if nmcli -t -f NAME connection show --active 2>/dev/null | grep -qx "$LAN_CON"; then
    chk "LAN/AP 连接" ok "$LAN_CON 已激活"
  else
    chk "LAN/AP 连接" fail "$LAN_CON 未激活"
  fi

  if [ -z "${LAN_IF:-}" ] || [ -z "${LAN_IP:-}" ]; then
    chk "LAN 地址" fail "配置里没有网卡信息（config.env 缺）"
  elif ip -4 -br addr show "$LAN_IF" 2>/dev/null | grep -q "${LAN_IP%%.*}\."; then
    chk "LAN 地址" ok "$LAN_IF 有 ${LAN_IP}/24"
  else
    chk "LAN 地址" fail "$LAN_IF 上没有 $LAN_IP"
  fi

  if [ "$(cat /proc/sys/net/ipv4/ip_forward 2>/dev/null)" = "1" ]; then
    chk "IP 转发" ok "ip_forward=1"
  else
    chk "IP 转发" fail "ip_forward 没开"
  fi

  if systemctl is-active --quiet ua3f 2>/dev/null; then
    chk "伪装层进程" ok "ua3f active"
  else
    chk "伪装层进程" fail "ua3f 没在跑"
  fi

  if nft list table ip gray_ua3f >/dev/null 2>&1; then
    chk "伪装规则表" ok "ip gray_ua3f"
  else
    chk "伪装规则表" fail "表不存在"
  fi

  if systemctl is-enabled --quiet grayrouter 2>/dev/null; then
    chk "开机自启" ok "grayrouter enabled"
  else
    chk "开机自启" fail "grayrouter 没设自启"
  fi

  CODE=$(curl -s -m 6 -o /dev/null -w '%{http_code}' http://connect.rom.miui.com/generate_204 2>/dev/null)
  if [ "$CODE" = "204" ] || [ "$CODE" = "200" ]; then
    chk "出口连通" ok "HTTP $CODE"
  else
    chk "出口连通" fail "HTTP ${CODE:-000}"
  fi

  echo "VERIFY_SUMMARY|fail=$FAILN"
  exit "$FAILN"
fi

# ---------- 0. 依赖自检 ----------
step "0/7 检查运行环境"
GR_MISSING=0
have() { command -v "$1" >/dev/null 2>&1; }

if have nmcli; then
  ok "NetworkManager 已安装"
  if [ "$(systemctl is-active NetworkManager 2>/dev/null)" = "active" ]; then
    ok "NetworkManager 正在运行"
  else
    warn "NetworkManager 没在运行，请先执行： sudo systemctl enable --now NetworkManager"
    GR_MISSING=1
  fi
else
  warn "缺少 NetworkManager（nmcli）—— 本脚本靠它建热点、配 IP、开共享"
  echo "    Ubuntu 桌面版自带； Server 版或精简系统请先装："
  echo "      sudo apt update && sudo apt install -y network-manager"
  GR_MISSING=1
fi

if have ip; then ok "iproute2 已安装"
else warn "缺少 iproute2： sudo apt install -y iproute2"; GR_MISSING=1; fi

if have nft; then ok "nftables 已安装（会写 TTL=128 规则）"
else warn "没装 nftables → TTL 伪装那一步会跳过，不影响上网： sudo apt install -y nftables"; fi

if have iw; then :; else warn "没装 iw → 无法检查无线是否支持 AP 模式： sudo apt install -y iw"; fi

if [ "$GR_MISSING" = 1 ]; then
  if [ "$DRY" = 1 ]; then
    warn "演练模式：依赖不全，真装会失败，上面列了需要的命令"
  else
    die "依赖不全，先补齐上面的再运行（想只看计划可加 --dry-run）"
  fi
fi
echo


# ---------- 1. 探测 ----------
step "1/7 探测硬件"
gr_scan_nics
gr_scan_wan

[ ${#GR_WIRED[@]} -eq 0 ] && [ ${#GR_WIFI[@]} -eq 0 ] && die "没找到任何物理网卡"

printf "  %-10s %-4s %-6s %-9s %s\n" "网卡" "类型" "链路" "速度" "IP"
for i in "${GR_WIRED[@]:-}"; do [ -n "$i" ] && gr_iface_line "$i"; done
for i in "${GR_WIFI[@]:-}";  do [ -n "$i" ] && gr_iface_line "$i"; done
echo
if [ -n "$GR_DEF_IF" ]; then
  ok "当前出口：$GR_DEF_IF（${GR_WAN_KIND:-未知}）→ 网关 ${GR_DEF_GW:-?}"
else
  warn "当前没有默认路由（上游可能还没联网，装完需自己接通上游）"
fi

ok_list=$(gr_topologies)
TOPO_AVAIL=$(echo "$ok_list" | tr -s ' ')
if [ -z "${TOPO_AVAIL// /}" ]; then
  bad "这台机器不满足任何拓扑（需要至少两块网口）"
  exit 3
fi
info "可用拓扑：$TOPO_AVAIL"

# ---------- 2. 选拓扑 ----------
step "2/7 选择拓扑"
echo "  A  有线 WAN → 无线 AP 当 LAN        （笔记本/带无线的小主机）"
echo "  B  有线 WAN → 有线 LAN              （双网口工控机，最稳）"
echo "  C  无线 WAN（连别人 WiFi）→ 有线 LAN（蹭网再分给有线设备）"
echo

pick_topology() {
  local t=$T_TOPOLOGY
  if [ -z "$t" ]; then
    if [ "$(echo "$TOPO_AVAIL" | wc -w)" -eq 1 ]; then
      t=$(echo "$TOPO_AVAIL" | awk '{print $1}')
      info "只有一种可用拓扑，自动选择 $t"
    else
      [ "$GR_NONINT" = 1 ] && die "这台机器有多个可用拓扑（$TOPO_AVAIL），非交互模式必须指定 --topology="
      t=$(ask "选拓扑（$TOPO_AVAIL）" "A")
    fi
  fi
  echo "${t^^}"
}
TOPO=$(pick_topology)
case " $TOPO_AVAIL " in
  *" $TOPO "*) ok "拓扑 $TOPO" ;;
  *) die "拓扑 $TOPO 在这台机器上不可用（可用：$TOPO_AVAIL）" ;;
esac

# ---------- 3. 选网卡 ----------
step "3/7 确认网卡"
WAN_IF=""; LAN_IF=""; LAN_TYPE=""
case "$TOPO" in
  A) WAN_IF=${GR_DEF_IF:-${GR_WIRED[0]}}; LAN_IF=${GR_APCAP[0]}; LAN_TYPE="wifi-ap" ;;
  B) WAN_IF=${GR_DEF_IF:-${GR_WIRED[0]}}
     for i in "${GR_WIRED[@]}"; do [ "$i" != "$WAN_IF" ] && { LAN_IF=$i; break; }; done
     LAN_TYPE="ethernet" ;;
  C) LAN_IF=${GR_WIRED[0]}; WAN_IF=""; LAN_TYPE="ethernet" ;;
esac
[ -z "$WAN_IF" ] && [ "$TOPO" != "C" ] && die "没找到 WAN 网卡"
[ -z "$LAN_IF" ] && die "没找到 LAN 网卡"
ok "WAN  : ${WAN_IF:-（无线，见下一步）}"
ok "LAN  : $LAN_IF（$LAN_TYPE）"

# ---------- 4. 参数 ----------
step "4/7 填参数"
WIFI_SSID=""; WIFI_PASS=""; UP_SSID=""; UP_PASS=""
if [ "$LAN_TYPE" = "wifi-ap" ]; then
  WIFI_SSID=${T_SSID:-$(ask "给这个热点起个名字(SSID)" "Gray-Router")}
  WIFI_PASS=${T_PASS:-}
  [ "$DRY" = 1 ] && [ -z "$WIFI_PASS" ] && WIFI_PASS="dryrun-placeholder"
  if [ ${#WIFI_PASS} -lt 8 ] && [ "$DRY" = 0 ]; then
    [ "$GR_NONINT" = 1 ] && die "热点密码缺失或少于 8 位（非交互模式请用 --password=至少8位）"
    while [ ${#WIFI_PASS} -lt 8 ]; do
      [ -n "$WIFI_PASS" ] && warn "密码至少 8 位"
      read -r -s -p "热点密码（至少 8 位）: " WIFI_PASS; echo
    done
  fi
else
  info "LAN 为有线，不需要热点参数"
fi
if [ "$TOPO" = "C" ]; then
  UP_SSID=${T_UP_SSID:-$(ask "要连的上游 WiFi 名称" "")}
  [ -z "$UP_SSID" ] && die "拓扑 C 需要上游 WiFi 名称"
  if [ -z "$T_UP_PASS" ] && [ "$DRY" = 0 ]; then
    read -r -s -p "上游 WiFi 密码（开放网络直接回车）: " UP_PASS; echo
  else
    UP_PASS=$T_UP_PASS
  fi
fi

DEF_LANIP="192.168.50.1"
# 冲突检测：能 ping 通就说明这个段已被占用
if ping -c1 -W1 "$DEF_LANIP" >/dev/null 2>&1; then
  warn "$DEF_LANIP 已被别的设备占用，改用 192.168.60.1"
  DEF_LANIP="192.168.60.1"
fi
if [ -n "$T_LANIP" ]; then LANIP=$T_LANIP
elif [ "$GR_NONINT" = 1 ]; then LANIP=$DEF_LANIP
else LANIP=$(ask "LAN 网关地址" "$DEF_LANIP"); fi

echo
info "加固选项：$T_HARDEN（none=不改 TTL；ttl=统一 TTL 为 128）"
[ "$T_HARDEN" = "none" ] && [ "$GR_ASSUME_YES" = 0 ] && \
  confirm "要不要顺便做 TTL 伪装？（校园网共享常用，普通宽带不需要）" "n" && T_HARDEN="ttl"

# ---------- 5. 计划确认 ----------
step "5/7 即将执行"
cat <<PLAN
  拓扑        $TOPO
  WAN         ${WAN_IF:-$UP_SSID（无线上游）}
  LAN         $LAN_IF（$LAN_TYPE）
  LAN 网关    $LANIP/24
  ${WIFI_SSID:+热点        $WIFI_SSID（WPA2）}
  加固        $T_HARDEN
  落点        $GR_ETC  $GR_OPT  $GR_VAR
  不会改动    现有 WAN 连接配置、netplan、你原有的热点
PLAN
echo
if [ "$DRY" = 1 ]; then
  ok "演练结束（没有改动任何东西）"
  exit 0
fi
confirm "确认开始安装？" "y" || die "已取消"

# ---------- 6. 执行 ----------
step "6/7 安装"
mkdir -p "$GR_ETC" "$GR_OPT/bin" "$GR_VAR"
jinit
track_dir "$GR_ETC"; track_dir "$GR_OPT"; track_dir "$GR_VAR"

# 配置
cat >"$GR_ETC/config.env" <<CFG
# GrayRouter 配置（安装于 $(date '+%F %T')）
TOPO=$TOPO
WAN_IF=$WAN_IF
LAN_IF=$LAN_IF
LAN_TYPE=$LAN_TYPE
LAN_CON=GrayRouter-LAN
SSID=$WIFI_SSID
LAN_IP=$LANIP
HARDEN=$T_HARDEN
CFG
chmod 600 "$GR_ETC/config.env"
track_created "$GR_ETC/config.env"
ok "配置写入 $GR_ETC/config.env"

# 脚本
for f in "$HERE"/bin/*.sh; do
  [ -e "$f" ] || continue
  install -m 755 "$f" "$GR_OPT/bin/$(basename "$f")"
done
track_created "$GR_OPT/bin"
ok "脚本安装到 $GR_OPT/bin（$(ls "$GR_OPT"/bin/*.sh 2>/dev/null | wc -l) 个）"

# 转发
SYSCTL_DIR=${SYSCTL_DIR:-/etc/sysctl.d}
SYSCTL_F=$SYSCTL_DIR/99-grayrouter.conf
backup_file "$SYSCTL_F"
echo "net.ipv4.ip_forward=1" >"$SYSCTL_F"
track_created "$SYSCTL_F"
sysctl -q -p "$SYSCTL_F"
ok "已开启 ip_forward"

# LAN / AP 连接（用 NetworkManager 的 shared，自动带 DHCP+NAT）
mk_lan() {
  local name=$1 type=$2 ifname=$3
  if nmcli -t -f NAME connection show | grep -qx "$name"; then
    warn "已存在连接 $name，先删除旧的重建"
    nmcli connection delete "$name" >/dev/null 2>&1 || true
  fi
  if [ "$type" = "wifi-ap" ]; then
    nmcli connection add type wifi ifname "$ifname" con-name "$name" ssid "$WIFI_SSID" \
      autoconnect yes save yes >/dev/null || return 1
    nmcli connection modify "$name" \
      802-11-wireless.mode ap \
      802-11-wireless.band bg \
      802-11-wireless.powersave 2 \
      wifi-sec.key-mgmt wpa-psk wifi-sec.psk "$WIFI_PASS" \
      ipv4.method shared ipv4.addresses "$LANIP/24" >/dev/null || return 1
  else
    nmcli connection add type ethernet ifname "$ifname" con-name "$name" \
      autoconnect yes save yes >/dev/null || return 1
    nmcli connection modify "$name" ipv4.method shared ipv4.addresses "$LANIP/24" >/dev/null || return 1
  fi
  track_nm "$name"
  nmcli connection up "$name" >/dev/null
}

LAN_CON="GrayRouter-LAN"
mk_lan "$LAN_CON" "$LAN_TYPE" "$LAN_IF" || die "创建 LAN 连接失败"
ok "LAN 已启动：$LAN_CON（$LANIP/24，DHCP+NAT 由 NetworkManager 负责）"

# 拓扑 C：先把上游 WiFi 连上
if [ "$TOPO" = "C" ]; then
  WIFI_IF=${GR_WIFI[0]}
  UP_CON="GrayRouter-WAN-WiFi"
  if nmcli -t -f NAME connection show | grep -qx "$UP_CON"; then
    nmcli connection delete "$UP_CON" >/dev/null 2>&1 || true
  fi
  nmcli connection add type wifi ifname "$WIFI_IF" con-name "$UP_CON" ssid "$UP_SSID" >/dev/null || die "创建上游 WiFi 连接失败"
  if [ -n "$UP_PASS" ]; then
    nmcli connection modify "$UP_CON" wifi-sec.key-mgmt wpa-psk wifi-sec.psk "$UP_PASS" >/dev/null
  fi
  track_nm "$UP_CON"
  nmcli connection up "$UP_CON" >/dev/null && ok "已连上游 WiFi：$UP_SSID" || warn "上游 WiFi 没连上，装完自己确认一下"
fi

# 加固：TTL（调用包内脚本，逻辑单点）
if [ "$T_HARDEN" = "ttl" ]; then
  if bash "$GR_OPT/bin/grayrouter-harden.sh" >/dev/null 2>&1; then
    ok "TTL 统一为 128"
    track_created "$GR_ETC/ttl.nft"
  else
    warn "TTL 规则没加载成功（可跳过，不影响上网）"
  fi
fi

# 伪装层：UA3F（IPID 重写 + 明文 UA；不做 MitM，客户端零证书零配置）
UA3F_OK=0
if [ -x "$HERE/bin/ua3f" ]; then
  install -m 755 "$HERE/bin/ua3f" "$GR_OPT/bin/ua3f"
  install -m 755 "$HERE/bin/ua3f-rules.sh" "$GR_OPT/bin/ua3f-rules.sh"
  install -m 644 "$HERE/etc/ua3f.yaml" "$GR_ETC/ua3f.yaml"
  track_created "$GR_OPT/bin/ua3f"
  track_created "$GR_ETC/ua3f.yaml"
  UA3F_UNIT=${SYSTEMD_DIR:-/etc/systemd/system}/ua3f.service
  cat >"$UA3F_UNIT" <<UA3FUNIT
[Unit]
Description=GrayRouter 伪装层（UA3F：IPID 重写）
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$GR_OPT/bin/ua3f -c $GR_ETC/ua3f.yaml
Restart=always
RestartSec=5
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
UA3FUNIT
  track_unit "$UA3F_UNIT"
  systemctl daemon-reload
  if systemctl enable --now ua3f.service >/dev/null 2>&1; then
    sleep 2
    if systemctl is-active --quiet ua3f; then
      UA3F_OK=1
      ok "伪装层已启动（UA3F，IPID 重写）"
    else
      warn "UA3F 没起来（不影响上网）：journalctl -u ua3f -n 30"
    fi
  else
    warn "UA3F 开机自启设置失败（不影响上网）"
  fi
  if [ "$UA3F_OK" = "1" ] && bash "$GR_OPT/bin/ua3f-rules.sh" apply >/dev/null 2>&1; then
    ok "伪装规则已加载（tproxy → 1080）"
  else
    warn "伪装规则没加载（可能内核缺 nf_tproxy，不影响上网）"
  fi
else
  warn "包里没有 ua3f 二进制，跳过伪装层"
fi

# 开机自启
SYSTEMD_DIR=${SYSTEMD_DIR:-/etc/systemd/system}
UNIT=$SYSTEMD_DIR/grayrouter.service
cat >"$UNIT" <<UNITEOF
[Unit]
Description=GrayRouter 软路由（LAN/AP + 规则）
After=NetworkManager.service
Wants=NetworkManager.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/nmcli connection up $LAN_CON
ExecStartPost=-/usr/bin/nft -f $GR_ETC/ttl.nft
ExecStartPost=-/usr/bin/bash $GR_OPT/bin/ua3f-rules.sh apply
ExecStop=/usr/bin/nmcli connection down $LAN_CON
ExecStopPost=-/usr/bin/bash $GR_OPT/bin/ua3f-rules.sh clear

[Install]
WantedBy=multi-user.target
UNITEOF
track_unit "$UNIT"
track_created "$UNIT"
systemctl daemon-reload
systemctl enable grayrouter.service >/dev/null 2>&1 || systemctl enable grayrouter.service >/dev/null 2>&1
ok "已设置开机自启（systemctl disable grayrouter 可关）"

# 每设备限速：tc 规则不落盘，开机用 oneshot 重建
if [ -f "$GR_OPT/bin/grayrouter-limit.sh" ] || [ -f /usr/local/bin/grayrouter-limit.sh ]; then
  LIMIT_UNIT=$SYSTEMD_DIR/grayrouter-limit.service
  cat >"$LIMIT_UNIT" <<LIMITEOF
[Unit]
Description=GrayRouter 每设备限速恢复（tc 规则不落盘，开机重建）
After=NetworkManager.service
Wants=NetworkManager.service

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'sleep 8; /usr/local/bin/grayrouter-limit.sh apply'

[Install]
WantedBy=multi-user.target
LIMITEOF
  track_unit "$LIMIT_UNIT"
  track_created "$LIMIT_UNIT"
  systemctl daemon-reload
  systemctl enable grayrouter-limit.service >/dev/null 2>&1 || true

# AP 频段恢复（开机后把 set-band 保存的频段补回去；Ubuntu 26.04 的 AP 连接在 /run 下重建）
BAND_UNIT=$SYSTEMD_DIR/grayrouter-band.service
cat >"$BAND_UNIT" <<BANDUNIT
[Unit]
Description=GrayRouter 恢复 AP 频段（失败自动回退 2.4G）
After=NetworkManager.service grayrouter.service

[Service]
Type=oneshot
ExecStart=$GR_OPT/bin/grayrouter-band.sh apply
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
BANDUNIT
systemctl daemon-reload >/dev/null 2>&1 || true
systemctl enable grayrouter-band.service >/dev/null 2>&1 || true
  ok "已设置每设备限速开机恢复"
fi

# ---------- 7. 自检 ----------
step "7/7 自检"
FAIL=0
have_ip() { ip -4 -br addr show "$1" 2>/dev/null | grep -q "$2"; }
have_ip "$LAN_IF" "${LANIP%%.*}" && ok "LAN 网卡有 IP" || { bad "LAN 网卡没有 IP"; FAIL=1; }
[ "$(cat /proc/sys/net/ipv4/ip_forward)" = "1" ] && ok "ip_forward=1" || { bad "ip_forward 没开"; FAIL=1; }
if systemctl is-active --quiet ua3f 2>/dev/null; then
  ok "伪装层 ua3f 运行中"
else
  warn "伪装层 ua3f 没在跑（不影响上网，可稍后 systemctl start ua3f）"
fi
if nft list table ip gray_ua3f >/dev/null 2>&1; then
  ok "伪装规则表已加载"
else
  warn "伪装规则表没加载"
fi
[ -n "$GR_DEF_IF" ] && ok "上游默认路由在（$GR_DEF_IF）" || warn "上游还没联网，装完自己接通"
if curl -s -m 6 -o /dev/null -w "%{http_code}" https://www.baidu.com 2>/dev/null | grep -qE '^(2|3)'; then
  ok "路由器本身能出网"
else
  warn "路由器本身出网失败（上游可能没认证/没插线）"
fi
nmcli -t -f NAME,DEVICE connection show --active 2>/dev/null | grep -q "$LAN_CON" \
  && ok "LAN 连接处于活动状态" || { bad "LAN 连接没起来"; FAIL=1; }

# ---- rtw88 假在线修复：驱动参数 + 信标自愈 + 开机自启 ----
if [ -x "$HERE/bin/install-rtw88-fix.sh" ]; then
  echo
  bash "$HERE/bin/install-rtw88-fix.sh" || warn "rtw88 稳定化没装全（不影响上网）"
fi

echo
if [ "$FAIL" = 0 ]; then
  printf "${C_G}安装完成。${C_N}手机/电脑连 WiFi「%s」或用网线接 LAN 口即可上网。\n" "${WIFI_SSID:-$LAN_CON}"
  printf "${C_D}卸载：sudo bash uninstall.sh    状态：sudo %s/bin/router-status.sh${C_N}\n" "$GR_OPT"
else
  printf "${C_R}自检未通过。${C_N}可执行 sudo bash uninstall.sh 回滚，或把上面输出发回来。\n"
  exit 4
fi
