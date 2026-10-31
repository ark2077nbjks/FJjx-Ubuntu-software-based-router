## 1.51.1 — 系统页 + 变更守卫 + 可持久化调优

发布日期：2026-10-31

### 新增
- **「系统」页（第 12 栏）**：服务表（grayrouter-* / ssh / NetworkManager / docker，单个重启，二次确认）、电源（重启网络 / 重启设备 / 关机）、系统更新检查、进程 Top5
- **实时负载**：CPU / 内存双线曲线，3 秒一采样、只看最近 3 分钟，离开页面立即停（单条 SSH 长连接）
- **系统调优卡**：显示 BBR / fq / 日志限额与占用 / DNS 缓存 / AP 频段 / 开机自愈，可重新应用或撤销
- **变更守卫**：切模式前布防，120 秒内不确认自动回滚（对齐 VyOS commit-confirm）
- **快照软回滚**：先存 `pre-restore`，恢复时不重启 NetworkManager（对齐 OPNsense restore + apply）
- **配置审计**：`/etc` 关键目录每小时快照进本地 git（`/var/lib/grayrouter/etcsnap`）

### 修复
- 优化配置（BBR / 日志限额 / DNS 缓存 / 5G 频段）改为 oneshot 单元，重启不再丢失
- DNS 生效改用 `nmcli general reload dns-rc`，不再重启 NetworkManager（不断网）
- 诊断改为并发 + 网关 6 包 + 可取消 + 非模态，修掉「程序卡死 / 丢包 33%」误判
- ToolTip 深底深字看不见（全局 TextBlock 样式覆盖气泡文字颜色）
- 流量页空状态把 TextBlock 插入 DataGrid 导致 `ItemsSource` 异常
- 设备显示名统一走 `DisplayName()`，无信息量名字退回 IP

### 校验
```
SHA256(GrayRouter-1.51.1.zip) = 5D78B773252E7D43442C7F376D3EFBE2BCFA65DB0DA3760299DD900BCEBBF3DD
SHA256(NetPanel.exe)          = 6F81BAF6CBBD83E3DF235E1FF7EEFCFBA2FF243BA5DE79AB71F975B4FD449E22
```

### 升级
解压覆盖后执行 `sudo bash install.sh`（会重新安装 bin 脚本与 systemd 单元），面板 exe 直接替换即可。
