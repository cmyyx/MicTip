using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MicTip.Models;

namespace MicTip.UI.Icons;

/// <summary>图标与其 tooltip 文案的不可变组合。</summary>
public sealed record IconState(Icon Icon, string Tooltip);

/// <summary>
/// 程序化生成托盘图标。三态: 开启(绿)/静音(红)/断开(灰+感叹号)。
/// 不依赖外部 .ico 文件, 首阶段即可运行; 后续可替换为美术资源。
/// </summary>
public static class IconFactory
{
    private const int Size = 32;

    /// <summary>按状态返回图标与对应 tooltip 文案。</summary>
    public static IconState Build(MicState state, string? deviceName)
    {
        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            var (bg, fg, overlayText) = state switch
            {
                MicState.Live => (Color.FromArgb(34, 197, 94), Color.White, ""),       // 绿
                MicState.Muted => (Color.FromArgb(220, 38, 38), Color.White, ""),       // 红
                MicState.Disconnected => (Color.FromArgb(100, 116, 139), Color.White, "!"), // 灰
                _ => (Color.DarkGray, Color.White, ""),
            };

            // 圆角背景
            using var bgBrush = new SolidBrush(bg);
            var rect = new Rectangle(2, 2, Size - 4, Size - 4);
            g.FillRoundedRect(bgBrush, rect, 8);

            // 麦克风剪影 (简化的竖向胶囊 + 底座)
            using var fgPen = new Pen(fg, 2f);
            using var fgBrush = new SolidBrush(fg);
            // 麦克风头
            var head = new Rectangle(13, 6, 6, 11);
            g.FillRoundedRect(fgBrush, head, 3);
            // 支架弧
            g.DrawArc(fgPen, 9, 9, 14, 14, 0, 180);
            // 立柱
            g.DrawLine(fgPen, 16, 23, 16, 26);
            // 底座
            g.DrawLine(fgPen, 12, 26, 20, 26);

            // 静音斜杠
            if (state == MicState.Muted)
            {
                using var slashPen = new Pen(Color.White, 2.8f);
                slashPen.StartCap = LineCap.Round;
                slashPen.EndCap = LineCap.Round;
                g.DrawLine(slashPen, 5, 27, 27, 5);
            }

            if (!string.IsNullOrEmpty(overlayText))
            {
                // 断开状态: 右下感叹号
                using var warnBrush = new SolidBrush(Color.FromArgb(251, 191, 36));
                g.FillEllipse(warnBrush, 19, 19, 11, 11);
                using var exFont = new Font("Segoe UI", 7f, FontStyle.Bold);
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString(overlayText, exFont, Brushes.Black, new RectangleF(19, 19, 11, 11), sf);
            }
        }

        var hicon = bmp.GetHicon();
        var icon = Icon.FromHandle(hicon);
        var tooltip = state switch
        {
            MicState.Live => $"麦克风已开启{(deviceName is null ? "" : $" - {deviceName}")}",
            MicState.Muted => $"麦克风已静音{(deviceName is null ? "" : $" - {deviceName}")}",
            MicState.Disconnected => "未找到麦克风设备",
            _ => "MicTip",
        };
        return new IconState(icon, tooltip);
    }
}

/// <summary>圆角矩形辅助 (Graphics 没有原生方法)。</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush b, Rectangle r, int radius)
    {
        var path = BuildRoundedRect(r, radius);
        g.FillPath(b, path);
    }

    public static void DrawRoundedRect(this Graphics g, Pen p, Rectangle r, int radius)
    {
        var path = BuildRoundedRect(r, radius);
        g.DrawPath(p, path);
    }

    private static GraphicsPath BuildRoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
