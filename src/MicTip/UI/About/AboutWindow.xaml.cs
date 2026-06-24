using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using MicTip.Services;

namespace MicTip.UI.About;

/// <summary>
/// 关于窗口。显示版本/简介/仓库地址, 并支持手动检查更新。
/// </summary>
public partial class AboutWindow : Window
{
    private readonly UpdateChecker _checker = new();

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{UpdateChecker.CurrentVersion}";
        UpdateStatusText.Text = "";
    }

    private void OnRepoLinkClick(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
        e.Handled = true;
    }

    private bool _checking;
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        _checking = true;
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        UpdatePanel.Visibility = Visibility.Collapsed;

        var result = await _checker.CheckAsync();

        CheckUpdateBtn.IsEnabled = true;
        _checking = false;

        if (result.Error != null)
        {
            UpdateStatusText.Text = $"检查失败: {result.Error}";
            return;
        }

        if (result.HasUpdate)
        {
            UpdateStatusText.Text = "";
            UpdatePanel.Visibility = Visibility.Visible;
            UpdateDetailText.Text = $"发现新版本 v{result.LatestVersion}，点击前往下载";
            // 点击面板跳转 release 页
            UpdatePanel.MouseLeftButtonUp += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(result.ReleaseUrl!) { UseShellExecute = true }); }
                catch { /* 忽略 */ }
            };
            UpdatePanel.Cursor = System.Windows.Input.Cursors.Hand;
        }
        else
        {
            UpdateStatusText.Text = "已是最新版本";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
