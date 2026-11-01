## 1.51.4 · 收尾修复

### 修复
- **审计仓库属主**：`/var/lib/grayrouter/etcsnap` 归 root，普通用户 `git log` 报 dubious ownership。现在 `audit init` 把属主交还 `$SUDO_USER` 并写 `safe.directory`
- **多余的跨段 nft 表**：`forward` 策略为 `accept` 时跨段本来互通，`lan-usb.sh rules` 不再建 `gray_lan2`，并清理旧表
- **`opt-backup` 无限增长**：每次开机 `apply` 新增一份，现在只留最近 2 份

### 面板
- 内嵌部署包更新到 1.51.4（含以上三项）

### 校验
```
GrayRouter-1.51.4.zip  B146D0D927D2BB1F32DDA3DEBF4FB6A37E36DB333B4ED7FF35A545C16E511327
NetPanel.exe           035820530034BACF701BA8B40CF9E4CCC207C545B3A02FE32DB25B8964631D51
```
