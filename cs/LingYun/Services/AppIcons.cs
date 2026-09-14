using System.IO;
using SkiaSharp;

namespace LingYun.Services;

/// <summary>应用图标封面：内置资源 → 品牌色块。</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, (SKColor color, string letter)> Brand = new()
    {
        ["msedge"] = (new SKColor(0x0c, 0x59, 0xa4), "E"),
        ["chrome"] = (new SKColor(0x42, 0x85, 0xf4), "C"),
        ["firefox"] = (new SKColor(0xff, 0x71, 0x39), "F"),
        ["spotify"] = (new SKColor(0x1d, 0xb9, 0x54), "S"),
        ["cloudmusic"] = (new SKColor(0xc2, 0x0c, 0x0c), "云"),
        ["vlc"] = (new SKColor(0xff, 0x88, 0x00), "V"),
        ["bilibili"] = (new SKColor(0x00, 0xae, 0xec), "B"),
        ["potplayer"] = (new SKColor(0xd8, 0x4b, 0x1e), "P"),
        ["qqmusic"] = (new SKColor(0x31, 0xc2, 0x7b), "音"),
        ["kugou"] = (new SKColor(0x00, 0x9a, 0xff), "酷"),
        ["douyin"] = (new SKColor(0x20, 0x20, 0x24), "抖"),
    };

    /// <summary>
    /// 这些应用的 SMTC 会话通常不带封面（或带的是网页缩略图），内置站标比会话缩略图更贴切，
    /// 因此绘制时优先用内置图标 —— 对应参考实现的 Always 策略。
    /// </summary>
    private static readonly HashSet<string> PreferBundledKeys = new() { "bilibili", "potplayer" };

    /// <summary>该应用是否应优先用内置图标（而非系统会话缩略图）。</summary>
    internal static bool PreferBundled(string appId) => PreferBundledKeys.Contains(BrandKey(appId));

    private static string AssetsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var p = Path.Combine(baseDir, "Assets");
        return Directory.Exists(p) ? p : Path.Combine(baseDir, "assets");
    }

    public static SKBitmap? TryLoad(string appId, int size)
    {
        var key = BrandKey(appId);
        if (key.Length == 0) return null;
        try
        {
            using var stream = OpenLogo(key);
            if (stream is null) return null;
            using var bmp = SKBitmap.Decode(stream);
            if (bmp is null) return null;
            return bmp.Resize(new SKImageInfo(size, size), SKFilterQuality.High);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 站标来源：先找 exe 同目录 Assets/{key}.png（用户可自行替换），
    /// 再退回内嵌资源（单文件 exe 被单独拷走时仍可用）。
    /// </summary>
    private static Stream? OpenLogo(string key)
    {
        var path = Path.Combine(AssetsDir(), key + ".png");
        if (File.Exists(path)) return File.OpenRead(path);
        return typeof(AppIcons).Assembly
            .GetManifestResourceStream($"LingYun.Assets.{key}.png");
    }

    public static void DrawFallback(SKCanvas canvas, SKRect rect, string appId)
    {
        var key = BrandKey(appId);
        SKColor color = new(0x44, 0x44, 0x44);
        string letter = "♪";
        if (key.Length > 0 && Brand.TryGetValue(key, out var b))
        {
            color = b.color;
            letter = b.letter;
        }
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(rect, rect.Width * 0.22f, rect.Height * 0.22f, paint);
        using var text = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = rect.Height * 0.42f,
            TextAlign = SKTextAlign.Center,
            // 品牌字里有「云」和 ♪，Segoe UI 画不出来
            Typeface = AppFonts.Pick(letter, SKFontStyleWeight.Bold),
        };
        canvas.DrawText(letter, rect.MidX, rect.MidY + text.TextSize * 0.35f, text);
    }

    private static string BrandKey(string appId)
    {
        var a = appId.ToLowerInvariant();
        foreach (var k in Brand.Keys)
            if (a.Contains(k)) return k;
        return "";
    }

    /// <summary>
    /// AUMID → 友好名，给展开面板的「来源」切换器用（多个会话时区分播放器）。
    /// 关键词表与 <see cref="Brand"/> 同源，命中不了就退回原 AppId 的短名。
    /// </summary>
    internal static string FriendlyName(string appId)
    {
        var key = BrandKey(appId);
        if (key.Length > 0 && Friendly.TryGetValue(key, out var name)) return name;
        if (string.IsNullOrWhiteSpace(appId)) return "未知来源";
        // AUMID 形如 "MSEdge!App" / "cloudmusic.exe"：取 ! 前、去扩展名。
        // 这里不做截断——显示宽度由绘制方决定，避免两处各截一刀
        return appId.Split('!')[0].Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> Friendly = new()
    {
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["spotify"] = "Spotify",
        ["cloudmusic"] = "网易云",
        ["vlc"] = "VLC",
        ["qqmusic"] = "QQ音乐",
        ["bilibili"] = "哔哩哔哩",
        ["potplayer"] = "PotPlayer",
        ["douyin"] = "抖音",
    };
}
