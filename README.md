# MicTip

Windows 麦克风静音工具 —— 全局快捷键静音麦克风，静音时显示悬浮窗提醒，并通过 Windows 原生音量接口操作（即声音设置里的那个静音）。

始终以管理员权限运行，确保在全屏游戏中也能响应快捷键。**不使用 AutoHotkey**，避免被部分反作弊误判。

## 功能

- **切换静音**（默认 `Ctrl+Alt+M`）：切换麦克风的静音基线
- **Push-to-talk**（默认鼠标侧键 Mouse4）：仅在已静音时按住说话，松开恢复静音
- **悬浮窗**：仅在静音基线开启时显示，显示设备名 + 实时音量条 + 状态图标
  - 静音时红 + 🔇「已静音」
  - PTT 说话中绿 + 🎤「说话中」
  - 可拖动，位置自动记忆
- **托盘图标**三态：开启（绿）/ 静音（红）/ 断开（灰⚠）
- **设备策略**：跟随默认通讯设备 / 默认设备 / 指定设备 / 静音全部
- **设备热插拔**：插拔/切换默认设备时自动跟上，设备缺失时进入断开态
- **外部静音同步**：手动点系统音量图标静音，状态实时同步
- **开机自启**：通过任务计划程序实现，开机启动时免 UAC 弹窗

## 使用

### 运行

发布版是单个 `MicTip.exe`（约 69 MB，self-contained，无需安装 .NET 运行时），双击运行会弹 UAC 确认（因为需要管理员权限）。

启动后在系统托盘出现麦克风图标：
- **左键单击托盘图标**：切换静音
- **右键托盘图标**：打开菜单（设置 / 开机自启 / 退出）

### 设置

右键托盘 → **设置**，可配置：
- 快捷键（点击输入框后按下想要的组合键，含鼠标侧键）
- 目标设备策略与指定设备
- 悬浮窗开关、显示设备名/音量条
- 开机自启

## 构建

需要 .NET 8 SDK。

```bash
# 构建
dotnet build src/MicTip -c Release

# 发布为单文件 (win-x64, self-contained)
dotnet publish src/MicTip -c Release
```

产物：`src/MicTip/bin/Release/net8.0-windows/win-x64/publish/MicTip.exe`

## 技术说明

| 关注点 | 方案 |
|--------|------|
| 游戏内可用快捷键 | 低级键盘/鼠标钩子 (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`)，进程内回调，不注入其它进程 |
| 麦克风控制 | NAudio 封装的 Core Audio API (`IAudioEndpointVolume`)，即 Windows 声音设置同源接口 |
| 外部静音同步 | `IAudioEndpointVolumeCallback` 回调 |
| 设备热插拔 | `IMMNotificationClient` 回调 + 防抖刷新 |
| 悬浮窗不抢焦点 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` |
| 免 UAC 自启 | 任务计划程序 `ONLOGON` + `HIGHEST` |
| 权限 | `app.manifest` 指定 `requireAdministrator` |

## 限制

- 极少数独占鼠标的游戏可能影响鼠标侧键 PTT（键盘 PTT 不受影响）
- 程序化生成的托盘图标较朴素，后续可替换为美术图标资源
