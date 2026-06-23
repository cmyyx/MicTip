using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicTip.Audio;
using MicTip.Hotkeys;
using MicTip.Models;
using MicTip.Services;
// 本文件位于 MicTip.UI.Settings 命名空间, "Settings" 会被解析为该命名空间,
// 故给设置模型起别名以消歧。
using SettingsModel = MicTip.Models.Settings;

namespace MicTip.UI.Settings;

/// <summary>
/// 设置窗口。编辑 Settings 副本, 点击"确定"时回传。
/// 热键录入: 用 PreviewKeyDown 捕获, 支持修饰键组合与鼠标侧键占位文本。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsModel _edit;       // 编辑副本
    private readonly AudioDeviceManager _deviceManager;
    private readonly Action<SettingsModel> _apply;
    private readonly SettingsService _settingsService;

    // 捕获热键的输入框标识
    private TextBox? _capturingBox;
    private bool _loading = true;

    /// <param name="current">当前设置 (不会被修改)。</param>
    /// <param name="deviceManager">用于填充设备下拉。</param>
    /// <param name="apply">确定时回调, 传入新的设置对象。</param>
    /// <param name="settingsService">用于显示/操作配置存储位置。</param>
    public SettingsWindow(SettingsModel current, AudioDeviceManager deviceManager, Action<SettingsModel> apply, SettingsService settingsService)
    {
        _edit = CloneSettings(current);
        _deviceManager = deviceManager;
        _apply = apply;
        _settingsService = settingsService;
        InitializeComponent();

        LoadDeviceList();
        LoadFromEdit();
        RefreshConfigStorageUi();
        _loading = false;
    }

    private static SettingsModel CloneSettings(SettingsModel s)
    {
        // 简单深拷贝 (HotkeyDef 也是可变对象, 需重建)
        return new SettingsModel
        {
            ToggleHotkey = new HotkeyDef { Modifiers = s.ToggleHotkey.Modifiers, Key = s.ToggleHotkey.Key },
            PttHotkey = new HotkeyDef { Modifiers = s.PttHotkey.Modifiers, Key = s.PttHotkey.Key },
            DeviceStrategy = s.DeviceStrategy,
            SpecificDeviceId = s.SpecificDeviceId,
            OverlayEnabled = s.OverlayEnabled,
            OverlayPosition = s.OverlayPosition,
            OverlayX = s.OverlayX,
            OverlayY = s.OverlayY,
            ShowMeter = s.ShowMeter,
            ShowDeviceName = s.ShowDeviceName,
        };
    }

    // ===== 加载 =====

    private void LoadDeviceList()
    {
        DeviceBox.Items.Clear();
        foreach (var d in _deviceManager.EnumerateCaptureDevices())
        {
            var name = d.FriendlyName;
            DeviceBox.Items.Add(name);
            d.Dispose();
        }
    }

    private void LoadFromEdit()
    {
        ToggleHotkeyBox.Text = _edit.ToggleHotkey.ToString();
        PttHotkeyBox.Text = _edit.PttHotkey.ToString();
        OverlayEnabledBox.IsChecked = _edit.OverlayEnabled;
        ShowDeviceNameBox.IsChecked = _edit.ShowDeviceName;
        ShowMeterBox.IsChecked = _edit.ShowMeter;

        // 策略下拉
        for (int i = 0; i < StrategyBox.Items.Count; i++)
        {
            if (StrategyBox.Items[i] is ComboBoxItem item
                && item.Tag is DeviceStrategy ds && ds == _edit.DeviceStrategy)
            {
                StrategyBox.SelectedIndex = i;
                break;
            }
        }

        // 指定设备下拉
        if (!string.IsNullOrEmpty(_edit.SpecificDeviceId))
        {
            foreach (var item in DeviceBox.Items)
            {
                if (item is string s && s == _edit.SpecificDeviceId)
                {
                    DeviceBox.SelectedItem = item;
                    break;
                }
            }
        }
        UpdateDeviceBoxEnabled();

        // 悬浮窗位置下拉
        for (int i = 0; i < PositionBox.Items.Count; i++)
        {
            if (PositionBox.Items[i] is ComboBoxItem item
                && item.Tag is OverlayPosition op && op == _edit.OverlayPosition)
            {
                PositionBox.SelectedIndex = i;
                break;
            }
        }

        // 坐标
        OverlayXBox.Text = double.IsNaN(_edit.OverlayX) ? "" : _edit.OverlayX.ToString("F0");
        OverlayYBox.Text = double.IsNaN(_edit.OverlayY) ? "" : _edit.OverlayY.ToString("F0");
        UpdatePositionControlsEnabled();
    }

    private void UpdateDeviceBoxEnabled()
    {
        bool specific = _edit.DeviceStrategy == DeviceStrategy.Specific;
        DeviceBox.IsEnabled = specific;
        if (!specific) DeviceBox.SelectedIndex = -1;
    }

    // ===== 策略变更 =====

    private void OnStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (StrategyBox.SelectedItem is ComboBoxItem item && item.Tag is DeviceStrategy ds)
        {
            _edit.DeviceStrategy = ds;
            UpdateDeviceBoxEnabled();
        }
    }

    // ===== 热键捕获 =====

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        _capturingBox = (TextBox)sender;
        e.Handled = true;

        // IME (中文输入法) 开启时, 字母键会被 IME 拦截, e.Key 变成 ImeProcessed;
        // 真实按键在 e.ImeProcessedKey。同理 DeadCharProcessed / System。
        var key = e.Key == Key.System ? e.SystemKey
                : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key == Key.DeadCharProcessed ? e.DeadCharProcessedKey
                : e.Key;

        // 仅修饰键按下时不捕获, 等主键
        if (key == Key.LeftCtrl || key == Key.RightCtrl
            || key == Key.LeftShift || key == Key.RightShift
            || key == Key.LeftAlt || key == Key.RightAlt
            || key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // 修饰键
        var mods = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods |= KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods |= KeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) mods |= KeyModifiers.Win;

        // Key → VK
        int vk = KeyToVk(key);
        if (vk <= 0) return;

        var def = new HotkeyDef { Modifiers = mods, Key = vk };
        if (_capturingBox == ToggleHotkeyBox) _edit.ToggleHotkey = def;
        else if (_capturingBox == PttHotkeyBox) _edit.PttHotkey = def;
        _capturingBox.Text = def.ToString();
    }

    private static int KeyToVk(Key key)
    {
        // WPF Key 枚举值与虚拟键码基本一致 (字母数字区), 用 KeyInterop 转换最稳妥
        var vk = KeyInterop.VirtualKeyFromKey(key);
        return vk;
    }

    private void OnClearToggle(object sender, RoutedEventArgs e)
    {
        _edit.ToggleHotkey = HotkeyDef.Empty;
        ToggleHotkeyBox.Text = HotkeyDef.Empty.ToString();
    }

    private void OnClearPtt(object sender, RoutedEventArgs e)
    {
        _edit.PttHotkey = HotkeyDef.Empty;
        PttHotkeyBox.Text = HotkeyDef.Empty.ToString();
    }

    // ===== 悬浮窗位置 =====

    private void OnPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PositionBox.SelectedItem is ComboBoxItem item && item.Tag is OverlayPosition op)
        {
            _edit.OverlayPosition = op;
            UpdatePositionControlsEnabled();
        }
    }

    private void UpdatePositionControlsEnabled()
    {
        if (OverlayXBox == null || OverlayYBox == null || CenterHorizontallyBtn == null || CenterVerticallyBtn == null) return;
        bool isCustom = _edit.OverlayPosition == OverlayPosition.Custom;
        OverlayXBox.IsEnabled = isCustom;
        OverlayYBox.IsEnabled = isCustom;
        CenterHorizontallyBtn.IsEnabled = isCustom;
        CenterVerticallyBtn.IsEnabled = isCustom;
    }

    private void OnCoordinatesChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (OverlayXBox == null || OverlayYBox == null) return;

        if (double.TryParse(OverlayXBox.Text, out double x))
            _edit.OverlayX = x;
        else
            _edit.OverlayX = double.NaN;

        if (double.TryParse(OverlayYBox.Text, out double y))
            _edit.OverlayY = y;
        else
            _edit.OverlayY = double.NaN;
    }

    private void OnCenterHorizontally(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        // 悬浮窗宽为 300, 阴影边界约为 10
        double x = work.Left + (work.Width - 300) / 2;
        OverlayXBox.Text = x.ToString("F0");
        PositionBox.SelectedIndex = 4; // 强制切到自定义
    }

    private void OnCenterVertically(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        // 悬浮窗高为 104, 阴影边界约为 10
        double y = work.Top + (work.Height - 104) / 2;
        OverlayYBox.Text = y.ToString("F0");
        PositionBox.SelectedIndex = 4; // 强制切到自定义
    }

    // ===== 确定 / 取消 =====

    private void OnOK(object sender, RoutedEventArgs e)
    {
        // Specific 策略必须选了设备
        if (_edit.DeviceStrategy == DeviceStrategy.Specific && string.IsNullOrEmpty(DeviceBox.Text))
        {
            MessageBox.Show("选择了“指定设备”策略, 请先在下拉中选择一个设备。", "MicTip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 提交编辑
        if (_edit.DeviceStrategy == DeviceStrategy.Specific)
            _edit.SpecificDeviceId = DeviceBox.Text;
        _edit.OverlayEnabled = OverlayEnabledBox.IsChecked == true;
        _edit.ShowDeviceName = ShowDeviceNameBox.IsChecked == true;
        _edit.ShowMeter = ShowMeterBox.IsChecked == true;

        _apply(_edit);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ===== 配置存储 =====

    /// <summary>刷新配置存储位置 UI (路径 + 便携模式按钮文案)。</summary>
    private void RefreshConfigStorageUi()
    {
        ConfigPathText.Text = _settingsService.CurrentFilePath;
        TogglePortableBtn.Content = _settingsService.IsPortable ? "退出便携" : "启用便携";
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // 确保目录存在 (便携模式下 exe 目录必然存在, 这里防御性处理 roaming 情况)
            var dir = _settingsService.CurrentDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    private void OnTogglePortable(object sender, RoutedEventArgs e)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir == null) return;
        var portableFile = Path.Combine(exeDir, "settings.json");

        try
        {
            if (_settingsService.IsPortable)
            {
                // 退出便携: 删除 exe 同目录的 settings.json
                // (下次启动自动回退到 %AppData%)
                if (File.Exists(portableFile)) File.Delete(portableFile);
            }
            else
            {
                // 启用便携: 把当前配置复制到 exe 同目录
                // (若 roaming 不存在则写一份默认值)
                var current = _settingsService.Load();
                // 把当前编辑中的设置也一并写入, 避免丢失未保存的修改
                _settingsService.Save(_edit);
                // 此时 Save 仍写到 roaming, 手动复制到 exe 目录
                File.Copy(_settingsService.CurrentFilePath, portableFile, overwrite: true);
            }
            RefreshConfigStorageUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"切换便携模式失败: {ex.Message}", "MicTip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
