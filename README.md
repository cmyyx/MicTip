# MicTip

Windows 麦克风静音工具 —— 全局快捷键静音麦克风，静音时显示悬浮窗提醒，并通过 Windows 原生音量接口操作（即声音设置里的那个静音）。

始终以管理员权限运行，确保在全屏游戏中也能响应快捷键。

## 功能

### 静音控制

- **切换静音**（默认 `Ctrl+Alt+M`）：切换麦克风的静音基线
- **Push-to-talk**（默认鼠标侧键 Mouse4）：仅在已静音时按住说话，松开恢复静音
- **外部静音同步**：手动点系统音量图标静音，状态实时同步
- **设备热插拔**：插拔/切换默认设备时自动跟上，设备缺失时进入断开态
- **设备策略**：跟随默认通讯设备 / 默认设备 / 指定设备 / 静音全部

### 悬浮窗

仅在静音基线开启时显示，显示设备名 + 实时音量条 + 状态图标：

- 静音时红 + 🔇「已静音」
- PTT 说话中绿 + 🎤「说话中」
- 点击穿透、不抢焦点、不可交互，位置通过设置窗口配置（四角对齐 / 自定义坐标 / 居中）

### 无声提醒

当麦克风非静音但长时间收不到任何声音，且电脑处于活跃状态时，弹出悬浮窗提醒（琥珀色样式），提示你说句话以确认麦克风是否正常工作：

- **有任何声音即自动消失**
- **按切换静音的快捷键可快速关闭本次提醒**（不会切换静音状态，重新开始计时）
- 仅在麦克风非静音时检测
- 电脑非活跃（如看电影/离开）时不累积计时，避免误触发
- 托盘右键菜单可暂停（1 小时 / 4 小时 / 今日不再）或永久关闭

### 其他

- **托盘图标**三态：开启（绿）/ 静音（红）/ 断开（灰⚠）
- **更新检查**：启动时自动检查 GitHub Release 新版本，发现新版本时弹出托盘通知，点击跳转下载页；可在设置中关闭
- **关于窗口**：托盘右键 → 关于，可查看版本、访问仓库、手动检查更新
- **单实例保护**：重复启动自动退出

## 使用

### 下载

前往 [Releases](https://github.com/cmyyx/MicTip/releases) 下载最新版本：

| 版本 | 体积 | 要求 |
|------|------|------|
| **自包含** | ~69 MB | 无需安装任何依赖 |
| **框架依赖** | ~1 MB | 需安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |

双击 `MicTip.exe` 运行会弹 UAC 确认（因为需要管理员权限）。

### 操作

启动后在系统托盘出现麦克风图标：

- **左键单击托盘图标**：切换静音
- **右键托盘图标**：打开菜单
  - **设置…**：配置快捷键、设备、悬浮窗、无声提醒等
  - **无声提醒 ▶**：暂停 / 恢复 / 永久关闭
  - **打开配置目录…**
  - **关于…**：版本信息、仓库地址、检查更新
  - **退出**

### 设置

右键托盘 → **设置**，可配置：

- 快捷键（点击输入框后按下想要的组合键，含鼠标侧键）
- 目标设备策略与指定设备
- 悬浮窗开关与位置
- 无声提醒开关、触发时长
- 启动时自动检查更新
- 便携模式（exe 同目录放 `settings.json` 即启用，配置随程序走）

## 构建

需要 .NET 8 SDK。

```bash
# 构建
dotnet build src/MicTip -c Release

# 发布 (自包含 + 框架依赖)
./publish.ps1

# 或单独发布:
# 自包含 (~69 MB, 无需 .NET 运行时)
dotnet publish src/MicTip -p:PublishProfile=SelfContained
# 框架依赖 (~1 MB, 需 .NET 8 桌面运行时)
dotnet publish src/MicTip -p:PublishProfile=FrameworkDependent
```

产物：

- `src/MicTip/bin/publish/self-contained/MicTip.exe`
- `src/MicTip/bin/publish/framework-dependent/MicTip.exe`

## 发版

推送 `v*` 开头的 tag（如 `v0.1.0`）即可触发 GitHub Actions 自动构建并发布 Release：

```bash
git tag v0.1.0
git push origin v0.1.0
```

Actions 会从 tag 提取版本号注入程序集，构建两个版本并上传到 GitHub Release。

## 技术说明

| 关注点 | 方案 |
|--------|------|
| 游戏内可用快捷键 | 低级键盘/鼠标钩子 (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`)，进程内回调，不注入其它进程 |
| 麦克风控制 | NAudio 封装的 Core Audio API (`IAudioEndpointVolume`)，即 Windows 声音设置同源接口 |
| 外部静音同步 | `IAudioEndpointVolumeCallback` 回调 |
| 设备热插拔 | `IMMNotificationClient` 回调 + 防抖刷新 |
| 悬浮窗不抢焦点 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` + `WS_EX_TRANSPARENT`（点击穿透） |
| 电脑活跃检测 | Win32 `GetLastInputInfo`（过去 30s 内有键鼠输入视为活跃） |
| 更新检查 | GitHub Releases API (`/repos/cmyyx/MicTip/releases/latest`) |
| 权限 | `app.manifest` 指定 `requireAdministrator` |