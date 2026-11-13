# Ubuntu Server 适配

> 目标：把这套 GrayRouter 软路由方案从 **Ubuntu Desktop** 迁到 **Ubuntu Server**（或无桌面最小安装）。
> 结论：**能迁**，脚本层零改动（35 个脚本无 GUI 依赖），但要补 4 个包 + 换网络管理栈。
> 生成：2026-11-13 · 对应 v1.51.9

---

## 一、为什么 Desktop → Server 不是「换个镜像」那么简单

| 差异 | Desktop（现在） | Server | 影响 |
|---|---|---|---|
| 网络管理 | **NetworkManager**（nmcli 可用） | **systemd-networkd**（默认） | **16 个脚本依赖 nmcli，必须换** |
| 防火墙工具 | 预装 nftables | 通常只有 iptables | 黑名单/TTL 伪装要补 nftables |
| JSON 工具 | 预装 jq | 通常没有 | configbackup 要补 jq |
| 无线工具 | 预装 iw | 通常没有 | AP 管理要补 iw |
| 桌面 | gnome-shell + ptyxis | 无 | 脚本无 GUI 依赖，不影响 |
| 图形兜底 | 有（出问题能开桌面） | 无 | **只能 SSH，要备 HDMI 键盘/U 盘** |

**核心结论**：脚本本身完全适配 Server，**唯一硬门槛是网络栈从 systemd-networkd 换成 NetworkManager**。

---

## 二、依赖清单（实测统计）

35 个脚本里用到的命令，按次数：

| 命令 | 用了几次 | Server 默认 | 处理 |
|---|---|---|---|
| `ip` | 154 | ✅ | 无 |
| `nmcli` | 84 | ❌ | **装 network-manager** |
| `nft` | 63 | ❌ | 装 nftables |
| `dnsmasq` | 30 | ⚠️（NM 自带 dnsmasq） | 随 NM 装 |
| `iw` | 27 | ❌ | 装 iw |
| `tc` | 26 | ✅（iproute2） | 无 |
| `jq` | 17 | ❌ | 装 jq |
| `docker` | 11 | ❌ | 可选（NapCat 用，不属于本方案） |
| `curl` | 11 | ✅ | 无 |
| `python3` | 10 | ✅ | 无 |

---

## 三、迁移步骤（三种方式）

### 方式 A：新装 Server 版（推荐，最干净）

```bash
# 1. 装系统（Ubuntu Server 26.04 LTS，最小安装）
# 2. 首次登录后，先跑准备脚本
sudo bash install-server-prep.sh
# 3. 再跑原来的安装器
sudo bash ../Ubuntuflie/install.sh
```

### 方式 B：Desktop 就地转 Server（不重装）

```bash
# 装包 + 换栈（不改系统文件结构）
sudo bash install-server-prep.sh

# 想彻底去掉桌面（可选，谨慎）：
sudo apt remove --purge ubuntu-desktop gnome-shell
sudo apt autoremove
```
> 注意：去桌面前先确认 SSH 能连、SSH 免密配好，否则会失去图形界面。

### 方式 C：只检查不切换（先看差异）

```bash
bash check-server.sh          # 只读检查，输出 PASS/FAIL
sudo bash install-server-prep.sh --dry-run   # 演练，不改任何东西
```

---

## 四、准备脚本做了什么

`install-server-prep.sh`：

1. **装依赖**：`network-manager nftables jq iw iproute2 curl python3`
2. **切网络栈**：停用 `systemd-networkd`，启用 `NetworkManager`
3. **netplan 渲染器**：把 `/etc/netplan/*.yaml` 里的 `renderer: networkd` 改成 `renderer: NetworkManager`
4. **备份**：所有改动前备份到 `/var/lib/grayrouter/server-migration-backup/`
5. **验证**：nmcli 可用、无线接口能看到、nft 可用

**⚠️ 切换网络栈会短暂断网**（几秒到几十秒），必须：
- 确保能物理接触（HDMI + 键盘）
- 或者确认 SSH 会话不会因为你自己的网卡重连而断

---

## 五、Server 版验证清单

```bash
# 1. 网络管理栈
systemctl is-active NetworkManager              # active
systemctl is-active systemd-networkd            # inactive
nmcli general status                            # connected

# 2. 依赖命令
for c in nmcli nft iw jq tc ip curl python3; do
  command -v "$c" >/dev/null && echo "✓ $c" || echo "✗ $c"
done

# 3. 无线 AP 能力
iw dev                                          # 能看到无线的 phy
iw list | grep -A2 "Supported interface modes"  # 要有 AP

# 4. 方案自身健康
systemctl --failed --no-legend | wc -l          # 0
sudo /usr/local/bin/grayrouter-audit.sh verify  # ✓ 审计正常
```

---

## 六、Server 版特有的坑

| 坑 | 说明 | 对策 |
|---|---|---|
| **netplan 还在用 networkd** | 只装 NM 不改 renderer，NM 管不到 | 准备脚本会改 |
| **NM 抢占失败** | 有云初始化（cloud-init）时会写 networkd 配置 | 检查 `/etc/netplan/50-cloud-init.yaml` |
| **没有图形兜底** | 改网络配置断了就进不去 | 改前确认 SSH 稳、备 HDMI |
| **iw 看不到无线** | Server 内核可能缺驱动 | `dmesg \| grep -i firmware` 查 |
| **AP 模式不支持** | 有些无线网卡不支持 AP | `iw list` 看 Supported interface modes |
| **DNS 解析（NM 自带 dnsmasq）** | Server 版 dnsmasq 来自 NM | 装完 NM 就有，不用单独装 |
| **没有 cups/snap** | 无关脚本，别装 | — |

---

## 七、和 Desktop 版的差异（运行时行为）

| 项 | Desktop | Server |
|---|---|---|
| 开机时间 | 慢（桌面服务） | **快**（无 GUI） |
| 内存占用 | ~1.5G 起 | **~300-500M** |
| 日志噪音 | gnome/snap 一堆 | **干净** |
| 面板连接 | SSH | SSH（**一样**） |
| 脚本行为 | 一样 | **一样** |

**收益：Server 版更省资源、更干净、更适合长期跑。**

---

## 八、迁移后的清理（可选）

```bash
# 1. 如果原来是 Desktop，确认不再需要桌面
# 2. 清掉桌面残留包（谨慎，先确认 SSH 正常）
sudo apt remove --purge ubuntu-desktop ubuntu-session gnome-shell 2>/dev/null
sudo apt autoremove -y

# 3. 检查启动耗时
systemd-analyze blame | head -10

# 4. 确认开机自启服务都在
systemctl list-unit-files "grayrouter*" | grep enabled
```

---

## 文件说明

| 文件 | 用途 |
|---|---|
| `README.md` | 本文件（适配说明） |
| `install-server-prep.sh` | 一键准备（装包 + 换栈 + 验证），支持 `--dry-run` |
| `check-server.sh` | 只读检查，输出 PASS/FAIL 清单 |

用法：

```bash
bash check-server.sh                              # 先看现状
sudo bash install-server-prep.sh --dry-run        # 演练
sudo bash install-server-prep.sh                  # 真执行
bash check-server.sh                              # 复查
sudo bash ../Ubuntuflie/install.sh                # 装软路由本体
```
