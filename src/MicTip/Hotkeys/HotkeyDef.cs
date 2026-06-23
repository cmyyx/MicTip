namespace MicTip.Hotkeys;

/// <summary>
/// 修饰键组合 (Windows 键盘修饰符)。
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// 一个完整的快捷键定义: 修饰键 + 主键。
/// 主键既可以是 Windows 虚拟键码 (VK_*), 也可以是鼠标侧键 (MouseX1/MouseX2)。
/// </summary>
public sealed class HotkeyDef
{
    /// <summary>修饰键组合。</summary>
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.None;

    /// <summary>
    /// 主键。使用 Windows 虚拟键码 (字母用大写 ASCII, 如 'M' = 0x4D)。
    /// 鼠标侧键用专用占位: MouseX1 = -1, MouseX2 = -2。
    /// </summary>
    public int Key { get; set; }

    /// <summary>鼠标侧键 X1 占位值。</summary>
    public const int MouseX1 = -1;

    /// <summary>鼠标侧键 X2 占位值。</summary>
    public const int MouseX2 = -2;

    public bool IsMouse => Key == MouseX1 || Key == MouseX2;

    /// <summary>空快捷键 (未配置)。</summary>
    public static HotkeyDef Empty => new() { Key = 0 };

    public bool IsEmpty => Key == 0;

    public override bool Equals(object? obj) =>
        obj is HotkeyDef o && o.Key == Key && o.Modifiers == Modifiers;

    public override int GetHashCode() => HashCode.Combine((int)Modifiers, Key);

    public override string ToString()
    {
        if (IsEmpty) return "(未设置)";
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyToString(Key));
        return string.Join("+", parts);
    }

    public static string KeyToString(int key)
    {
        switch (key)
        {
            case MouseX1: return "Mouse4";
            case MouseX2: return "Mouse5";
        }

        if (key <= 0 || key > 255) return "?";

        try
        {
            var wpfKey = System.Windows.Input.KeyInterop.KeyFromVirtualKey(key);
            if (wpfKey != System.Windows.Input.Key.None)
            {
                switch (wpfKey)
                {
                    case System.Windows.Input.Key.D0: case System.Windows.Input.Key.NumPad0: return "0";
                    case System.Windows.Input.Key.D1: case System.Windows.Input.Key.NumPad1: return "1";
                    case System.Windows.Input.Key.D2: case System.Windows.Input.Key.NumPad2: return "2";
                    case System.Windows.Input.Key.D3: case System.Windows.Input.Key.NumPad3: return "3";
                    case System.Windows.Input.Key.D4: case System.Windows.Input.Key.NumPad4: return "4";
                    case System.Windows.Input.Key.D5: case System.Windows.Input.Key.NumPad5: return "5";
                    case System.Windows.Input.Key.D6: case System.Windows.Input.Key.NumPad6: return "6";
                    case System.Windows.Input.Key.D7: case System.Windows.Input.Key.NumPad7: return "7";
                    case System.Windows.Input.Key.D8: case System.Windows.Input.Key.NumPad8: return "8";
                    case System.Windows.Input.Key.D9: case System.Windows.Input.Key.NumPad9: return "9";
                    
                    case System.Windows.Input.Key.Back: return "Backspace";
                    case System.Windows.Input.Key.Escape: return "Esc";
                    case System.Windows.Input.Key.Capital: return "CapsLock";
                    case System.Windows.Input.Key.Next: return "PageDown";
                    case System.Windows.Input.Key.Prior: return "PageUp";
                    
                    case System.Windows.Input.Key.OemQuestion: return "/";
                    case System.Windows.Input.Key.OemQuotes: return "'";
                    case System.Windows.Input.Key.OemSemicolon: return ";";
                    case System.Windows.Input.Key.OemOpenBrackets: return "[";
                    case System.Windows.Input.Key.OemCloseBrackets: return "]";
                    case System.Windows.Input.Key.OemPipe: return "\\";
                    case System.Windows.Input.Key.OemComma: return ",";
                    case System.Windows.Input.Key.OemPeriod: return ".";
                    case System.Windows.Input.Key.OemMinus: return "-";
                    case System.Windows.Input.Key.OemPlus: return "=";
                    case System.Windows.Input.Key.OemTilde: return "`";
                    
                    default:
                        return wpfKey.ToString();
                }
            }
        }
        catch
        {
            // fallback
        }

        return FormattableString.Invariant($"VK(0x{key:X2})");
    }
}
