## 1.51.3 · 安装器修复

这一版只修安装器（面板本体功能未变），影响**新装机器**，本机无需重装。

### 修复
- **限速单元起不来**：`grayrouter-limit.service` 的 ExecStart 写死 `/usr/local/bin/grayrouter-limit.sh`，而安装器把脚本装进 `/opt/grayrouter/bin`。现在跟随 `$GR_OPT` 并带兜底路径
- **安装器少装 8 个单元**：新增 `install-extras.sh` 与 `etc/units/` 15 个模板，补齐 sqm / opt / audit / devices / lan2 / watch / traffic / beacon / wanfail / usbnet@
- **122 处硬编码 `/usr/local/bin`**：安装后自动建符号链接指向 `/opt/grayrouter/bin`，两种布局通用；卸载时清理链接
- **包内 SHA256SUMS 是旧版的**（校验必然失败）：改为逐文件哈希

### 面板
- 内嵌的部署包从 1.50 更新到 1.51.3（此前面板「一键部署」装的是旧脚本）

### 校验
```
GrayRouter-1.51.3.zip   sha256 1b3769e9ef04e785828eaf7f805ea0aae187a8431151e89421826bd0825e29ca
NetPanel.exe            sha256 a4d84614bcb2c06bc271d4a2d99bef7998b4ebbaab7cf649d3c18eaee0b0a00
```
