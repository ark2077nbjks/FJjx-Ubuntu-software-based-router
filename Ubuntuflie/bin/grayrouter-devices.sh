#!/usr/bin/env bash
# ============================================================
#  grayrouter-devices.sh —— 设备管理（黑名单 + 流量统计）
#   黑名单：nft 表 inet gray_dev，按 MAC 丢包（forward + input）
#   流量  ：iw dev wlp2s0 station dump 的 rx/tx（内核维护，最准）
#   数据  ：/etc/grayrouter/devices.json
#   用法  ：apply | block <mac> | unblock <mac> | unblock-all | list
# ============================================================
set -uo pipefail

TBL=gray_dev
JSON=/etc/grayrouter/devices.json
LOG=/var/log/grayrouter-devices.log
# LAN 口自动探测：优先 AP 模式网口，其次 192.168.x 地址的口，最后兜底 wlp2s0
# ---------- LAN 网口集合（不止一个） ----------
# 因地制宜：凡是 NetworkManager 里 ipv4.method=shared 的活动连接，就是正在发 DHCP 的 LAN 口
# （现在有 Gray-Router=wlp2s0 和 LAN-USB=enx…；以后再加 USB 网口也会自动认出）
WAN_IF=$(/usr/local/bin/grayrouter-roles.sh get 2>/dev/null | sed -n 's/^WAN_IF=//p')
LAN_IFS=${GR_LAN_IF:-}
lan_ifaces() {
  local name dev type m out=""
  while IFS=: read -r name dev type; do
    [ -n "${dev:-}" ] || continue
    case "$type" in 802-11-wireless|802-3-ethernet|802-11-wireless-mesh) ;; *) continue ;; esac
    m=$(nmcli -g ipv4.method con show "$name" 2>/dev/null | head -1)
    [ "$m" = "shared" ] && out="$out $dev"
  done < <(nmcli -t -f NAME,DEVICE,TYPE con show --active 2>/dev/null)
  if [ -z "${out// /}" ]; then   # 兜底：有内网地址、不是 WAN、不是 docker/veth
    out=$(ip -4 -o addr show 2>/dev/null \
          | awk '{print $2" "$4}' | grep -E ' (192\.168|10\.|172\.(1[6-9]|2[0-9]|3[01]))\.' | awk '{print $1}' \
          | grep -vE '^(docker|veth|br-|lo)' || true)
  fi
  echo "$out" | tr ' ' '\n' | grep -v '^$' | grep -vx "${WAN_IF:-__none__}" | sort -u | tr '\n' ' '
}
LAN_IFS=$(lan_ifaces)
[ -z "${LAN_IFS// /}" ] && LAN_IFS=wlp2s0
IFACE=${LAN_IFS%% *}   # 兼容旧逻辑（第一个 LAN 口）

log() { printf '%s %s\n' "$(date '+%F %T')" "$*" >> "$LOG" 2>/dev/null; }
norm_mac() { printf '%s' "${1:-}" | tr 'A-Z' 'a-z' | tr -d ' '; }

ensure_json() {
  [ -f "$JSON" ] || { mkdir -p "$(dirname "$JSON")"; printf '{"devices": {}}\n' > "$JSON"; }
}

json_set() {  # $1=mac $2=blocked(true/false) $3=name
  ensure_json
  python3 - "$JSON" "$1" "$2" "${3:-}" <<'PY'
import json, sys, time
path, mac, blocked, name = sys.argv[1], sys.argv[2], sys.argv[3] == "true", sys.argv[4]
d = json.load(open(path))
devs = d.setdefault("devices", {})
e = devs.setdefault(mac, {"name": "", "blocked": False, "added": int(time.time())})
e["blocked"] = blocked
e["changed"] = int(time.time())
if name:
    e["name"] = name
json.dump(d, open(path, "w"), indent=1, ensure_ascii=False)
PY
}

blocked_list() {
  ensure_json
  python3 - "$JSON" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
for mac, e in d.get("devices", {}).items():
    if e.get("blocked"):
        print(mac)
PY
}

ensure_table() {
  if ! nft list table inet $TBL >/dev/null 2>&1; then
    nft add table inet $TBL 2>>"$LOG"
    log "建表 inet $TBL"
  fi
  nft list set inet $TBL blocked >/dev/null 2>&1 ||
    nft add set inet $TBL blocked '{ type ether_addr; }' 2>>"$LOG"

  mkchain() {
    nft list chain inet $TBL "$1" >/dev/null 2>&1 && return 0
    nft -f - 2>>"$LOG" <<EOF
table inet $TBL {
  chain $1 {
    $2
  }
}
EOF
  }
  mkchain gr_fwd "type filter hook forward priority -10; policy accept;"
  mkchain gr_in  "type filter hook input priority -10; policy accept;"

  nft list chain inet $TBL gr_fwd 2>/dev/null | grep -q '@blocked drop' ||
    nft add rule inet $TBL gr_fwd ether saddr @blocked drop 2>>"$LOG"
  nft list chain inet $TBL gr_in 2>/dev/null | grep -q '@blocked drop' ||
    nft add rule inet $TBL gr_in ether saddr @blocked drop 2>>"$LOG"
}

sync_set() {
  local cur want
  cur=$(nft list set inet $TBL blocked 2>/dev/null | tr ',{}' '\n\n\n' \
        | grep -oiE '([0-9a-f]{2}:){5}[0-9a-f]{2}' | tr 'A-Z' 'a-z' | sort -u)
  want=$(blocked_list | tr 'A-Z' 'a-z' | sort -u)
  [ "$cur" = "$want" ] && return 0

  nft flush set inet $TBL blocked 2>>"$LOG"
  local m
  for m in $want; do
    nft add element inet $TBL blocked "{ $m }" 2>>"$LOG" || log "加集合失败 $m"
  done
  log "同步黑名单：$(printf '%s' "$want" | wc -w) 台"
}

do_apply() {
  ensure_table
  sync_set
  log "apply 完成，黑名单 $(blocked_list | wc -w) 台"
}

do_block() {
  local mac; mac=$(norm_mac "${1:-}")
  [[ "$mac" =~ ^([0-9a-f]{2}:){5}[0-9a-f]{2}$ ]] || { echo "MAC 格式不对：$1"; exit 65; }
  ensure_table
  nft add element inet $TBL blocked "{ $mac }" 2>>"$LOG" || true
  json_set "$mac" true
  log "拉黑 $mac"
  echo "已拉黑 $mac"
}

do_unblock() {
  local mac; mac=$(norm_mac "${1:-}")
  [[ "$mac" =~ ^([0-9a-f]{2}:){5}[0-9a-f]{2}$ ]] || { echo "MAC 格式不对：$1"; exit 65; }
  nft delete element inet $TBL blocked "{ $mac }" 2>/dev/null || true
  json_set "$mac" false
  log "解除 $mac"
  echo "已解除 $mac"
}

do_unblock_all() {
  local n=0 m
  for m in $(blocked_list); do
    nft delete element inet $TBL blocked "{ $m }" 2>/dev/null || true
    json_set "$m" false
    n=$((n+1))
  done
  log "全部解除，共 $n 台"
  echo "已全部解除（$n 台）"
}

do_list() {
  ensure_table
  ensure_json
  : > /run/grayrouter-devices.sta
  local i
  for i in $LAN_IFS; do
    [ -d "/sys/class/net/$i/wireless" ] && iw dev "$i" station dump >> /run/grayrouter-devices.sta 2>/dev/null
  done
  # 注意：ip neigh show dev X 的输出不含 dev 字段，所以一次读全表，按 LAN 口白名单过滤
  ip neigh show > /run/grayrouter-devices.neigh 2>/dev/null || : > /run/grayrouter-devices.neigh
  do_names > /run/grayrouter-devices.leases 2>/dev/null || : > /run/grayrouter-devices.leases
  nft list set inet $TBL blocked > /run/grayrouter-devices.set 2>/dev/null || : > /run/grayrouter-devices.set
  python3 - "$JSON" /run/grayrouter-devices.sta /run/grayrouter-devices.neigh \
            /run/grayrouter-devices.leases /run/grayrouter-devices.set "$LAN_IFS" <<'PY'
import json, re, sys, subprocess

json_path, sta_path, neigh_path, lease_path, set_path = sys.argv[1:6]
lan_set = set((sys.argv[6] if len(sys.argv) > 6 else "").split())

try:
    devs = json.load(open(json_path, encoding="utf-8")).get("devices", {})
except Exception:
    devs = {}

# --- 1) WiFi 关联表：信号 + 上下行字节 ---
sta, cur = {}, None
try:
    for line in open(sta_path, errors="replace"):
        line = line.strip()
        if line.startswith("Station "):
            cur = line.split()[1].lower()
            sta[cur] = {"up": 0, "down": 0, "signal": None}
        elif cur and ":" in line:
            k, v = line.split(":", 1)
            k, v = k.strip(), v.strip()
            if k == "rx bytes" and v.isdigit():
                sta[cur]["up"] = int(v)
            elif k == "tx bytes" and v.isdigit():
                sta[cur]["down"] = int(v)
            elif k == "signal":
                m = re.match(r"(-?\d+)", v)
                if m:
                    sta[cur]["signal"] = int(m.group(1))
except Exception:
    pass

# --- 2) ARP/邻居表（所有 LAN 口）：在线状态 + IP + 从哪个口进来 ---
def read_acct():
    """读 nft 计数表（gray_acct），覆盖 WiFi + 有线 + 静态 IP"""
    up, dn = {}, {}
    for name, tgt in (("up4", up), ("down4", dn)):
        try:
            txt = subprocess.run(["nft", "-j", "list", "set", "inet", "gray_acct", name],
                                 capture_output=True, text=True, timeout=5).stdout
            for it in json.loads(txt).get("nftables", []):
                st = it.get("set")
                if not st or st.get("name") != name:
                    continue
                for e in st.get("elem", []):
                    body = e.get("elem", {})
                    ip = body.get("val")
                    if ip:
                        tgt[ip] = int((body.get("counter") or {}).get("bytes", 0))
        except Exception:
            pass
    return up, dn

acct_up, acct_dn = read_acct()
neigh_ip, neigh_if, neigh_st = {}, {}, {}
try:
    for line in open(neigh_path, errors="replace"):
        p = line.split()
        if len(p) >= 6 and "lladdr" in p and "dev" in p:
            dev = p[p.index("dev") + 1]
            if lan_set and dev not in lan_set:
                continue
            state = p[-1].upper()
            if state in ("FAILED", "INCOMPLETE"):
                continue
            mac = p[p.index("lladdr") + 1].lower()
            neigh_ip[mac] = p[0]
            neigh_if[mac] = dev
            neigh_st[mac] = state
except Exception:
    pass

# --- 3) DHCP 租约：主机名 + 分配过的 IP ---
leases = {}
try:
    for line in open(lease_path, errors="replace"):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 3:
            leases[p[0].lower()] = {"ip": p[1], "name": p[2].strip()}
except Exception:
    pass

# --- 4) 黑名单 ---
blocked = {b.lower() for b in re.findall(r"(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}",
           open(set_path, errors="replace").read())}
for mac, e in devs.items():
    if e.get("blocked"):
        blocked.add(mac)

out = []
for mac in set(devs) | set(sta) | set(neigh_ip) | set(leases) | blocked:
    e = devs.get(mac, {})
    s = sta.get(mac, {})
    L = leases.get(mac, {})
    ip = neigh_ip.get(mac) or L.get("ip", "")
    iface = neigh_if.get(mac, "")
    # 只有「关联到 AP」或「邻居表 REACHABLE/DELAY/PROBE」才算在线；STALE 是缓存残影，会挂几分钟
    online = (mac in sta) or (neigh_st.get(mac) in ("REACHABLE", "DELAY", "PROBE"))
    kind = "wifi" if mac in sta else ("wired" if mac in neigh_ip else "")
    # 清掉“没有任何信息”的幽灵条目（历史 JSON 里残留的假 MAC）
    if not online and not ip and not (e.get("name") or L.get("name")) and mac not in blocked \
       and not s.get("up") and not s.get("down"):
        continue
    out.append({
        "mac": mac,
        "name": (e.get("name") or L.get("name") or "").strip(),
        "blocked": mac in blocked,
        "online": online,
        "ip": ip,
        "iface": iface,
        "kind": kind,
        "state": neigh_st.get(mac, ""),
        "up": int(acct_up.get(ip, s.get("up", 0))),
        "down": int(acct_dn.get(ip, s.get("down", 0))),
        "signal": s.get("signal"),
    })
out.sort(key=lambda x: (not x["online"], -(x["up"] + x["down"]), x["mac"]))
print(json.dumps({"devices": out}, ensure_ascii=False))
PY
}

ensure_json
# ---------- 名字：从 DHCP 租约里挖（MAC -> 主机名）----------
do_names() {
  # NetworkManager 共享模式下 dnsmasq 的租约：过期时间 MAC IP 主机名 客户端ID
  for f in /var/lib/NetworkManager/dnsmasq-*.leases /var/lib/misc/dnsmasq.leases; do
    [ -f "$f" ] || continue
    awk -v OFS='\t' 'NF>=4 && $4!="*" { mac=tolower($2); sub(/^\*$/, "", mac); print mac, $3, $4 }' "$f" 2>/dev/null
  done | sort -u
}

case "${1:-}" in
  apply)       do_apply ;;
  block)       do_block "${2:-}" ;;
  unblock)     do_unblock "${2:-}" ;;
  unblock-all) do_unblock_all ;;
  list)        do_list ;;
  names)       do_names ;;
  *) echo "用法: $0 apply|block <mac>|unblock <mac>|unblock-all|list|names"; exit 1 ;;
esac
