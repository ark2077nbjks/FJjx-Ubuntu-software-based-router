# 更新日志

版本号规则照 Minecraft 那套：**主.次**，新功能就把次版本 +1（1.35 → 1.36 → 1.37），同一个版本只修 bug 才加补丁位（1.36.1、1.36.2）。不跳号、不写多余的 `.0`。

安装包跟着面板号走：`GrayRouter-1.35.zip` 配面板 1.35，一眼对得上。

---

## 1.35 · 2026-10-26

**设备管理（第一档）**

- 设备页新增「流量 ↑/↓」和「操作」两列，顶部新增「全部解除」
- 按 MAC 拉黑：nft 表 `inet gray_dev`，`forward` 与 `input` 双链丢包 —— 设备连着 WiFi 但完全没网；换 IP、重连 DHCP 都逃不掉
- 每设备流量读内核 `iw dev <AP接口> station dump` 的 `rx/tx bytes`（和 OpenWrt 同一套数据源）
- 黑名单存 `/etc/grayrouter/devices.json`，`grayrouter-devices.service` 开机恢复
- 遥控助手新增 5 条白名单命令（`dev-block` / `dev-unblock` / `dev-unblock-all` / `dev-apply` / `dev-list`），参数只认 MAC
- **没做**：限速（上传/下载）

**版本号统一**

- 版本只在 `NetPanel.csproj` 写一次，界面从程序集读，删掉 XAML 里硬编码的 `v1.29.2`
- 安装包从 `1.1.2` 改为跟随面板号：`GrayRouter-1.35.zip`

**修掉的坑**

| 现象 | 根因 |
|---|---|
| nft 表建不起来且静默失败 | `set type ether_addr` 不能带 `flags interval` |
| `chain fwd {` 语法错 | `fwd`、`in` 是 nft 保留字，不能当链名 |
| 下行流量恒 0 | `forward` 钩子上 `ether daddr` 还是路由器自己，`ip daddr` 受 NAT 时序影响 |
| 设备列表空 | `ip neigh show dev <接口>` 的输出里没有 `dev` 字段 |

---

## 1.34 · 2026-10-26（旧编号 1.34.0 ~ 1.34.9）

- 一键部署：SSH.NET 直连、内置安装包、7 步进度、9 项自检、断线自愈、演练模式
- 自更新：查 Release → 下载 → 校验大小 → 自替换 → 失败写 `update.log` 并拉起旧版
- 日志轮转：4 MB × 5 份，导出 / 剪切迁移 / 软删 / 硬删
- XAML 格式化 + 抽公共样式；`MainWindow.xaml` 从一行 79 KB 拆成可读结构
- 无线频段页、频段切换（4 层回滚 + `systemd-run` 死手保险）
- 看门狗两阶段上线、7 类诊断建议、虚拟网卡折叠、流量图时间轴

## 1.33 及更早（旧编号 1.0 ~ 1.33）

- 软路由脚本体系（拓扑 A/B/C 自动探测）、UA3F 伪装层（TTL / UA / IPID，MitM 关）
- 面板 14 个栏目：状态、校园网、网址检测、网卡、设备、WiFi、游戏延迟、测速、流量、连接、代理检测、日志、无线频段、其他
- 校园网登录/注销（srun，运营商可选，密码走 Windows DPAPI）

> 旧编号的 Release 和 tag 保持原样，不改写历史。
