using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingYun.Config;

public sealed class TaskItem
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string Color { get; set; } = "#00A0FF";
    public string Time { get; set; } = "N/A";
    /// <summary>日程是否已完成（展开页点勾选圈切换）。</summary>
    public bool Done { get; set; }
}

/// <summary>快捷页的自定义程序（「＋ 添加程序」写入 quick_custom；单击启动）。</summary>
public sealed class QuickCustomApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Color { get; set; } = "#38bdf8";
}

public sealed class AppConfig
{
    public int ToolVersion { get; set; } = 2;
    public bool Enabled { get; set; }
    public List<int> Weekdays { get; set; } = new();
    public List<string> Dates { get; set; } = new();
    public int Hour { get; set; } = 23;
    public int Minute { get; set; }
    public string Theme { get; set; } = "dark";
    /// <summary>岛显示到哪块显示器（-1 = 跟随主显示器）。</summary>
    public int MonitorIndex { get; set; } = -1;
    public string Action { get; set; } = "shutdown";
    public bool AlsoPauseMedia { get; set; }
    public string Location { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public List<TaskItem> Tasks { get; set; } = new();
    /// <summary>快捷页自定义程序（最多 3 个，占安全行；危险动作永远独占最后一行）。</summary>
    public List<QuickCustomApp> QuickCustoms { get; set; } = new();
    public string AnimationStyle { get; set; } = "Fluid Blobs";
    /// <summary>媒体胶囊上显示真实音频频谱（WASAPI 环回采样）；关掉则用假动画。</summary>
    public bool Spectrum { get; set; } = true;
    /// <summary>性能页是否显示实时下载/上传网速卡。</summary>
    public bool PerfNetwork { get; set; } = true;
    /// <summary>紧凑胶囊缩放（1.0 = 240x52；允许 0.8–1.5）。</summary>
    public double CompactScale { get; set; } = 1.0;
    /// <summary>展开面板缩放（1.0 = 460x320；允许 0.95–1.25，Alert/Confirm 随之等比）。</summary>
    public double ExpandedScale { get; set; } = 1.0;
    /// <summary>岛相对屏幕工作区中心的水平偏移（px，负=左移）。</summary>
    public int OffsetX { get; set; }
    /// <summary>岛距工作区顶部的距离（px，默认 8）。</summary>
    public int OffsetY { get; set; } = 8;
    /// <summary>展开媒体页显示在线歌词（LRCLIB）；关掉则不请求也不显示。</summary>
    public bool Lyrics { get; set; } = true;
    /// <summary>系统通知弹窗：有通知时接管胶囊（约 6 秒），点击唤醒对应应用。</summary>
    public bool Toast { get; set; } = true;
    /// <summary>组合模式：胶囊里同时显示多个模块（时间/硬件/媒体），宽度随内容自适应。</summary>
    public bool Composite { get; set; }
    /// <summary>组合模式模块：时间。</summary>
    public bool CompositeClock { get; set; } = true;
    /// <summary>组合模式模块：CPU/内存占用。</summary>
    public bool CompositeHardware { get; set; } = true;
    /// <summary>组合模式模块：媒体（封面 + 歌词/标题 + 频谱；无会话时不显示）。</summary>
    public bool CompositeMedia { get; set; } = true;
    /// <summary>歌词卡拉OK逐字：当前句按播放进度从左到右点亮（关掉则整句一个颜色）。</summary>
    public bool LyricsKaraoke { get; set; } = true;
    /// <summary>歌词延迟补偿（毫秒，正 = 歌词提前）：显示器/音频链路有延迟时手动校准。</summary>
    public int LyricDelayMs { get; set; }
    /// <summary>背景不透明度（40–100%，只作用于背景类颜色，文字不变）。</summary>
    public int Opacity { get; set; } = 100;
    /// <summary>
    /// 设置窗口材质：acrylic 亚克力（系统真模糊）/ glass 液态玻璃（近似，WPF 做不了折射）/
    /// classic 原生 Windows（纯色 + 系统控件）。岛的材质在 <see cref="Theme"/> 里选。
    /// </summary>
    public string UiMaterial { get; set; } = "acrylic";
    /// <summary>
    /// 液态玻璃自适应（默认开）：抓岛背后那一小块桌面算亮度，自动在浅色玻璃（深字）
    /// 与深色玻璃（白字）之间切换，保证任何壁纸下文字都清楚。只对 theme=liquid-glass 生效。
    /// </summary>
    public bool GlassAdaptive { get; set; } = true;
    /// <summary>闲置自动隐藏：无媒体且鼠标离开 10 秒后收起岛，光标压到屏幕顶部恢复。</summary>
    public bool AutoHide { get; set; }
    /// <summary>
    /// 展开媒体页样式：a=精修（默认，结构同旧版、质感重做）/ b=沉浸（封面模糊铺满 + 玻璃控制条）/
    /// c=氛围海报（封面取色双光斑 + 大标题）。设置窗口「媒体页」可实时切换。
    /// </summary>
    public string MediaStyle { get; set; } = "a";

    [JsonIgnore]
    public bool HasRules => Weekdays.Count > 0 || Dates.Count > 0;
}

public static class ConfigStore
{
    public static string DefaultPath()
    {
        var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                  ?? AppContext.BaseDirectory;
        return System.IO.Path.Combine(dir, "灵云配置.json");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        var legacy = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!, "早睡配置.json");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            try { File.Move(legacy, path); } catch { /* ignore */ }
        }

        if (!File.Exists(path))
        {
            var cfg = new AppConfig();
            Save(cfg, path);
            return cfg;
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            return Normalize(cfg);
        }
        catch
        {
            var cfg = new AppConfig();
            Save(cfg, path);
            return cfg;
        }
    }

    public static void Save(AppConfig cfg, string? path = null)
    {
        path ??= DefaultPath();
        var normalized = Normalize(cfg);
        var json = JsonSerializer.Serialize(normalized, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }

    public static AppConfig Normalize(AppConfig cfg)
    {
        cfg.ToolVersion = 2;
        if (cfg.Hour is < 0 or > 23) cfg.Hour = 23;
        if (cfg.Minute is < 0 or > 59) cfg.Minute = 0;
        if (cfg.Theme is not ("dark" or "light" or "system" or "liquid-glass")) cfg.Theme = "dark";
        if (cfg.MonitorIndex < -1 || cfg.MonitorIndex > 15) cfg.MonitorIndex = -1;
        if (cfg.CompactScale is < 0.8 or > 1.5) cfg.CompactScale = 1.0;
        if (cfg.ExpandedScale is < 0.95 or > 1.25) cfg.ExpandedScale = 1.0;
        if (cfg.OffsetX is < -280 or > 280) cfg.OffsetX = 0;
        if (cfg.OffsetY is < 0 or > 200) cfg.OffsetY = 8;
        if (cfg.Action is not ("shutdown" or "lock" or "display_off" or "pause_media"))
            cfg.Action = "shutdown";
        // 组合模式三个模块全关等于只剩一个空壳，强制留时间
        if (cfg.Composite && !cfg.CompositeClock && !cfg.CompositeHardware && !cfg.CompositeMedia)
            cfg.CompositeClock = true;
        if (cfg.LyricDelayMs is < -3000 or > 3000) cfg.LyricDelayMs = 0;
        if (cfg.Opacity is < 40 or > 100) cfg.Opacity = 100;
        if (cfg.MediaStyle is not ("a" or "b" or "c" or "d")) cfg.MediaStyle = "a";
        if (cfg.UiMaterial is not ("acrylic" or "glass" or "classic")) cfg.UiMaterial = "acrylic";
        cfg.Weekdays = cfg.Weekdays.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(x => x).ToList();
        cfg.Dates = cfg.Dates
            .Where(d => DateTime.TryParse(d, out _))
            .Distinct().OrderBy(x => x).ToList();
        if (cfg.Tasks.Count > 12) cfg.Tasks = cfg.Tasks.Take(12).ToList();
        foreach (var t in cfg.Tasks)
        {
            t.Name = t.Name.Trim();
            if (string.IsNullOrEmpty(t.Name)) t.Name = "事项";
            if (!t.Color.StartsWith("#")) t.Color = "#00A0FF";
        }
        if (cfg.QuickCustoms.Count > 3) cfg.QuickCustoms = cfg.QuickCustoms.Take(3).ToList();
        cfg.QuickCustoms = cfg.QuickCustoms
            .Where(a => !string.IsNullOrWhiteSpace(a.Path))
            .ToList();
        foreach (var a in cfg.QuickCustoms)
        {
            a.Name = a.Name.Trim();
            if (string.IsNullOrEmpty(a.Name)) a.Name = "程序";
            a.Path = a.Path.Trim();
            if (!a.Color.StartsWith("#")) a.Color = "#38bdf8";
        }
        return cfg;
    }
}
