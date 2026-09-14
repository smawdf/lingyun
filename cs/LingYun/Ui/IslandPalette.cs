using SkiaSharp;

namespace LingYun.Ui;

/// <summary>
/// 岛体配色（深/浅两套）。所有绘制都从这里取色——以前散在各 draw 方法里的硬编码颜色
/// 导致「配置里有 theme 字段但原生岛根本不理会」，浅色主题完全无效。
/// </summary>
internal readonly record struct IslandPalette(
    SKColor Body, SKColor Fg, SKColor Sub, SKColor Dim, SKColor Accent,
    SKColor Track, SKColor Card, SKColor Shadow, SKColor Ok, SKColor Danger, SKColor Warn, bool Dark)
{
    public static IslandPalette For(string? theme, int opacityPercent = 100)
    {
        var p = ResolveLight(theme, SystemUsesLightTheme()) ? LightTheme : DarkTheme;
        if (opacityPercent >= 100) return p;
        // 只压背景类颜色：文字/强调色保持不透明，透明度调低也不会看不清字
        double a = Math.Clamp(opacityPercent, 40, 100) / 100.0;
        return p with
        {
            Body = ScaleAlpha(p.Body, a),
            Card = ScaleAlpha(p.Card, a),
            Track = ScaleAlpha(p.Track, a),
            Shadow = ScaleAlpha(p.Shadow, a),
        };
    }

    /// <summary>主题解析（纯函数，自测用）：light 恒浅；system 跟随系统；其余深色。</summary>
    internal static bool ResolveLight(string? theme, bool systemIsLight)
        => string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase) && systemIsLight);

    /// <summary>系统当前是否浅色（读 Windows 个性化设置；读不到按深色）。</summary>
    internal static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    private static SKColor ScaleAlpha(SKColor c, double a)
        => c.WithAlpha((byte)Math.Round(c.Alpha * a));

    /// <summary>深色：纯黑（#000000，用户指定）——OLED 屏更省电、与任务栏/暗色壁纸更融合。</summary>
    public static readonly IslandPalette DarkTheme = new(
        Body: new SKColor(0, 0, 0),
        Fg: SKColors.White,
        Sub: new SKColor(0xa0, 0xa0, 0xa0),
        Dim: new SKColor(0x8c, 0x8c, 0x8c),
        Accent: new SKColor(0x60, 0xcd, 0xff),
        Track: new SKColor(255, 255, 255, 36),
        Card: new SKColor(255, 255, 255, 17),
        Shadow: new SKColor(0, 0, 0, 55),
        Ok: new SKColor(0x4a, 0xd9, 0x7a),
        Danger: new SKColor(0xff, 0x6b, 0x6b),
        Warn: new SKColor(0xfc, 0xe1, 0x00),
        Dark: true);

    /// <summary>浅色：暖白面板 + 深灰墨色；强调/状态色都换成浅底上对比度足够的版本。</summary>
    public static readonly IslandPalette LightTheme = new(
        Body: new SKColor(0xf5, 0xf6, 0xf8),
        Fg: new SKColor(0x18, 0x18, 0x1a),
        Sub: new SKColor(0x5c, 0x5c, 0x66),
        Dim: new SKColor(0x6b, 0x6b, 0x75),
        Accent: new SKColor(0x0a, 0x7a, 0xf0),
        Track: new SKColor(0, 0, 0, 33),
        Card: new SKColor(0, 0, 0, 13),
        Shadow: new SKColor(0, 0, 0, 45),
        Ok: new SKColor(0x14, 0x8f, 0x45),
        Danger: new SKColor(0xcc, 0x2b, 0x2b),
        Warn: new SKColor(0xa8, 0x6a, 0x00),
        Dark: false);

    /// <summary>WCAG 相对亮度（自测用：断言两套配色的前景/背景对比度达标）。</summary>
    internal static double RelativeLuminance(SKColor c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }

    /// <summary>对比度（1..21）：用于自测确保浅色主题的文字真的看得清。</summary>
    internal static double Contrast(SKColor a, SKColor b)
    {
        double l1 = RelativeLuminance(a), l2 = RelativeLuminance(b);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }
}
