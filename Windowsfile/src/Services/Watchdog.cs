using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetPanel.Services;

/// <summary>看门狗（grayrouter-watch）：脚本内嵌在 exe 里，面板一键装到远端。</summary>
public static class Watchdog
{
    public const string Script = """
#!/usr/bin/env bash
# GrayRouter 看门狗 —— 检测"假在线"（AP 有 IP、有设备、但数据不动）
# MODE=observe 只记录；MODE=act 才真的重启 AP 连接
set -u
IFACE="${IFACE:-wlp2s0}"
EXPECT_IP="${EXPECT_IP:-192.168.50.1}"
MODE="${MODE:-observe}"
MODE_FILE="$STATE_DIR/mode.request"
STATE_DIR="${STATE_DIR:-/run/grayrouter}"
OUT="$STATE_DIR/watch.json"
LAST="$STATE_DIR/last.txt"
LOG="${LOG:-/var/log/grayrouter-watch.log}"
SPAN=180                # 观察窗口：秒
HOT=0                    # 连续可疑次数

mkdir -p "$STATE_DIR" 2>/dev/null; chmod 755 "$STATE_DIR" 2>/dev/null
[ -f "$STATE_DIR/mode.request" ] || { echo observe > "$STATE_DIR/mode.request"; chmod 666 "$STATE_DIR/mode.request" 2>/dev/null; }
[ -s "$STATE_DIR/mode.request" ] && MODE=$(tr -d " \r\n" < "$STATE_DIR/mode.request")
[ -f "$STATE_DIR/restart.request" ] || { : > "$STATE_DIR/restart.request"; chmod 666 "$STATE_DIR/restart.request" 2>/dev/null; }

ts=$(date '+%Y-%m-%d %H:%M:%S')
now=$(date +%s)

REQ="$STATE_DIR/restart.request"
if [ -s "$REQ" ]; then
  : > "$REQ"
  logger -t grayrouter-watch "收到面板重启请求，重启 AP"
  echo "$ts [执行] 面板请求 → 重启 AP 连接" >> "$LOG"
  nmcli connection up Gray-Router >/dev/null 2>&1
  echo "$now 0 0" > "$LAST"
fi

ip_ok=false
if ip -4 -o addr show dev "$IFACE" 2>/dev/null | grep -q "$EXPECT_IP/"; then ip_ok=true; fi

stations=0
if command -v iw >/dev/null 2>&1; then
  stations=$(iw dev "$IFACE" station dump 2>/dev/null | grep -c '^Station')
fi

rx=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
tx=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)

prev_ts=0; prev_rx=0; prev_tx=0
[ -f "$LAST" ] && read -r prev_ts prev_rx prev_tx < "$LAST"
echo "$now $rx $tx" > "$LAST"

span=$(( now - prev_ts ))
delta=0
[ "$prev_ts" -gt 0 ] && delta=$(( (rx + tx) - (prev_rx + prev_tx) ))

verdict=ok
would=false
# 首次运行 / 计数器重置 / 间隔过长：只当基线，不判定
if [ "$prev_ts" -le 0 ] || [ "$span" -gt 3600 ] || [ "$delta" -lt 0 ]; then
  verdict=ok
elif [ "$ip_ok" = true ] && [ "$stations" -gt 0 ] && [ "$span" -ge "$SPAN" ] && [ "$delta" -eq 0 ]; then
  verdict=suspect
  would=true
fi

# 连续可疑计数
if [ "$verdict" = suspect ]; then
  HOT=$(( $(cat "$STATE_DIR/hot" 2>/dev/null || echo 0) + 1 ))
else
  HOT=0
fi
echo "$HOT" > "$STATE_DIR/hot"

acted=false
if [ "$verdict" = suspect ] && [ "$MODE" = act ] && [ "$HOT" -le 3 ]; then
  logger -t grayrouter-watch "假在线（第 $HOT 次），重启 AP 连接 $IFACE"
  echo "$ts [执行] 第 ${HOT} 次假在线 → nmcli connection up Gray-Router" >> "$LOG"
  nmcli connection up Gray-Router >/dev/null 2>&1 && acted=true
  echo "$now 0 0" > "$LAST"   # 重置基线，避免立刻又判定
elif [ "$verdict" = suspect ]; then
  echo "$ts [观察] 本应重启 AP（模式=$MODE，连续第 ${HOT} 次，窗口 ${span}s，字节增量 ${delta}）" >> "$LOG"
fi

cat > "$OUT.tmp" <<JSON
{"ts":"$ts","iface":"$IFACE","ip_ok":$ip_ok,"stations":$stations,"span":$span,"delta":$delta,"verdict":"$verdict","mode":"$MODE","would_restart":$would,"acted":$acted,"hot":$HOT}
JSON
mv -f "$OUT.tmp" "$OUT"
chmod 644 "$OUT" 2>/dev/null
""";

    public const string Service = """
[Unit]
Description=GrayRouter 看门狗（检测 AP 假在线）
After=network.target

[Service]
Type=oneshot
Environment=MODE=observe
ExecStart=/usr/local/bin/grayrouter-watch.sh
""";

    public const string Timer = """
[Unit]
Description=每分钟运行 GrayRouter 看门狗

[Timer]
OnBootSec=2min
OnUnitActiveSec=60s
AccuracySec=5s
Unit=grayrouter-watch.service

[Install]
WantedBy=timers.target
""";

    public const string Install = """
#!/usr/bin/env bash
# 安装看门狗（需要 sudo）
set -e
D="$(cd "$(dirname "$0")" && pwd)"
install -m 755 "$D/grayrouter-watch.sh" /usr/local/bin/grayrouter-watch.sh
install -m 644 "$D/grayrouter-watch.service" /etc/systemd/system/grayrouter-watch.service
install -m 644 "$D/grayrouter-watch.timer" /etc/systemd/system/grayrouter-watch.timer
mkdir -p /run/grayrouter && chmod 755 /run/grayrouter
: > /run/grayrouter/restart.request && chmod 666 /run/grayrouter/restart.request
echo observe > /run/grayrouter/mode.request && chmod 666 /run/grayrouter/mode.request
touch /var/log/grayrouter-watch.log && chmod 644 /var/log/grayrouter-watch.log
systemctl daemon-reload
systemctl enable --now grayrouter-watch.timer >/dev/null
systemctl start grayrouter-watch.service
sleep 1
echo "== 安装完成 =="
systemctl is-enabled grayrouter-watch.timer
systemctl list-timers grayrouter-watch.timer --no-pager | head -2
echo "== 首次结果 =="
cat /run/grayrouter/watch.json 2>/dev/null || echo "还没写出结果"
""";

    public const string Toggle = """
#!/usr/bin/env bash
# 切换看门狗模式：observe（只看）/ act（自动重启 AP）
set -e
MODE="${1:-}"
case "$MODE" in
  observe|act) ;;
  *) echo "用法: $0 observe|act"; exit 1;;
esac
sed -i "s/^Environment=MODE=.*/Environment=MODE=$MODE/" /etc/systemd/system/grayrouter-watch.service
systemctl daemon-reload
systemctl restart grayrouter-watch.timer >/dev/null
systemctl start grayrouter-watch.service
echo "已切换到 $MODE"
cat /run/grayrouter/watch.json 2>/dev/null
""";

    private static readonly string[] Names =
        { "grayrouter-watch.sh", "grayrouter-watch.service", "grayrouter-watch.timer", "install-watch.sh", "toggle-watch-mode.sh" };

    private static string StageFiles()
    {
        var dir = Path.Combine(AppPaths.TmpDir, "watch");
        Directory.CreateDirectory(dir);
        var map = new (string Name, string Body)[]
        {
            ("grayrouter-watch.sh", Script),
            ("grayrouter-watch.service", Service),
            ("grayrouter-watch.timer", Timer),
            ("install-watch.sh", Install),
            ("toggle-watch-mode.sh", Toggle),
        };
        foreach (var (name, body) in map)
            File.WriteAllText(Path.Combine(dir, name), body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return dir;
    }

    /// <summary>上传文件 → 弹真终端跑 sudo 安装（只需输这一次密码）</summary>
    public static async Task<(bool Ok, string Detail)> InstallAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");

        var dir = StageFiles();
        var (rc, _, err) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"mkdir -p ~/grayrouter-watch\"");
        if (rc != 0) return (false, "连不上远端：" + err.Trim());

        var files = string.Join(" ", Names.Select(n => "\"" + Path.Combine(dir, n) + "\""));
        var scpArgs = "-q -P " + cfg.Port + " -o StrictHostKeyChecking=accept-new " + files;
        var (rc2, o2, e2) = await Remote.RunAsync("scp.exe", scpArgs + " " + cfg.Target + ":~/grayrouter-watch/");
        if (rc2 != 0)
            return (false, "上传失败：" + (string.IsNullOrWhiteSpace(e2) ? o2 : e2).Trim());

        Remote.RunInTerminal(cfg, "安装看门狗", "sudo bash ~/grayrouter-watch/install-watch.sh");
        return (true, "文件已传到远端，正在弹终端——在那里输一次密码即可");
    }

    /// <summary>手动：点一下让远端重启 AP（不需要密码）</summary>
    public static async Task<(bool Ok, string Detail)> RestartApAsync(RemoteConfig cfg)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");
        var cmd = "date +%s > /run/grayrouter/restart.request 2>/dev/null && echo WATCH_OK";
        var (rc, o, e) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"" + cmd + "\"");
        var txt = (o + e).Trim();
        if (rc != 0 || !txt.Contains("WATCH_OK"))
            return (false, "远端没接受请求——看门狗可能还没装（先点「安装看门狗」）");
        return (true, "已发出请求，看门狗会在下一轮（≤60 秒）重启 AP");
    }

    /// <summary>开关：写模式文件，远端下一轮就按新模式跑（不需要密码）</summary>
    public static async Task<(bool Ok, string Detail)> SetModeAsync(RemoteConfig cfg, string mode)
    {
        if (!cfg.IsConfigured) return (false, "还没配置远端地址");
        var cmd = "echo " + mode + " > /run/grayrouter/mode.request 2>/dev/null && echo WATCH_OK";
        var (rc, o, e) = await Remote.RunAsync("ssh.exe",
            Remote.BaseArgs(cfg, true) + cfg.Target + " \"" + cmd + "\"");
        var txt = (o + e).Trim();
        if (rc != 0 || !txt.Contains("WATCH_OK"))
            return (false, "远端没接受：看门狗可能没装，或版本旧（缺 /run/grayrouter/mode.request）。重装一次看门狗即可。");
        return (true, mode == "act" ? "已开启自动执行（60 秒内生效）" : "已切回观察模式（60 秒内生效）");
    }
}
