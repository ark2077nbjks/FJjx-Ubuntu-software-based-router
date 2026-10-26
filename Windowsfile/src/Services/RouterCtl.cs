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
case "${arg:-}" in ''|*[!0-9]*) arg="" ;; esac

# 同一个 id 只执行一次（避免 path 重复触发）
last=$(cat "$SEEN" 2>/dev/null || echo "")
[ "$rid" = "$last" ] && exit 0
echo "$rid" > "$SEEN"

log() { echo "$(date '+%F %T') $*" >> "$LOG"; }

write_res() {   # $1=state  $2=exit  $3=msg
  STATE="$1" CODE="$2" RID="$rid" CMD="$cmd" MSG="$3"
  export STATE CODE RID CMD MSG
  python3 - <<'PY' 2>/dev/null || echo "{\"state\":\"$1\",\"exit\":$2}" > "$RES"
import json, os, time
json.dump({"state": os.environ["STATE"], "exit": int(os.environ["CODE"]),
           "id": os.environ["RID"], "cmd": os.environ["CMD"],
           "msg": os.environ["MSG"], "ts": int(time.time())},
          open("/run/grayrouter/result.json", "w"), ensure_ascii=False)
PY
  chmod 644 "$RES" 2>/dev/null
}

log "收到请求 id=$rid cmd=$cmd"

case "$cmd" in
  mode-router)   SCRIPT=$HOMEDIR/router-up.sh ;;
  mode-hotspot)  SCRIPT=$HOMEDIR/router-down.sh ;;
  harden)        SCRIPT=/usr/local/bin/grayrouter-harden.sh ;;
  wifi-5g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="5g ${arg:-36}" ;;
  wifi-2g)       SCRIPT=$HOMEDIR/grayrouter-wifi/set-band.sh; ARGS="2g ${arg:-6}" ;;
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

write_res "running" 0 "开始执行 $cmd ${arg:-}"
OUT=$(mktemp /tmp/grctl.XXXXXX)
bash "$SCRIPT" ${ARGS:-} >> "$OUT" 2>&1
code=$?
cat "$OUT" >> "$LOG"
tailmsg=$(tail -n 3 "$OUT" | tr '\n' ' ' | cut -c1-300)
rm -f "$OUT"

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

    private static readonly string[] Names =
        { "grayrouter-ctl.sh", "grayrouter-ctl.service", "grayrouter-ctl.path", "install-ctl.sh" };

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
        };
        foreach (var (name, body) in map)
            File.WriteAllText(Path.Combine(dir, name), body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return dir;
    }

    /// <summary>远端装没装（看 systemd 单元在不在）</summary>
    public static async Task<bool> IsInstalledAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return false;
        var (rc, o, _) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"test -f /etc/systemd/system/grayrouter-ctl.path && echo YES\"");
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

    /// <summary>发一条命令，等远端执行完（最多等 4 分钟）</summary>
    public static async Task<(bool Ok, string Detail)> RunAsync(RemoteConfig cfg, string cmd, Action<string>? progress = null)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");

        var id = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var write = "echo '" + id + " " + cmd + "' > /run/grayrouter/request 2>/dev/null && echo CTL_OK";
        var (rc, o, e) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"" + write + "\"");
        var txt = (o + e).Trim();
        if (rc != 0 || !txt.Contains("CTL_OK"))
            return (false, "远端没接受命令——遥控助手可能还没装（先点「安装遥控」）");

        for (int i = 0; i < 80; i++)
        {
            await Task.Delay(3000);
            progress?.Invoke(i < 5 ? "正在执行…" : "还在跑（" + ((i + 1) * 3) + " 秒）…");

            var (rc2, out2, _) = await Remote.RunAsync("ssh.exe",
                Remote.BaseArgs(cfg, true) + cfg.Target + " \"cat /run/grayrouter/result.json 2>/dev/null\"");
            if (rc2 != 0 || string.IsNullOrWhiteSpace(out2)) continue;

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
