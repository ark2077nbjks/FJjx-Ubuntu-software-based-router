using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetPanel.Services;

/// <summary>远端遥控（grayrouter-ctl）：切模式 / 回滚 / 加固，装一次后不用密码。</summary>
public static class RouterCtl
{
    public const string Script = """
#!/usr/bin/env bash
# ============================================================
#  grayrouter-ctl.sh —— 面板遥控入口（白名单 5 个命令，支持附加信道参数）
#    请求文件：/run/grayrouter/request   内容 "<id> <命令>"
#    结果文件：/run/grayrouter/result.json
#  由 grayrouter-ctl.path 监听请求文件变化后触发，root 运行
# ============================================================
set -uo pipefail

REQ=/run/grayrouter/request
RES=/run/grayrouter/result.json
RDIR=/run/grayrouter/res
SEEN=/run/grayrouter/ctl.seen
LOG=/var/log/grayrouter-ctl.log

# 安装时写入的用户（面板登录的那个账号），脚本都放在它的家目录
GR_USER=""
[ -f /etc/grayrouter/ctl.conf ] && . /etc/grayrouter/ctl.conf
HOMEDIR=$(getent passwd "${GR_USER:-root}" 2>/dev/null | cut -d: -f6)
[ -n "$HOMEDIR" ] || HOMEDIR=/root

mkdir -p /run/grayrouter 2>/dev/null
[ -f "$REQ" ] || { : > "$REQ"; chmod 666 "$REQ" 2>/dev/null; }
[ "$(id -u)" -eq 0 ] || exit 0

raw=$(tr -d '\r\n' < "$REQ" 2>/dev/null || true)
[ -z "${raw// /}" ] && exit 0

rid=$(printf '%s' "$raw" | awk '{print $1}')
cmd=$(printf '%s' "$raw" | awk '{print $2}')
arg=$(printf '%s' "$raw" | awk '{print $3}')
rest=$(printf '%s' "$raw" | cut -d' ' -f3- | tr -d '\r')
case "${arg:-}" in ''|*[!0-9]*) arg="" ;; esac

# 同一个 id 只执行一次（避免 path 重复触发）
last=$(cat "$SEEN" 2>/dev/null || echo "")
[ "$rid" = "$last" ] && exit 0
echo "$rid" > "$SEEN"

log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

write_res() {   # $1=state  $2=exit  $3=msg
  STATE="$1" CODE="$2" RID="$rid" CMD="$cmd" MSG="$3"
  export STATE CODE RID CMD MSG RES RDIR
  mkdir -p "$RDIR" 2>/dev/null; chmod 777 "$RDIR" 2>/dev/null
  python3 - <<'PY' 2>/dev/null || echo "{\"state\":\"$1\",\"exit\":$2}" > "$RES"
import json, os, time
d = {"state": os.environ["STATE"], "exit": int(os.environ["CODE"]),
     "id": os.environ["RID"], "cmd": os.environ["CMD"],
     "msg": os.environ["MSG"], "ts": int(time.time())}
for p in (os.environ["RES"], os.path.join(os.environ["RDIR"], os.environ["RID"] + ".json")):
    try:
        json.dump(d, open(p, "w"), ensure_ascii=False)
    except Exception:
        pass
PY
  chmod 644 "$RES" 2>/dev/null
  chmod 644 "$RDIR/$rid.json" 2>/dev/null
  ls -t "$RDIR"/*.json 2>/dev/null | tail -n +41 | xargs -r rm -f 2>/dev/null
}

# 设备命令的参数只允许 MAC，防止被塞奇怪的东西
case "$cmd" in
  dev-block|dev-unblock)
    printf '%s' "$rest" | grep -qiE '^([0-9a-f]{2}:){5}[0-9a-f]{2}$' || {
      write_res "error" 65 "MAC 格式不对：$rest"; log "拒绝 MAC: $rest"; exit 0; } ;;
  dev-*) : ;;
esac

log "收到请求 id=$rid cmd=$cmd"

case "$cmd" in
  mode-router)   SCRIPT=$HOMEDIR/router-up.sh ;;
  mode-hotspot)  SCRIPT=$HOMEDIR/router-down.sh ;;
  harden)        SCRIPT=/usr/local/bin/grayrouter-harden.sh ;;
  wifi-5g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="5g ${arg:-36}" ;;
  wifi-2g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="2g ${arg:-6}" ;;
  wifi-ssid)     SCRIPT=$HOMEDIR/grayrouter-ctl/set-ssid.sh; ARGS="$rest"
                 printf '%s' "$rest" | grep -qE '^[^[:space:][:cntrl:]]+$' || {
                   write_res "error" 74 "WiFi 名字不能含空格：$rest"; log "拒绝 wifi-ssid: $rest"; exit 0; }
                 _b=$(printf '%s' "$rest" | wc -c | tr -d ' ')
                 if [ "$_b" -lt 1 ] || [ "$_b" -gt 32 ]; then
                   write_res "error" 75 "WiFi 名字须 1~32 字节（当前 ${_b} 字节）"; exit 0; fi ;;
  dev-block)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="block $rest"; DEVCAP=1 ;;
  dev-unblock)   SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="unblock $rest"; DEVCAP=1 ;;
  dev-unblock-all) SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="unblock-all"; DEVCAP=1 ;;
  dev-apply)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="apply"; DEVCAP=1 ;;
  dev-list)      SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="list"; DEVCAP=1 ;;
  dev-names)     SCRIPT=$HOMEDIR/grayrouter-devmgr/grayrouter-devices.sh; ARGS="names"; DEVCAP=1 ;;
  traffic-now)   SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="now ${arg:-30}"; DEVCAP=1 ;;
  traffic-summary) SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="summary ${arg:-30}"; DEVCAP=1 ;;
  traffic-reset) SCRIPT=/usr/local/bin/grayrouter-traffic.sh; ARGS="reset"; DEVCAP=1 ;;
  *)
    write_res "unknown" 64 "不认识的命令：$cmd"
    log "拒绝：$cmd"
    exit 0
    ;;
esac

if [ ! -f "$SCRIPT" ]; then
  write_res "error" 66 "找不到脚本：$SCRIPT"
  log "找不到 $SCRIPT"
  exit 0
fi

write_res "running" 0 "开始执行 $cmd ${ARGS:-}"
if [ "${DEVCAP:-0}" = "1" ]; then
  tailmsg=$(bash "$SCRIPT" ${ARGS:-} 2>>"$LOG")
  code=$?
else
  OUT=$(mktemp /tmp/grctl.XXXXXX)
  bash "$SCRIPT" ${ARGS:-} >> "$OUT" 2>&1
  code=$?
  cat "$OUT" >> "$LOG"
  tailmsg=$(tail -n 3 "$OUT" | tr '\n' ' ' | cut -c1-300)
  rm -f "$OUT"
fi

if [ "$code" -eq 0 ]; then
  write_res "done" 0 "$tailmsg"
  log "完成 $cmd (exit 0)"
else
  write_res "error" "$code" "$tailmsg"
  log "失败 $cmd (exit $code)"
fi
""";

    public const string Service = """
[Unit]
Description=GrayRouter 面板遥控（执行一条来自面板的命令）
After=network.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/grayrouter-ctl.sh
TimeoutStartSec=900
Nice=5
""";

    public const string PathUnit = """
[Unit]
Description=监听 GrayRouter 面板请求文件

[Path]
PathModified=/run/grayrouter/request
Unit=grayrouter-ctl.service

[Install]
WantedBy=multi-user.target
""";

    public const string Install = """
#!/usr/bin/env bash
# ============================================================
#  安装 grayrouter-ctl（面板遥控）：只需要 sudo 执行一次
#  用法：sudo bash grayrouter-ctl/install-ctl.sh
# ============================================================
set -uo pipefail
SRC=$(cd "$(dirname "$0")" && pwd)
[ "$(id -u)" -eq 0 ] || { echo "需要 root：sudo bash grayrouter-ctl/install-ctl.sh"; exit 1; }

echo "[1/5] 安装程序"
install -m 755 "$SRC/grayrouter-ctl.sh" /usr/local/bin/grayrouter-ctl.sh || exit 1
install -m 644 "$SRC/grayrouter-ctl.service" /etc/systemd/system/grayrouter-ctl.service || exit 1
install -m 644 "$SRC/grayrouter-ctl.path" /etc/systemd/system/grayrouter-ctl.path || exit 1

echo "[2/5] 准备请求 / 结果文件"
mkdir -p /etc/grayrouter
GR_USER="${SUDO_USER:-$(id -un)}"
echo "GR_USER=$GR_USER" > /etc/grayrouter/ctl.conf
chmod 644 /etc/grayrouter/ctl.conf
echo "      账号：$GR_USER（脚本从它的家目录找）"
mkdir -p /run/grayrouter
chmod 755 /run/grayrouter
: > /run/grayrouter/request; chmod 666 /run/grayrouter/request
echo '{}' > /run/grayrouter/result.json; chmod 644 /run/grayrouter/result.json
touch /var/log/grayrouter-ctl.log; chmod 644 /var/log/grayrouter-ctl.log

echo "[3/5] 重载 systemd"
systemctl daemon-reload

echo "[4/5] 启动监听"
systemctl enable --now grayrouter-ctl.path

echo "[5/5] 检查"
systemctl is-active grayrouter-ctl.path
echo
echo "✅ 装好了：面板现在可以切模式 / 回滚 / 加固伪装，都不用输密码"
echo "   日志：/var/log/grayrouter-ctl.log"
""";

    /// <summary>改 WiFi 名称（SSID）的远端脚本：只写配置 + 重应用 + 轮询验证，绝不 down 连接（down 会连带断有线）</summary>
    private const string SsidScript = """
#!/usr/bin/env bash
# ============================================================
#  set-ssid.sh <新名字>  —— 改 WiFi 名称（SSID）
#
#  设计要点（踩坑教训）：
#   · 绝不 nmcli con down —— 会让 NetworkManager 重排整卡网络，有线一起断
#   · 失败不自动回滚     —— 回滚要再断一次，伤害更大
#   · 只写配置 + con up + 轮询验证
#   · 802.11 标准：SSID 上限 32 字节（一个汉字 3 字节）
# ============================================================
set -uo pipefail
NEW="${1:-}"
CON="Gray-Router"
DIR="/root/grayrouter-backup"
BAK="$DIR/ssid.bak"

if [ "$(id -u)" -ne 0 ]; then
  echo "需要管理员权限，正在提权…"
  exec sudo bash "$0" "$@"
fi

[ -n "$NEW" ] || { echo "用法：set-ssid.sh <新名字>"; exit 2; }

if ! printf '%s' "$NEW" | grep -qE '^[^[:space:][:cntrl:]]+$'; then
  echo "❌ 名字不合法：不能含空格或控制字符"
  exit 2
fi
BYTES=$(printf '%s' "$NEW" | wc -c | tr -d ' ')
if [ "$BYTES" -lt 1 ] || [ "$BYTES" -gt 32 ]; then
  echo "❌ 名字太长：当前 ${BYTES} 字节，上限 32 字节（一个汉字算 3 字节）"
  exit 2
fi

OLD=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
if [ -z "$OLD" ]; then
  echo "❌ 找不到无线连接 $CON"
  exit 1
fi

if [ "$OLD" = "$NEW" ]; then
  echo "√ 已经是这个名字：$NEW"
  exit 0
fi

echo "===== 修改 WiFi 名称 ====="
echo "原名称：$OLD"
echo "新名称：$NEW"

mkdir -p "$DIR" 2>/dev/null
printf '%s\n' "$OLD" > "$BAK"
echo "[1/3] 已记录原名（不会自动回滚，需要时用它改回）"

if ! nmcli con modify "$CON" 802-11-wireless.ssid "$NEW"; then
  echo "❌ 写配置失败，设置未改动"
  exit 1
fi
echo "[2/3] 已写入新名称"

# 只重应用，不 down（关键：down 会连带断有线）
nmcli con up "$CON" >/dev/null 2>&1 || true
echo "[3/3] 正在重启 AP 应用新名字…"

CUR=""
for i in $(seq 1 20); do
  sleep 1
  CUR=$(nmcli -g 802-11-wireless.ssid con show "$CON" 2>/dev/null)
  [ "$CUR" = "$NEW" ] && break
done

if [ "$CUR" = "$NEW" ]; then
  echo "✅ 成功：WiFi 名称已改为 $NEW"
  echo "   无线设备需用新名字重连（约 10~20 秒）；有线不受影响。"
  exit 0
fi

echo "❌ 读回不一致（当前=${CUR:-空}）"
echo "   配置里已是新名字，下次无线重连或重启会自动生效。"
echo "   本次不自动回滚（避免再断一次）。要改回原名可手动："
echo "   nmcli con modify $CON 802-11-wireless.ssid \"$OLD\" && nmcli con up $CON"
exit 1
""";

    private static readonly string[] Names =
        { "grayrouter-ctl.sh", "grayrouter-ctl.service", "grayrouter-ctl.path", "install-ctl.sh", "set-ssid.sh" };

    private static string StageFiles()
    {
        var dir = Path.Combine(AppPaths.TmpDir, "grctl");
        Directory.CreateDirectory(dir);
        var map = new (string Name, string Body)[]
        {
            ("grayrouter-ctl.sh", Script),
            ("grayrouter-ctl.service", Service),
            ("grayrouter-ctl.path", PathUnit),
            ("install-ctl.sh", Install),
            ("set-ssid.sh", SsidScript),
        };
        foreach (var (name, body) in map)
            File.WriteAllText(Path.Combine(dir, name), body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return dir;
    }

    /// <summary>远端装没装（systemd 单元在 + 改 WiFi 名脚本在）</summary>
    public static async Task<bool> IsInstalledAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return false;
        var (rc, o, _) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target +
            " \"test -f /etc/systemd/system/grayrouter-ctl.path && test -f ~/grayrouter-ctl/set-ssid.sh && echo YES\"");
        return rc == 0 && o.Contains("YES");
    }

    /// <summary>上传 + 弹真终端跑 sudo 安装（只需这一次密码）</summary>
    public static async Task<(bool Ok, string Detail)> InstallAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");

        var dir = StageFiles();
        var (rc, _, err) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"mkdir -p ~/grayrouter-ctl\"");
        if (rc != 0) return (false, "连不上远端：" + err.Trim());

        var files = string.Join(" ", Names.Select(n => "\"" + Path.Combine(dir, n) + "\""));
        var (rc2, o2, e2) = await Remote.RunAsync("scp.exe",
            "-q -P " + cfg.Port + " -o StrictHostKeyChecking=accept-new " + files + " " + cfg.Target + ":~/grayrouter-ctl/");
        if (rc2 != 0)
            return (false, "上传失败：" + (string.IsNullOrWhiteSpace(e2) ? o2 : e2).Trim());

        Remote.RunInTerminal(cfg, "安装遥控助手", "sudo bash ~/grayrouter-ctl/install-ctl.sh");
        return (true, "文件已上传，终端里输一次密码即可");
    }

    /// <summary>切 5GHz（远端自带 150 秒死手回滚）</summary>
    public static Task<(bool Ok, string Detail)> Set5GAsync(RemoteConfig cfg, int channel = 36, Action<string>? progress = null)
        => RunAsync(cfg, "wifi-5g " + channel, progress);

    /// <summary>切回 2.4GHz</summary>
    public static Task<(bool Ok, string Detail)> Set2GAsync(RemoteConfig cfg, int channel = 6, Action<string>? progress = null)
        => RunAsync(cfg, "wifi-2g " + channel, progress);

    /// <summary>改 WiFi 名称（SSID）。远端会重启 AP，无线客户端需用新名字重连；面板走有线不受影响。</summary>
    public static Task<(bool Ok, string Detail)> SetSsidAsync(RemoteConfig cfg, string ssid, Action<string>? progress = null)
        => RunAsync(cfg, "wifi-ssid " + ssid, progress);

    /// <summary>发一条命令，等远端执行完（最多等 4 分钟）</summary>
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static int _seq;

    /// <summary>连续失败次数：给面板的轮询做指数退避用（成功一次就归零）</summary>
    public static int Fails { get; private set; }

    /// <summary>按失败次数放大轮询间隔：基础 3s → 6/12/24…，封顶 maxSec</summary>
    public static TimeSpan Backoff(TimeSpan baseIv, int maxSec = 300)
    {
        if (Fails <= 0) return baseIv;
        double s = baseIv.TotalSeconds * Math.Pow(2, Math.Min(Fails, 6));
        return TimeSpan.FromSeconds(Math.Min(maxSec, Math.Max(baseIv.TotalSeconds, s)));
    }

    /// <summary>同一条通道同一时刻只能有一个请求：远端只会执行「最新一条」，并发会互相顶掉。</summary>
    public static async Task<(bool Ok, string Detail)> RunAsync(RemoteConfig cfg, string cmd, Action<string>? progress = null)
    {
        await _gate.WaitAsync();
        try
        {
            var r = await RunCoreAsync(cfg, cmd, progress);
            if (r.Ok) Fails = 0;
            else Fails = Math.Min(Fails + 1, 6);
            return r;
        }
        finally { _gate.Release(); }
    }

    private static async Task<(bool Ok, string Detail)> RunCoreAsync(RemoteConfig cfg, string cmd, Action<string>? progress = null)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");

        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + (_seq++ % 1000).ToString("D3");
        var write = "echo '" + id + " " + cmd + "' > /run/grayrouter/request 2>/dev/null && echo CTL_OK";
        var (rc, o, e) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"" + write + "\"");
        var txt = (o + e).Trim();
        if (rc != 0 || !txt.Contains("CTL_OK"))
            return (false, "远端没接受命令——遥控助手可能还没装（先点「安装遥控」）");

        var miss = 0;
        for (int i = 0; i < 80; i++)
        {
            // 快起步：前 12 次 0.4 秒一探，之后 1 秒，再往后 3 秒；短命令（读状态/列租约）不再干等 3 秒
            var waitMs = i < 12 ? 400 : i < 30 ? 1000 : 3000;
            await Task.Delay(waitMs);
            var elapsed = (i < 12 ? (i + 1) * 0.4 : i < 30 ? 4.8 + (i - 11) * 1.0 : 4.8 + 18 + (i - 29) * 3.0);
            progress?.Invoke(i < 5 ? "正在执行…" : "还在跑（" + (int)elapsed + " 秒）…");

            var (rc2, out2, _) = await Remote.RunAsync("ssh.exe",
                Remote.BaseArgs(cfg, true) + cfg.Target + " \"cat /run/grayrouter/res/" + id + ".json 2>/dev/null || cat /run/grayrouter/result.json 2>/dev/null\"");
            if (rc2 != 0 || string.IsNullOrWhiteSpace(out2))
            {
                // 切模式/切频段时远端会断开几十秒，连不上是正常的；但别死等到 4 分钟
                miss++;
                if (miss >= 16)
                    return (false, "远端连不上了（切换时正常现象）。等 Wi-Fi 回来点「刷新」看结果；" +
                                   "要确认就去看远端的 /var/log/grayrouter-ctl.log");
                continue;
            }
            miss = 0;

            try
            {
                using var doc = JsonDocument.Parse(out2.Trim());
                var root = doc.RootElement;
                var rid = root.TryGetProperty("id", out var rEl) ? rEl.ToString() : "";
                var state = root.TryGetProperty("state", out var sEl) ? (sEl.GetString() ?? "") : "";
                var msg = root.TryGetProperty("msg", out var mEl) ? (mEl.GetString() ?? "") : "";

                if (rid != id) continue;
                if (state == "running") continue;
                if (state == "done") return (true, string.IsNullOrWhiteSpace(msg) ? "完成" : msg);
                if (state == "unknown") return (false, "远端不认识这条命令：" + msg);
                var code = root.TryGetProperty("exit", out var eEl) ? eEl.ToString() : "?";
                return (false, "执行失败（exit " + code + "）：" + msg);
            }
            catch { }
        }
        return (false, "等太久了（4 分钟）——去远端看 /var/log/grayrouter-ctl.log");
    }
}
