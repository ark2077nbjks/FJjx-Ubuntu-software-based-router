## 1.51.2 — 频段持久化修复

发布日期：2026-10-31

### 修复
- **AP 频段重启后回落到 2.4G**：根因是 Ubuntu 26.04 的 AP 连接由 netplan 在 `/run` 下重建，`nmcli con modify` 的改动不持久。现在 `set-band.sh` 切换成功后写 `/etc/grayrouter/band.env`，`grayrouter-band.service` 开机后补一次；起不来自动回退 2.4G 并改写 env（对齐 OpenWrt auto-fallback）
- **仓库缺 `grayrouter-audit.sh`**：此前只存在于实机，新装用户执行安装会失败，已补入
- **审计不记录频段**：新增 `ap-band.txt`（只含 SSID / band / channel，不含密码），频段被改可追溯
- **`install.sh` 少装 `grayrouter-band.service`**：已补，新装机器自带

### 实机验证（J4125 · Ubuntu 26.04.1）
- 重启后频段保持在 `5 GHz / ch36`，开机日志：`开机恢复 a/36（当前 6）→ 恢复成功`
- 0 失败单元；bbr / fq / journal 196.5 MB / DNS 缓存 1000 全部自愈
- 外网 47.7 ms；审计提交含 `BAND=a CHANNEL=36 RUNTIME_CHANNEL=36`

### 校验
```
SHA256(GrayRouter-1.51.2.zip) = 2008D349A3EEBA2B33669B16B8A6EDCBDBD9A2E2730207D689C10C4E56C5E556
SHA256(NetPanel.exe)          = D3703A4DFFDA340D7DF2D351280CBC3CDEC8AC54EEDA3E1A445A2A4360145909
```

### 升级
解压覆盖后 `sudo bash install.sh`；面板 exe 直接替换。
