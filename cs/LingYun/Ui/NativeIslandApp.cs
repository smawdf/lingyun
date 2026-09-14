using System.Diagnostics;
using System.IO;
using LingYun.Config;
using LingYun.Platform;
using LingYun.Services;
using SkiaSharp;
using Thread = System.Threading.Thread;

namespace LingYun.Ui;

/// <summary>
/// 原生分层窗岛（NotchPeninsula 路线）：Win32 + UpdateLayeredWindow + Skia。
/// 承载紧凑时钟/媒体、形变展开、alert、计划命中测试。
/// </summary>
public sealed class NativeIslandApp : IDisposable
{
    private const int ShellW = 640;
    private const int ShellH = 400;

    /// <summary>壳尺寸，供诊断离屏渲染对齐坐标。</summary>
    internal const int ShellWidth = ShellW;
    internal const int ShellHeight = ShellH;
    // 基准尺寸（1.0 缩放）。实际尺寸 = 基准 × 配置缩放（CompactScale/ExpandedScale）。
    private const double CompactW0 = 240, CompactH0 = 52, CompactMediaW0 = 300;
    private const double ExpandedW0 = 460, ExpandedH0 = 320;
    private const double AlertW0 = 480, AlertH0 = 210;
    private const double ConfirmW0 = 430, ConfirmH0 = 168;

    // 配置驱动的实际尺寸。属性保证读到的永远是当前配置；三处形态→尺寸 switch 自动跟随。
    private double CompactW => CompactW0 * _cfg.CompactScale;
    private double CompactH => Math.Max(44, CompactH0 * _cfg.CompactScale);   // H 下限保住封面框/旁挂耳
    private double CompactMediaW => CompactMediaW0 * _cfg.CompactScale;
    private double ExpandedW => ExpandedW0 * _cfg.ExpandedScale;
    private double ExpandedH => ExpandedH0 * _cfg.ExpandedScale;
    private double AlertW => AlertW0 * _cfg.ExpandedScale;
    private double AlertH => AlertH0 * _cfg.ExpandedScale;
    private double ConfirmW => ConfirmW0 * _cfg.ExpandedScale;
    private double ConfirmH => ConfirmH0 * _cfg.ExpandedScale;
    /// <summary>基准尺寸（自测与诊断帧按这些值断言；缩放档位另行验证）。</summary>
    internal const double ConfirmWidth = ConfirmW0, ConfirmHeight = ConfirmH0;
    internal const double BaseCompactW = CompactW0, BaseCompactH = CompactH0, BaseCompactMediaW = CompactMediaW0;
    internal const double BaseExpandedW = ExpandedW0, BaseExpandedH = ExpandedH0;
    private const double MaxRadius = 26;
    private const double MorphMs = 360;
    private const int WarnMinutes = 15;
    private const double AutoCollapseMs = 900;   // 展开态下鼠标离开多久后自动回缩
    private const double ToastVisibleMs = 6000;  // 系统通知在胶囊里停留多久
    private const float ToastTitleSize = 13;     // 通知标题字号（基准像素）
    private const float ToastBodySize = 11.5f;   // 通知正文字号（基准像素）
    private const double ToastMinW = 260, ToastMaxW = 620;  // 通知胶囊宽度区间（基准像素）

    // 组合模式：模块顺序固定为 时间 → 硬件 → 媒体，宽度按内容自适应（借鉴 NotchPeninsula 1.5.0 的自动长度）
    private const double CompositeLeftPad = 16, CompositeRightPad = 10, CompositeGap = 16;
    private const double CompositeMinW = 220, CompositeMaxW = 900;
    private const float CompClockSize = 22;      // 组合模式时钟字号（与时钟胶囊一致）
    private const float CompHwTagSize = 10, CompHwPctSize = 10.5f;
    private const float CompHwBarW = 26;         // 硬件模块的进度条宽度
    private const float CompMediaArt = 32;       // 媒体模块封面边长
    private const float CompMediaTextMax = 200;  // 媒体文本（歌词/标题）最大宽度
    private const float CompSpectrumW = 26;      // 媒体模块频谱宽度
    private const double HintMs = 2200;          // 长按提示在展开面板底部停留多久
    private const double ConfirmTimeoutSec = 12; // 退出确认框无人理会多久后自动取消
    private const double AutoHideIdleSec = 10;   // 自动隐藏：鼠标离开多久后收起
    private const int AutoHideHotZonePx = 4;     // 自动隐藏：光标进入工作区顶部多少像素内恢复

    /// <summary>展开面板的页签名。页签与页面绘制、命中测试共用这一份顺序。</summary>
    internal static readonly string[] PageNames = { "计划", "性能", "天气", "日程", "日历", "快捷" };

    /// <summary>「快捷」页的下标——功能按钮只出现在这里，紧凑态一个按钮都不放。</summary>
    internal const int QuickPageIndex = 5;

    /// <summary>「日程」页的下标——可直接在岛上增删改。</summary>
    internal const int TasksPageIndex = 3;

    /// <summary>ISO 星期（1=周一 … 7=周日）对应的中文单字。</summary>
    private static readonly string[] WdNames = { "一", "二", "三", "四", "五", "六", "日" };

    private readonly AppConfig _cfg;

    /// <summary>
    /// 当前配色（跟随配置 theme；theme=system 时跟随系统并在切换后 ~2 秒内生效）。
    /// 缓存是必需的：每帧多处取色，不能每次都读注册表。
    /// </summary>
    private IslandPalette Pal
    {
        get
        {
            if (_palCache is null || (DateTime.UtcNow - _palAt).TotalSeconds >= 2)
            {
                _palAt = DateTime.UtcNow;
                var glassDark = _glassOverride ?? _glassDark;
                _palCache = IslandPalette.For(_cfg.Theme, _cfg.Opacity,
                    IslandPalette.IsLiquidGlass(_cfg.Theme) ? glassDark : null);
            }
            return _palCache.Value;
        }
    }

    private IslandPalette? _palCache;
    private DateTime _palAt;

    // ---- 液态玻璃自适应：背景实测 → 浅色/深色材质 ----
    private readonly BackdropSampler? _backdrop;
    private bool _glassDark;                 // 自适应结果：当前用深色玻璃（白字）
    private bool? _glassOverride;            // 诊断强制指定；null = 交回自适应
    private double _backdropLum = double.NaN;
    private double _glassContrast = double.NaN;
    private DateTime _nextBackdropAt = DateTime.MinValue;
    private const double BackdropIntervalMs = 1000;   // 每秒采一次：一次 5.5ms ≈ 0.5% 单核（实测）

    /// <summary>展开面板各页的专属强调色（深/浅两套，对齐设计提案：计划青/性能绿/天气蓝/日程紫/日历琥珀/快捷红）。</summary>
    internal SKColor PageAccent(int page) => Pal.Dark
        ? page switch
        {
            0 => new SKColor(0x60, 0xcd, 0xff),
            1 => new SKColor(0x4a, 0xde, 0x80),
            2 => new SKColor(0x38, 0xbd, 0xf8),
            3 => new SKColor(0xa7, 0x8b, 0xfa),
            4 => new SKColor(0xfb, 0xbf, 0x24),
            _ => new SKColor(0xff, 0x6b, 0x6b),
        }
        : page switch
        {
            0 => new SKColor(0x0a, 0x7a, 0xf0),
            1 => new SKColor(0x14, 0x8f, 0x45),
            2 => new SKColor(0x02, 0x84, 0xc7),
            3 => new SKColor(0x7c, 0x3a, 0xed),
            4 => new SKColor(0xb4, 0x53, 0x09),
            _ => new SKColor(0xdc, 0x26, 0x26),
        };

    /// <summary>
    /// 媒体页强调色：A 精修样式跟随封面取色（HSV 夹到可读区间，避免荧光色/看不清）；
    /// 取不到封面或 B/C 样式退回调色板强调色。
    /// </summary>
    /// <summary>
    /// HSV 构造（统一入口）：SkiaSharp 的 <c>FromHsv</c> 里 s/v 是 0–100 百分比，
    /// 而 <c>ToHsv</c> 返回 0–1——直接把 ToHsv 的结果喂给 FromHsv 会得到近乎纯黑的颜色
    /// （C 样式的光斑与强调色曾因此整片发黑，等于没有氛围）。这里统一按 0–1 输入。
    /// </summary>
    private static SKColor Hsv01(float hDeg, float s01, float v01)
        => SKColor.FromHsv(hDeg, Math.Clamp(s01, 0f, 1f) * 100f, Math.Clamp(v01, 0f, 1f) * 100f);

    /// <summary>
    /// 媒体页强调色：C 氛围样式跟随封面取色（HSV 夹到可读区间），A 精修 / B 沉浸用调色板固定强调色。
    /// 之前 A 也跟随封面，导致「精修」和「氛围」只剩背景一点差别、看上去几乎一样。
    /// </summary>
    private SKColor MediaAccent
    {
        get
        {
            if (MediaStyleKey != "c") return Pal.Accent;
            EnsureVibColors();
            _vibA.ToHsv(out float h, out float sat, out float val);
            return Pal.Dark
                ? Hsv01(h, Math.Clamp(sat, 0.45f, 0.85f), Math.Clamp(val, 0.62f, 0.80f))
                : Hsv01(h, Math.Clamp(sat, 0.55f, 0.85f), Math.Clamp(val, 0.38f, 0.52f));
        }
    }

    private readonly MediaSessionService _media;
    private readonly LayeredIslandHost _host = new();
    private readonly AudioSpectrumService? _spectrum;
    private readonly AudioVolumeService? _volumeSvc;
    private readonly LyricsService? _lyrics;
    private string _lyricsKey = "";   // 已向 LyricsService 登记的曲目，避免每帧重复登记
    // 渲染侧频谱平滑（attack 快 / release 慢），与 DSP 输出分离
    private readonly float[] _bars = new float[5];
    // 诊断注入的假频谱；null = 用真实服务
    private float[]? _spectrumOverride;
    // 诊断注入的假音量（音量行绘制用；命中测试不理会注入值）
    private (float level, bool muted)? _volumeOverride;
    // 诊断注入的歌词
    private LyricLine[]? _lyricsOverride;
    private float _marqueePhase;      // 跑马灯累计位移（整像素推进）
    private DateTime? _marqueeAt;
    private PerfMetrics? _perf;
    private WeatherInfo? _weather;
    private ToastData? _toast;
    private DateTime _toastAt;
    private float _toastW;   // 当前通知文本最大宽度（基准像素），决定胶囊加宽目标
    private int _page;
    private float _lastScale = 1f;   // 最近一帧的绘制缩放，命中测试必须与之对齐

    private double _islandW, _islandH;   // 构造函数里按配置初始化
    private double _fromW, _fromH, _toW, _toH;
    private double _morphT = 1; // 1 = done
    private string _mode = "compact";
    private string _focus = "timer";
    private bool _wasMediaActive;         // 媒体激活沿检测（自动切焦点用）
    private bool _pendingMediaFocus;      // 非紧凑态时媒体激活了，等回到紧凑态再切
    private volatile bool _paused;
    private volatile bool _showRequested;
    private volatile bool _exitConfirmRequested;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();
    private DateTime? _confirmAt;      // 确认框出现时刻，用于超时自动取消
    private volatile bool _hover;         // 鼠标当前是否在岛上（分层窗只对不透明像素投递鼠标消息）
    private DateTime? _leftAt;            // 鼠标离开岛的时刻，用于自动回缩
    private DateTime? _skip, _demo, _target;
    private long _lastBeep = -1;
    private int _pressIdx = -1;           // 正被按住的危险动作在快捷页网格里的单元下标（仅「快捷」页）
    private int _calOffset;               // 日历页相对本月的偏移（‹ › 翻月）
    private DateTime _pressAt;            // 按下时刻，算长按进度
    private string? _hint;                // 误触提示（单击危险动作时）
    private DateTime _hintAt;
    private int _shellX, _shellY;
    private int _workTop;                 // 岛所在显示器的工作区顶（自动隐藏热区判定用）
    private DateTime _nextAutoHideAt = DateTime.UtcNow;

    // 封面 / 内置图标缓存：避免每帧重复解码
    private byte[]? _coverSrc;
    private SKBitmap? _coverBmp;
    private string _iconKey = "";
    private SKBitmap? _iconBmp;

    // ---- 展开媒体页三样式的派生资源（换曲/换主题/换缩放才重建，逐帧成本≈0）----
    private string _blurKey = "";
    private SKImage? _blurImg;                      // B 沉浸：封面模糊 + 遮罩合成的整底
    private string _vibKey = "";
    private SKColor _vibA = new(0x60, 0xcd, 0xff);  // C 氛围：封面主色
    private SKColor _vibB = new(0xa8, 0x55, 0xf7);  // C 氛围：封面副色

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTime _lastFrame = DateTime.UtcNow;
    private DateTime _nextTick = DateTime.UtcNow;

    public event Action? ExitRequested;

    /// <summary>
    /// 系统通知被点击（岛侧已收起通知）。App 层负责唤醒对应应用并从操作中心划掉——
    /// 岛不直接碰 WinRT/进程，保持可离屏渲染与可测。
    /// </summary>
    public event Action<ToastData>? ToastClicked;

    public NativeIslandApp(AppConfig cfg, MediaSessionService media,
        PerfSampler? perf = null, WeatherService? weather = null, ToastService? toast = null,
        AudioSpectrumService? spectrum = null, AudioVolumeService? volume = null,
        LyricsService? lyrics = null, BackdropSampler? backdrop = null)
    {
        _cfg = cfg;
        _islandW = CompactW;
        _islandH = CompactH;
        _media = media;
        _spectrum = spectrum;
        _volumeSvc = volume;
        _lyrics = lyrics;
        _backdrop = backdrop;
        _media.Updated += _ => { /* 下一帧绘制 */ };
        if (perf is not null) perf.Metrics += m => _perf = m;
        if (weather is not null) weather.Updated += w => _weather = w;
        if (toast is not null)
        {
            // 事件来自轮询线程：改成岛线程内变更（宽度目标要跟着重算）
            toast.Changed += t => Post(() =>
            {
                SetToast(t);
                if (_mode == "compact") Retarget();
            });
        }
        _host.MouseMove += OnMove;
        _host.MouseWheel += OnWheel;
        // 拔屏 / 改分辨率 / 任务栏变化后岛要重新落位，否则会卡在旧坐标甚至跑到屏幕外
        _host.DisplayChanged += Relocate;
        _host.MouseLeave += () =>
        {
            _hover = false;
            CancelPress();
            _leftAt = DateTime.UtcNow;
        };
        _host.MouseDown += OnDown;
        // 危险动作靠"按住"确认，所以必须知道什么时候松手
        _host.MouseUp += OnUp;
        // 窗口被关闭（含 Finish 的 Close）也要把应用带走，否则会留下没有岛的僵尸进程占着单实例锁
        _host.Closed += () =>
        {
            try { ExitRequested?.Invoke(); }
            catch { /* 关窗通道，异常不致命 */ }
        };
    }

    public bool IsPaused => _paused;

    /// <summary>托盘开关：媒体胶囊是否显示真实频谱（写回配置由 App 层负责）。</summary>
    public bool SpectrumEnabled
    {
        get => _cfg.Spectrum;
        set => _cfg.Spectrum = value;
    }

    /// <summary>托盘开关：浅色主题（写回配置由 App 层负责；整岛每帧重绘，改完立刻生效）。</summary>
    public bool LightTheme
    {
        get => string.Equals(_cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);
        set => _cfg.Theme = value ? "light" : "dark";
    }

    /// <summary>托盘开关：系统通知弹窗。关掉时立即撤下正在显示的通知。</summary>
    public bool ToastEnabled
    {
        get => _cfg.Toast;
        set
        {
            _cfg.Toast = value;
            if (!value) Post(() => { SetToast(null); if (_mode == "compact") Retarget(); });
        }
    }

    /// <summary>托盘开关：展开媒体页是否显示在线歌词。关掉时立即清空，不再请求。</summary>
    public bool LyricsEnabled
    {
        get => _cfg.Lyrics;
        set
        {
            _cfg.Lyrics = value;
            if (!value)
            {
                _lyrics?.Clear();
                _lyricsKey = "";
            }
        }
    }

    /// <summary>托盘「暂停计划 / 恢复计划」。</summary>
    public bool TogglePause()
    {
        _paused = !_paused;
        return _paused;
    }

    /// <summary>托盘「显示灵动岛」：收起并重新置顶。</summary>
    public void RequestShow() => _showRequested = true;

    /// <summary>
    /// 把一次配置改动排进岛线程执行（编辑器窗口在 WPF 线程，直接改 Tasks 会和每帧绘制竞争）。
    /// </summary>
    public void Post(Action mutation) => _pending.Enqueue(mutation);

    /// <summary>
    /// 应用一次事项改动并写盘（排进岛线程执行）。original=null 且 replacement 非空 → 追加；
    /// replacement=null → 删除 original（按引用找，编辑器打开期间列表变动也不会错位）；
    /// 两者都有 → 按引用替换。
    /// </summary>
    public void ApplyTask(Config.TaskItem? original, Config.TaskItem? replacement)
    {
        Action mutate = () =>
        {
            if (original is null)
            {
                if (replacement is not null) _cfg.Tasks.Add(replacement);
            }
            else if (replacement is null)
            {
                _cfg.Tasks.Remove(original);
            }
            else
            {
                int i = _cfg.Tasks.IndexOf(original);
                if (i >= 0) _cfg.Tasks[i] = replacement;
            }
            try { ConfigStore.Save(_cfg); } catch { /* 写盘失败不致命 */ }
        };
        Post(mutate);
    }

    /// <summary>点日程行请求编辑（index = -1 表示新建）。岛线程抛出，App 在 WPF 线程开编辑窗。</summary>
    public event Action<int>? TaskEditRequested;

    /// <summary>点计划页时间卡：请求打开时间编辑窗（岛线程抛出，App 在 WPF 线程开窗）。</summary>
    public event Action? PlanTimeEditRequested;

    /// <summary>点快捷页「＋ 添加程序」：请求打开自定义程序编辑窗（WPF）。</summary>
    public event Action? QuickCustomEditRequested;

    /// <summary>托盘「岛设置」请求（岛线程抛出，App 在 WPF 线程弹设置窗）。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 岛内直接改配置并写盘（计划开关 / 动作 / 星期 / 日程勾选 / 日历点选等）。
    /// 排进岛线程执行；写盘失败不致命（本次会话内仍然生效）。
    /// </summary>
    public void MutateConfig(Action<AppConfig> mutate) => Post(() =>
    {
        mutate(_cfg);
        ConfigStore.Normalize(_cfg);
        try { ConfigStore.Save(_cfg); } catch { /* 磁盘异常不致命 */ }
    });

    /// <summary>
    /// 几何（缩放/偏移）变更入口：排进岛线程执行——尺寸目标重算 + 壳窗重定位，
    /// 与 16ms 渲染循环零竞争。配置写盘由调用方负责。
    /// </summary>
    public void ApplyGeometry()
        => Post(() =>
        {
            Retarget();
            ComputeShellPosition();
            _host.Move(_shellX, _shellY);
        });

    /// <summary>托盘菜单触发「岛设置」。</summary>
    public void RequestSettings() => SettingsRequested?.Invoke();

    /// <summary>
    /// 壳窗当前屏幕矩形（物理像素）：设置/任务/时刻/快捷程序等编辑窗打开时用来避开岛体落位。
    /// </summary>
    public (int X, int Y, int W, int H) ShellRect => (_shellX, _shellY, ShellW, ShellH);

    /// <summary>
    /// 编辑窗打开期间让岛退出置顶（关闭时传 false 恢复）。
    /// 岛每次 Move 都会重申 TOPMOST，不退出就会压住同样置顶的编辑窗（黑底对黑底，关闭按钮被盖住）。
    /// </summary>
    public void SetTopmostYield(bool yield) => Post(() => _host.SetTopmost(!yield));

    /// <summary>
    /// 设置窗口改了配置后调用：重解析尺寸并立即重绘（岛线程执行）。
    /// 配色/透明度靠 <see cref="Pal"/> 的缓存失效自动跟上（≤2 秒），这里只管几何。
    /// </summary>
    public void ApplyConfig() => Post(() =>
    {
        _palCache = null;          // 主题/透明度改动立刻生效，不等缓存过期
        Retarget();
        _host.Move(_shellX, _shellY);
        UpdateBackdrop(DateTime.UtcNow, force: true);   // 主题/不透明度变了，重新判断该用浅还是深材质
    });

    /// <summary>
    /// 托盘「退出」：在岛内弹确认（岛是置顶窗，永远可见）。
    /// 不再用 MessageBox——从托盘菜单的嵌套消息循环里弹模态框，本应用又没有活动窗口，
    /// 对话框会弹不出来/不被激活，表现就是「点了退出没反应」。
    /// </summary>
    public void RequestExitConfirm() => _exitConfirmRequested = true;

    public void SetDemo(DateTime when) => _demo = when;

    public void Start()
    {
        // Win32 窗口创建与消息/绘制必须同一线程
        var t = new Thread(RunUiThread) { IsBackground = true, Name = "lingyun-island" };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
    }

    private void RunUiThread()
    {
        try
        {
            ComputeShellPosition();
            _host.Create(ShellW, ShellH, _shellX, _shellY);
            Loop();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "灵云-crash.log"),
                    DateTime.Now + "\n" + ex);
            }
            catch { /* ignore */ }
            ExitRequested?.Invoke();
        }
    }

    /// <summary>
    /// 按目标显示器的工作区算出外壳位置（顶部居中，距顶 8px）。
    /// 用 Displays 而不是 SPI_GETWORKAREA —— 后者只会给主显示器。
    /// </summary>
    private void ComputeShellPosition()
    {
        var work = Displays.WorkAreaOf(_cfg.MonitorIndex);
        _workTop = work.Top;
        (_shellX, _shellY) = ShellPositionFor(work.Left, work.Right, work.Top, _cfg.OffsetX, _cfg.OffsetY);
    }

    /// <summary>壳窗口落位（纯函数，自测钉钳制）：水平 = 工作区中心 + 用户偏移，
    /// 垂直 = 工作区顶 + 用户偏移；都钳在工作区内，偏移再大也不会把岛推出屏幕。</summary>
    internal static (int X, int Y) ShellPositionFor(int workLeft, int workRight, int workTop,
        int offsetX, int offsetY)
    {
        int x = workLeft + (workRight - workLeft - ShellWidth) / 2 + offsetX;
        int y = workTop + offsetY;
        int minX = workLeft, maxX = workRight - ShellWidth;
        x = Math.Clamp(x, Math.Min(minX, maxX), Math.Max(minX, maxX));
        y = Math.Clamp(y, workTop, workTop + 400);
        return (x, y);
    }

    /// <summary>显示器配置变化后重新落位（换屏、改分辨率、任务栏变化）。</summary>
    private void Relocate()
    {
        ComputeShellPosition();
        // 索引可能已越界（屏被拔掉），回写真实生效的值
        var list = Displays.All();
        int want = _cfg.MonitorIndex;
        if ((uint)want >= (uint)list.Count)
        {
            want = list.FindIndex(m => m.Primary);
            _cfg.MonitorIndex = want;
            SaveMonitorChoice();
        }
        _host.Move(_shellX, _shellY);
        UpdateBackdrop(DateTime.UtcNow, force: true);   // 换屏/改分辨率后背后内容整个换了
    }

    /// <summary>
    /// 切换岛到下一块显示器（托盘用）。返回切换后的显示器序号（1 起，给人看）。
    /// </summary>
    public int CycleMonitor()
    {
        var list = Displays.All();
        if (list.Count <= 1) return 1;
        int cur = _cfg.MonitorIndex;
        if ((uint)cur >= (uint)list.Count) cur = list.FindIndex(m => m.Primary);
        _cfg.MonitorIndex = (cur + 1) % list.Count;
        SaveMonitorChoice();
        Relocate();
        return _cfg.MonitorIndex + 1;
    }

    /// <summary>当前显示器数量（托盘菜单文案用）。</summary>
    public int MonitorCount
    {
        get
        {
            try { return Displays.All().Count; }
            catch { return 1; }
        }
    }

    private void SaveMonitorChoice()
    {
        try { ConfigStore.Save(_cfg); }
        catch { /* 配置写不进去也不该影响显示 */ }
    }

    private void Loop()
    {
        // 退出由 WM_CLOSE 置位：窗口在岛线程内自行销毁，避免跨线程 DestroyWindow
        while (!_host.IsClosed)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Min(0.05, (now - _lastFrame).TotalSeconds);
            _lastFrame = now;
            _host.PumpOnce();
            while (_pending.TryDequeue(out var mutation))
            {
                try { mutation(); }
                catch { /* 单条改动失败不影响岛 */ }
            }

            if (_showRequested)
            {
                _showRequested = false;
                if (_mode != "compact") SetMode("compact");
                _host.Move(_shellX, _shellY);
            }

            if (_exitConfirmRequested)
            {
                _exitConfirmRequested = false;
                _confirmAt = DateTime.UtcNow;
                SetMode("confirm");
            }
            // 确认框超时自动取消，避免一直挂着（用户可能只是误点）
            else if (_mode == "confirm" && _confirmAt is not null
                     && (now - _confirmAt.Value).TotalSeconds > ConfirmTimeoutSec)
            {
                SetMode("compact");
            }

            // 自动回缩：展开态下鼠标离开一小段时间就收回紧凑态。
            // alert 不参与——那是必须由用户处理的状态，不能自己消失。
            if (_mode == "expanded" && !_hover && _leftAt is not null
                && (now - _leftAt.Value).TotalMilliseconds > AutoCollapseMs)
            {
                TraceClick("auto-collapse (mouse left expanded panel)");
                SetMode("compact");
            }

            if (now >= _nextAutoHideAt)
            {
                _nextAutoHideAt = now.AddMilliseconds(500);
                UpdateAutoHide(now);
            }

            if (now >= _nextTick)
            {
                _nextTick = now.AddMilliseconds(250);
                Tick();
            }


            if (_morphT < 1)
            {
                _morphT = Math.Min(1, _morphT + dt * 1000 / MorphMs);
                double e = OutExpo(_morphT);
                _islandW = _fromW + (_toW - _fromW) * e;
                _islandH = _fromH + (_toH - _fromH) * e;
            }

            RenderFrame();
            Thread.Sleep(16);
        }
    }

    private static double OutExpo(double t) => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);

    /// <summary>
    /// 闲置自动隐藏（默认关闭）：无媒体、无弹层、鼠标不在岛上、离开超过 10 秒 → 收起；
    /// 媒体开始播放、通知到达、托盘「显示灵动岛」或光标压到屏幕顶部热区 → 恢复。
    /// 隐藏期间窗口收不到鼠标消息，所以恢复靠这里的轮询。
    /// </summary>
    private void UpdateAutoHide(DateTime nowUtc)
    {
        if (!_cfg.AutoHide)
        {
            if (!_host.Visible) _host.SetVisible(true);
            return;
        }
        bool busy = _toast is not null || _mode != "compact";
        if (_host.Visible)
        {
            double idle = _leftAt is { } t ? (nowUtc - t).TotalSeconds : 0;
            if (ShouldAutoHide(true, _mode == "compact", MediaActive, busy, _hover, idle, AutoHideIdleSec))
            {
                _hover = false;
                _host.SetVisible(false);
            }
            return;
        }
        bool wake = MediaActive || _showRequested;
        if (!wake)
        {
            try
            {
                Native.GetCursorPos(out var p);
                wake = CursorInTopZone(p.y, _workTop, AutoHideHotZonePx);
            }
            catch { /* 拿不到光标位置就继续等下一次 */ }
        }
        if (wake)
        {
            _leftAt = nowUtc;   // 重新计时，避免刚显示又立刻收起
            _host.SetVisible(true);
            UpdateBackdrop(nowUtc, force: true);   // 收起期间桌面可能已经换过，显示前重采一次
        }
    }

    /// <summary>自动隐藏判定（纯逻辑，自测用）：开关开、紧凑态、无媒体、无弹层、未悬停、闲置超时。</summary>
    internal static bool ShouldAutoHide(bool enabled, bool compact, bool mediaActive, bool busy,
        bool hovered, double idleSec, double delaySec)
        => enabled && compact && !mediaActive && !busy && !hovered && idleSec > delaySec;

    /// <summary>光标是否落在工作区顶部的恢复热区里（纯函数，自测用）。</summary>
    internal static bool CursorInTopZone(int cursorY, int workTop, int zonePx)
        => cursorY <= workTop + zonePx;

    private void OnMove(int x, int y)
    {
        _hover = true;
        _leftAt = null;
        // 展开「快捷」页长按危险动作时，指针滑出原按钮 → 立刻取消，避免"按住后划走"仍触发
        if (_pressIdx >= 0 && _page == QuickPageIndex
            && !ActionHit(x, y, _pressIdx, out _, out _))
            CancelPress();
    }

    /// <summary>滚轮切页。上滚=上一页、下滚=下一页。</summary>
    private void OnWheel(int x, int y, int delta)
    {
        // 紧凑媒体态：滚轮直接调音量（展开态滚轮留给翻页，避免抢手势）
        if (_mode == "compact")
        {
            if (_focus == "media" && MediaActive && _volumeSvc is { } vol && vol.Available)
            {
                var cur = vol.GetVolume();
                if (cur is not null) vol.SetVolume(AudioVolumeService.Step(cur.Value, delta > 0 ? 1 : -1));
            }
            return;
        }
        if (_mode != "expanded") return;
        _hover = true;
        _leftAt = null;
        int dir = delta > 0 ? -1 : 1;
        _page = (_page + dir + PageNames.Length) % PageNames.Length;
    }

    private void CancelPress()
    {
        _pressIdx = -1;
    }

    /// <summary>胶囊下方那行提示（误触说明 / 已取消）。</summary>
    private void ShowHint(string text)
    {
        _hint = text;
        _hintAt = DateTime.UtcNow;
    }

    private bool HintActive =>
        _hint is not null && (DateTime.UtcNow - _hintAt).TotalMilliseconds < HintMs;

    /// <summary>松手。只有「危险动作 + 按够时长 + 指针仍在原按钮上」三者同时成立才执行；
    /// 其余一律当误触，什么都不做，只给一行提示告诉用户怎么才能执行。</summary>
    private void OnUp(int x, int y)
    {
        int idx = _pressIdx;
        if (idx < 0) return;
        double held = (DateTime.UtcNow - _pressAt).TotalMilliseconds;
        bool onCell = ActionHit(x, y, idx, out _, out _);
        CancelPress();   // 先清状态再执行：动作里可能重入 UI
        var (act, _, _) = QuickCell(idx);
        if (act is null || !QuickActions.ShouldRunOnHold(act, held, onCell))
        {
            ShowHint(act is null ? "已取消"
                : !onCell ? $"「{act.Label}」已取消"
                : $"「{act.Label}」要按住 {QuickActions.HoldMs / 1000.0:0.#} 秒才执行");
            return;
        }
        act.Run();
    }

    /// <summary>临时埋点：点击与自动回缩写日志，定位「点了没反应」。</summary>
    private static void TraceClick(string step)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "灵云-click-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {step}" + Environment.NewLine);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 展开态点击归属（纯函数，自测用）：媒体页可见时，那六个页面的处理器一律让位。
    /// 这条是硬性契约——它们原先无条件 return，把媒体页的播放/暂停/下一首/进度条点击
    /// 整个吃掉（默认停在「计划」页时必然中招）。
    /// </summary>
    internal static bool PageHandlerOwnsClick(bool mediaView, int page, int ownerPage)
        => !mediaView && page == ownerPage;

    private void OnDown(int x, int y)
    {
        TraceClick($"down mode={_mode} focus={_focus} page={_page} at ({x},{y}) scale={_lastScale:0.##}");
        var (ix, iy, iw, ih) = Island();
        _hover = true;
        _leftAt = null;
        if (_mode == "compact")
        {
            // 通知期间点任意处 = 关掉它 + 唤醒对应应用（1.5.0 的「消息激活」）
            if (ToastActive)
            {
                var clicked = _toast;
                SetToast(null);
                Retarget();
                if (clicked is not null) ToastClicked?.Invoke(clicked);
                return;
            }
            // 旁挂焦点：媒体与计划同时存在时，可在紧凑态切换"显示时间 / 显示媒体"
            if (MediaActive && TimerActive)
            {
                double ex = ix + iw - 78, ey = iy + 8;
                if (x >= ex && x <= ex + 36 && y >= ey && y <= ey + 36)
                {
                    _focus = _focus == "media" ? "timer" : "media";
                    Retarget();
                    return;
                }
            }
            if (x >= ix && x <= ix + iw && y >= iy && y <= iy + ih)
            {
                // 点击胶囊一律展开——"跳源窗口"挪到展开面板里（点标题/封面），
                // 原先紧凑态点击就跳走，面板根本打不开（用户反馈）
                SetMode("expanded");
                return;
            }
        }
        else if (_mode == "expanded")
        {
            // 媒体焦点下这个面板显示的是媒体页（没有页签、也没有那六个页面）。
            // 六个页面的点击处理必须让位——它们原先无条件 return，会把媒体页上的
            // 播放/暂停/下一首/进度条点击整个吃掉（默认停在「计划」页时必然中招）。
            bool mediaView = _focus == "media" && MediaActive;
            // 页签（媒体焦点下不显示页签，走媒体控制）
            if (!mediaView)
            {
                var (tabTop, tabBottom) = TabBand(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
                if (y >= tabTop && y <= tabBottom)
                {
                    var tabs = LayoutTabs(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
                    TraceClick($"tab band hit, tabs=[{string.Join(",", tabs.Select(t => $"{t.x:0}..{t.x + t.w:0}"))}]");
                    for (int i = 0; i < tabs.Length; i++)
                    {
                        var (tx, tw) = tabs[i];
                        if (x >= tx && x <= tx + tw)
                        {
                            _page = i;
                            TraceClick($"tab hit -> page {i} ({PageNames[i]})");
                            return;
                        }
                    }
                    TraceClick("tab band but no tab hit");
                }
            }
            // ✕
            if (x >= ix + iw - 40 && x <= ix + iw - 8 && y >= iy + 6 && y <= iy + 34)
            {
                SetMode("compact");
                return;
            }
            // 「计划」页：开关 / 时间卡 / 动作分段 / 同时暂停 / 星期方块（绘制与命中共用 PlanLayout）
            if (PageHandlerOwnsClick(mediaView, _page, 0))
            {
                var body = PageBody(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
                var pl = PlanLayout(body, _lastScale);
                if (pl.Toggle.Contains((float)x, (float)y))
                {
                    MutateConfig(c => c.Enabled = !c.Enabled);
                    return;
                }
                if (pl.TimeCard.Contains((float)x, (float)y))
                {
                    PlanTimeEditRequested?.Invoke();
                    return;
                }
                for (int a = 0; a < pl.Actions.Length; a++)
                {
                    if (!pl.Actions[a].Contains((float)x, (float)y)) continue;
                    string key = PlanActionKeys[a];
                    MutateConfig(c => c.Action = key);
                    return;
                }
                if (pl.AlsoPause.Contains((float)x, (float)y))
                {
                    MutateConfig(c => c.AlsoPauseMedia = !c.AlsoPauseMedia);
                    return;
                }
                for (int d = 0; d < pl.Weekdays.Length; d++)
                {
                    if (!pl.Weekdays[d].Contains((float)x, (float)y)) continue;
                    int day = d + 1;
                    MutateConfig(c => { if (!c.Weekdays.Remove(day)) c.Weekdays.Add(day); });
                    return;
                }
                SetMode("compact");
                return;
            }
            // 「日历」页：‹ › 翻月；点日期加入 / 移出计划（写 dates）
            if (PageHandlerOwnsClick(mediaView, _page, 4))
            {
                var body = PageBody(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
                var cl = CalLayout(body, _lastScale);
                if (cl.Prev.Contains((float)x, (float)y)) { _calOffset--; return; }
                if (cl.Next.Contains((float)x, (float)y)) { _calOffset++; return; }
                var first = CalFirst;
                int lead = ((int)first.DayOfWeek + 6) % 7;
                int days = DateTime.DaysInMonth(first.Year, first.Month);
                for (int d = 1; d <= days; d++)
                {
                    int ci = lead + d - 1;
                    float cx = body.Left + ci % 7 * cl.CellW + cl.CellW / 2f;
                    float cy = cl.GridTop + ci / 7 * cl.RowH;
                    if (Math.Abs(x - cx) <= cl.CellW / 2f && Math.Abs(y - cy) <= cl.RowH / 2f)
                    {
                        string key = new DateTime(first.Year, first.Month, d).ToString("yyyy-MM-dd");
                        MutateConfig(c => { if (!c.Dates.Remove(key)) c.Dates.Add(key); });
                        return;
                    }
                }
                SetMode("compact");
                return;
            }
            // 「日程」页：点勾选圈完成 / 取消、点行编辑、点 ✕ 删除、点底部「＋」新建（共用 TaskLayout）
            if (PageHandlerOwnsClick(mediaView, _page, TasksPageIndex))
            {
                var body = PageBody(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
                var (cells, dels, add, _) = TaskLayout(body, _lastScale, _cfg.Tasks.Count);
                for (int i = 0; i < cells.Length; i++)
                {
                    if (!cells[i].Contains((float)x, (float)y)) continue;
                    var done = new SKRect(cells[i].Left + 6 * _lastScale, cells[i].MidY - 11 * _lastScale,
                        cells[i].Left + 28 * _lastScale, cells[i].MidY + 11 * _lastScale);
                    if (done.Contains((float)x, (float)y))
                    {
                        int ti = i;
                        MutateConfig(c => { if (ti < c.Tasks.Count) c.Tasks[ti].Done = !c.Tasks[ti].Done; });
                        return;
                    }
                    if (dels[i].Contains((float)x, (float)y))
                    {
                        ApplyTask(_cfg.Tasks[i], null);   // 按引用删除，写盘由 mutate 内完成
                        return;
                    }
                    TaskEditRequested?.Invoke(i);
                    return;
                }
                if (add.Contains((float)x, (float)y))
                {
                    if (_cfg.Tasks.Count < 12) TaskEditRequested?.Invoke(-1);
                    return;
                }
                SetMode("compact");
                return;
            }
            // 「快捷」页：安全/自定义卡单击执行；危险动作长按 0.9 秒确认。点空白收起。
            if (PageHandlerOwnsClick(mediaView, _page, QuickPageIndex))
            {
                var g = QuickGridGeom();
                for (int i = 0; i < g.Cells.Length; i++)
                {
                    if (!g.Cells[i].Contains((float)x, (float)y)) continue;
                    var (act, custom, isSlot) = QuickCell(i);
                    if (isSlot)
                    {
                        QuickCustomEditRequested?.Invoke();
                        return;
                    }
                    if (custom is not null)
                    {
                        // 自定义程序：单击启动（路径失败给一行提示，不打断岛）
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(custom.Path)
                            {
                                UseShellExecute = true,
                            });
                            ShowHint($"已启动「{custom.Name}」");
                        }
                        catch
                        {
                            ShowHint($"「{custom.Name}」启动失败：检查路径");
                        }
                        return;
                    }
                    if (act is null) return;
                    if (!QuickActions.ShouldRunOnClick(act))
                    {
                        _pressIdx = i;
                        _pressAt = DateTime.UtcNow;
                        _hint = null;
                        return;
                    }
                    act.Run();
                    return;
                }
                SetMode("compact");
                return;
            }
            if (mediaView)
            {
                // 三样式的命中全部走 ChromeFor（与绘制同一套矩形，杜绝坐标漂移）
                var islandRect = new SKRect(ix, iy, ix + iw, iy + ih);
                float sc = (float)_lastScale;
                var ch = ChromeFor(islandRect, sc);
                // 「⌂ 面板」：切回计时器焦点 → 展开态立即显示多页面板（页签可达）
                if (ch.Home.Contains((float)x, (float)y))
                {
                    _focus = "timer";
                    Retarget();
                    return;
                }
                // 进度条：点按定位（与绘制同一条轨道）
                if (_media.State.DurationMs > 0 && ch.Seek.Contains((float)x, (float)y))
                {
                    double p = Math.Clamp((x - ch.Seek.Left) / ch.Seek.Width, 0, 1);
                    _ = _media.SeekAsync((long)(p * _media.State.DurationMs));
                    return;
                }
                // 来源切换 chip：多个会话时点一下切下一个
                if (_media.Sessions.Count > 1 && ch.SrcChip.Contains((float)x, (float)y))
                {
                    _media.CycleSession(1);
                    return;
                }
                // 音量：点喇叭切静音；点轨道直接定位
                if (_volumeSvc is { } vol && vol.Available)
                {
                    if (ch.VolGlyph.Contains((float)x, (float)y))
                    {
                        vol.ToggleMute();
                        return;
                    }
                    var vTrack = ch.VolTrack;
                    if (y >= vTrack.Top - 8 && y <= vTrack.Bottom + 8 && x >= vTrack.Left - 6 && x <= vTrack.Right + 6)
                    {
                        double v = Math.Clamp((x - vTrack.Left) / vTrack.Width, 0, 1);
                        vol.SetVolume((float)v);
                        return;
                    }
                }
                // 封面/标题：跳源窗口——走与通知点击同一条三级激活链
                // （WinRT 包激活 → COM → 进程前台化 → 窗口标题模糊匹配），
                // 抖音这类点分 AUMID 桌面应用在旧的单级 FocusApp 下永远匹配不到窗口
                if (ch.Cover.Contains((float)x, (float)y) || ch.TitleBox.Contains((float)x, (float)y))
                {
                    string wakeId = _media.State.AppId;
                    _ = Services.AppActivatorService.ActivateAsync(wakeId).ContinueWith(t =>
                    {
                        var (ok, how) = t.Status == TaskStatus.RanToCompletion ? t.Result : (false, "激活任务异常");
                        TraceClick($"media wake appId={wakeId} ok={ok} how={how}");
                    }, TaskScheduler.Default);
                    return;
                }
                // 传输键（上一首 / 播放暂停 / 下一首）
                if (ch.Prev.Contains((float)x, (float)y)) _ = _media.PrevAsync();
                else if (ch.Play.Contains((float)x, (float)y)) _ = _media.PlayPauseAsync();
                else if (ch.Next.Contains((float)x, (float)y)) _ = _media.NextAsync();
                SetMode("compact");
                return;
            }
            // 性能/天气等展示页没有可操作控件：点击空白同样收起展开面板。
            SetMode("compact");
            return;
        }
        else if (_mode == "confirm")
        {
            var (cancel, quit) = ConfirmButtons(new SKRect(ix, iy, ix + iw, iy + ih), (float)_lastScale);
            if (cancel.Contains((float)x, (float)y))
            {
                SetMode("compact");
                return;
            }
            if (quit.Contains((float)x, (float)y))
            {
                // 与「关闭岛窗口」同一条退出链（App 侧 QuitApp 已实测可干净退出）
                ExitRequested?.Invoke();
                return;
            }
        }
        else if (_mode == "alert")
        {
            var (cancelBtn, execBtn) = AlertButtons(new SKRect(ix, iy, ix + iw, iy + ih), (float)_lastScale);
            if (cancelBtn.Contains((float)x, (float)y))
            {
                if (_target is not null) _skip = _target;
                SetMode("compact");
            }
            else if (execBtn.Contains((float)x, (float)y))
            {
                // 「立即执行」：同危险动作语义，直接走电源动作（提醒里已是二次确认场景）
                PowerActions.Execute(_cfg.Action);
                SetMode("compact");
            }
        }
    }

    /// <summary>
    /// 曲目变化时向歌词服务登记一次（250ms 节拍调用）。放在 Tick 最前：
    /// 计划关闭/暂停时也要照常拉歌词——那时媒体照样在放。
    /// </summary>
    private void SyncLyrics()
    {
        if (_lyrics is null) return;
        var st = _media.State;
        if (!_cfg.Lyrics)
        {
            // 开关关掉：清一次状态即可，不必每拍空转
            if (_lyricsKey.Length > 0)
            {
                _lyricsKey = "";
                _lyrics.Clear();
            }
            return;
        }
        string key = st.Active ? LyricsService.TrackKey(st.Title, st.Artist) : "";
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        if (st.Active) _lyrics.EnsureFor(st.Title, st.Artist, (int)(st.DurationMs / 1000));
        else _lyrics.Clear();
    }

    /// <summary>
    /// 展开媒体页的歌词带：当前句（卡拉OK点亮）+ 下一句（灰）。
    /// 位置/字号/对齐来自 <see cref="MediaChrome"/>（随样式变化：B 沉浸左对齐，A/C 居中）。
    /// 没歌词/歌词未就绪时整带留空，不占位、不报错。
    /// </summary>
    private void DrawLyricsBand(SKCanvas canvas, MediaChrome ch, float s)
    {
        LyricLine[] lines;
        if (!_cfg.Lyrics) lines = Array.Empty<LyricLine>();   // 开关优先，注入也不画
        else if (_lyricsOverride is not null) lines = _lyricsOverride;
        else if (_lyrics is not null) lines = _lyrics.Lines;
        else return;
        if (lines.Length == 0) return;

        long posMs = _media.State.PositionMs;
        int idx = LyricIndexAt(lines, posMs);
        if (idx < 0) return;
        string cur = lines[idx].Text;
        if (_cfg.LyricsKaraoke)
        {
            long nextMs = idx + 1 < lines.Length
                ? (long)lines[idx + 1].Time.TotalMilliseconds
                : (long)lines[idx].Time.TotalMilliseconds + LyricLastLineMs;
            DrawKaraokeLine(canvas, cur, ch, ch.LyrCurY, ch.LyrCurSize,
                LyricProgress(posMs, (long)lines[idx].Time.TotalMilliseconds, nextMs, _cfg.LyricDelayMs));
        }
        else
        {
            DrawFittedLine(canvas, cur, ch, ch.LyrCurY, ch.LyrCurSize, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
        if (idx + 1 < lines.Length)
            DrawFittedLine(canvas, lines[idx + 1].Text, ch, ch.LyrNextY, ch.LyrNextSize, Pal.Dim);
    }

    /// <summary>最后一句没有下一句，按 4 秒估长度（卡拉OK进度用）。</summary>
    private const long LyricLastLineMs = 4000;

    /// <summary>取当前句下标；延迟补偿为正表示歌词提前（显示跟上人声）。</summary>
    private int LyricIndexAt(LyricLine[] lines, long posMs)
        => LyricsService.LineIndexAt(lines,
            TimeSpan.FromMilliseconds(Math.Max(0, posMs - _cfg.LyricDelayMs)));

    /// <summary>
    /// 卡拉OK进度：当前行已唱比例 0..1（自测断言用纯函数）。
    /// 下一句时间缺失/异常（span ≤ 0）返回 0，不猜。
    /// </summary>
    internal static float LyricProgress(long posMs, long lineMs, long nextMs, int delayMs)
    {
        long span = nextMs - lineMs;
        if (span <= 0) return 0f;
        return Math.Clamp((posMs - delayMs - lineMs) / (float)span, 0f, 1f);
    }

    /// <summary>卡拉OK行：先整行暗色，再按进度裁剪用强调色重画已唱部分（B 左对齐 / A·C 居中）。</summary>
    private void DrawKaraokeLine(SKCanvas canvas, string text, MediaChrome ch, float y, float size, float progress)
    {
        float maxW = ch.LyrBox.Width;
        float tw = MeasureText(text, size, SKFontStyleWeight.SemiBold);
        if (tw > maxW)
        {
            // 超宽走省略号路径：没法既裁剪又省略，宁可整行一个颜色
            DrawFittedLine(canvas, text, ch, y, size, Pal.Fg, SKFontStyleWeight.SemiBold);
            return;
        }
        float x = ch.LyrLeft ? ch.LyrBox.Left : ch.LyrBox.MidX - tw / 2;
        DrawText(canvas, text, x, y, size, Pal.Dim, SKFontStyleWeight.SemiBold);
        if (progress <= 0.001f) return;
        canvas.Save();
        canvas.ClipRect(new SKRect(x, y - size, x + tw * progress, y + size * 0.35f));
        DrawText(canvas, text, x, y, size, MediaAccent, SKFontStyleWeight.SemiBold);
        canvas.Restore();
    }

    /// <summary>歌词行绘制：B 沉浸左对齐 / A·C 居中，超宽尾部省略。</summary>
    private void DrawFittedLine(SKCanvas canvas, string text, MediaChrome ch, float y, float size,
        SKColor color, SKFontStyleWeight weight = SKFontStyleWeight.Medium)
    {
        float maxW = ch.LyrBox.Width;
        string shown = Ellipsize(text, size, maxW, weight);
        float x = ch.LyrLeft ? ch.LyrBox.Left : ch.LyrBox.MidX - MeasureText(shown, size, weight) / 2;
        DrawText(canvas, shown, x, y, size, color, weight);
    }

    /// <summary>SMTC 播放状态 → 中文（副标题用；未知状态原样返回）。</summary>
    private static string StatusText(string status) => status switch
    {
        "Playing" => "播放中",
        "Paused" => "已暂停",
        "Stopped" => "已停止",
        "Closed" => "已关闭",
        "Changing" => "切换中",
        "Opened" => "就绪",
        _ => status,
    };

    private bool MediaActive => _media.State.Active;
    private bool TimerActive => Scheduler.NextTarget(_cfg, DateTime.Now) is not null;

    /// <summary>系统通知正在胶囊里展示：开关打开、紧凑态、未过期。通知优先于媒体与时钟。</summary>
    private bool ToastActive =>
        _toast is not null
        && ShouldShowToast(_cfg.Toast, _mode == "compact",
            (DateTime.Now - _toastAt).TotalMilliseconds, ToastVisibleMs);

    /// <summary>自测用：通知是否应当占据胶囊（纯逻辑，时间是「已显示毫秒」）。</summary>
    internal static bool ShouldShowToast(bool enabled, bool compact, double shownMs, double visibleMs)
        => enabled && compact && shownMs < visibleMs;

    /// <summary>通知文本最大宽度（基准像素）：标题加粗，正文取更宽者。</summary>
    private static float ToastTextWidth(ToastData t)
    {
        float w = MeasureText(t.Title, ToastTitleSize, SKFontStyleWeight.SemiBold);
        if (t.Body.Length > 0) w = Math.Max(w, MeasureText(t.Body, ToastBodySize));
        return w;
    }

    /// <summary>
    /// 通知胶囊宽度（基准像素 × 缩放）：文本 + 左侧图标与内边距，clamp 260–620——
    /// 长通知自动加宽、短通知不过窄。纯函数，自测断言边界与单调。
    /// </summary>
    internal static double ToastWidth(float textW, double scale)
        => Math.Clamp(textW + 46 + 16, ToastMinW, ToastMaxW) * scale;

    /// <summary>岛线程内换通知：记时间、算宽度、必要时重定尺寸。</summary>
    private void SetToast(ToastData? t)
    {
        _toast = t;
        _toastAt = DateTime.Now;
        _toastW = t is null ? 0 : ToastTextWidth(t);
    }

    private (int x, int y, int w, int h) Island()
    {
        int w = (int)Math.Round(_islandW), h = (int)Math.Round(_islandH);
        int x = (ShellW - w) / 2;
        return (x, 0, w, h);
    }

    /// <summary>
    /// 按当前形态与焦点重算尺寸目标（焦点在紧凑态变了、媒体开始/停止时都必须调用，
    /// 否则媒体胶囊会被画在 240px 的时钟壳里）。
    /// </summary>
    private void Retarget()
    {
        (_toW, _toH) = _mode switch
        {
            "expanded" => (ExpandedW, ExpandedH),
            "alert" => (AlertW, AlertH),
            "confirm" => (ConfirmW, ConfirmH),
            _ => CompactTarget(),
        };
        if (Math.Abs(_toW - _fromW) < 0.5 && Math.Abs(_toH - _fromH) < 0.5 && _morphT >= 1)
            return;   // 目标没变，不重启形变
        _fromW = _islandW;
        _fromH = _islandH;
        _morphT = 0;
    }

    /// <summary>紧凑态目标尺寸。优先级：系统通知（自适应宽度）&gt; 组合模式（自动长度）&gt; 媒体焦点 &gt; 时钟。</summary>
    private (double W, double H) CompactTarget()
    {
        if (ToastActive) return (ToastWidth(_toastW, _cfg.CompactScale), CompactH);
        if (CompositeActive) return (CompositeTargetW(), CompactH);
        if (_focus == "media" && MediaActive) return (CompactMediaW, CompactH);
        return (CompactW, CompactH);
    }

    /// <summary>组合模式是否生效（紧凑态 + 配置开关；系统通知仍最优先）。</summary>
    private bool CompositeActive => _cfg.Composite && _mode == "compact";

    /// <summary>
    /// 组合模式布局：从左到右按「时间 → 硬件 → 媒体」排列（绘制/断言共用同一份几何）。
    /// 返回总宽与各模块槽位（基准像素，未乘缩放）。
    /// </summary>
    internal static (double Total, (double X, double W)[] Slots) CompositeLayout(
        bool clock, bool hw, bool media, float clockW, float hwW, float mediaW)
    {
        var slots = new List<(double X, double W)>();
        double x = CompositeLeftPad;
        void Add(double w)
        {
            slots.Add((x, w));
            x += w + CompositeGap;
        }
        if (clock) Add(clockW);
        if (hw) Add(hwW);
        if (media) Add(mediaW);
        if (slots.Count == 0) return (0, Array.Empty<(double X, double W)>());
        return (x - CompositeGap + CompositeRightPad, slots.ToArray());
    }

    /// <summary>组合模式目标宽度（基准像素 × 缩放）：clamp 220–900；三模块全关时退回时钟胶囊宽。</summary>
    internal static double CompositeWidth(bool clock, bool hw, bool media,
        float clockW, float hwW, float mediaW, double scale)
    {
        var (total, _) = CompositeLayout(clock, hw, media, clockW, hwW, mediaW);
        if (total <= 0) return CompactW0 * scale;
        return Math.Clamp(total, CompositeMinW, CompositeMaxW) * scale;
    }

    /// <summary>把基准槽位换算成画布坐标的模块矩形。</summary>
    internal static SKRect SlotRect(SKRect r, (double X, double W) slot, float s)
        => new(r.Left + (float)(slot.X * s), r.Top,
            r.Left + (float)((slot.X + slot.W) * s), r.Bottom);

    /// <summary>组合模式时钟槽宽（基准像素）：按三种可能文案取最宽者，倒计时逐秒变化时宽度不抖。</summary>
    internal static float CompactClockSlotW()
        => Math.Max(MeasureText("88:88", CompClockSize, SKFontStyleWeight.SemiBold),
            Math.Max(MeasureText("已暂停", CompClockSize, SKFontStyleWeight.SemiBold),
                MeasureText("8时88分", CompClockSize, SKFontStyleWeight.SemiBold)));

    /// <summary>组合模式硬件槽宽（基准像素）：标签 + 进度条 + 按「100%」定宽的百分比槽。</summary>
    internal static float CompositeHwW()
    {
        float tag = Math.Max(MeasureText("CPU", CompHwTagSize), MeasureText("内存", CompHwTagSize));
        return tag + 6 + CompHwBarW + 6 + MeasureText("100%", CompHwPctSize);
    }

    /// <summary>紧凑态时钟文案：暂停 → 已暂停；有计划目标 → 倒计时；否则 HH:mm。</summary>
    private string ClockLabel()
    {
        if (_paused) return "已暂停";
        if (_target is { } t)
        {
            double leftSec = (t - DateTime.Now).TotalSeconds;
            if (leftSec > 0)
                return leftSec >= 3600
                    ? $"{(int)leftSec / 3600}时{(int)leftSec % 3600 / 60:00}分"
                    : $"{(int)leftSec / 60:00}:{(int)leftSec % 60:00}";
        }
        return DateTime.Now.ToString("HH:mm");
    }

    /// <summary>组合模式媒体文本：歌词优先，其次标题（与上游一致）。</summary>
    private string CompositeMediaText()
    {
        if (_cfg.Lyrics)
        {
            var lines = _lyricsOverride ?? _lyrics?.Lines;
            if (lines is { Length: > 0 })
            {
                int idx = LyricsService.LineIndexAt(lines,
                    TimeSpan.FromMilliseconds(_media.State.PositionMs));
                if (idx >= 0 && lines[idx].Text.Length > 0) return lines[idx].Text;
            }
        }
        var t = _media.State.Title;
        return t.Length > 0 ? t : "未知曲目";
    }

    /// <summary>组合模式媒体槽宽（基准像素）：封面 + 文本（限宽）+ 频谱。</summary>
    private float CompositeMediaW()
        => CompMediaArt + 8
           + Math.Min(MeasureText(CompositeMediaText(), 14, SKFontStyleWeight.SemiBold), CompMediaTextMax)
           + 8 + CompSpectrumW;

    /// <summary>组合模式目标宽度（含 CompactScale 缩放）。</summary>
    private double CompositeTargetW()
        => CompositeWidth(_cfg.CompositeClock, _cfg.CompositeHardware,
            _cfg.CompositeMedia && MediaActive,
            CompactClockSlotW(), CompositeHwW(), CompositeMediaW(), _cfg.CompactScale);

    /// <summary>
    /// 媒体焦点维护（每拍调用）。规则：
    /// 无媒体 → 时钟焦点；无计划且有媒体 → 紧凑态持续保证媒体焦点（SMTC 会话可能短暂消失
    /// 再恢复，纯边沿触发会丢焦点——用户实测踩过）；有计划 → 只在媒体出现沿自动切一次，
    /// 之后尊重旁挂圆点的手动切换；展开/提醒/确认态不打断，挂 pending 回紧凑态再切。
    /// </summary>
    internal void UpdateMediaFocus()
    {
        bool active = _media.State.Active;
        bool edge = active != _wasMediaActive;
        _wasMediaActive = active;

        if (!active)
        {
            _pendingMediaFocus = false;
            if (_focus == "media")
            {
                _focus = "timer";
                if (_mode == "compact") Retarget();
            }
            return;
        }

        if (_mode != "compact")
        {
            if (edge) _pendingMediaFocus = true;
            return;
        }
        if (!TimerActive || edge || _pendingMediaFocus)
        {
            _pendingMediaFocus = false;
            if (_focus != "media")
            {
                _focus = "media";
                Retarget();
            }
        }
    }

    internal string Focus => _focus;

    private void SetMode(string mode)
    {
        if (_mode == mode)
        {
            // 同形态重入（如 RequestShow）：若挂着待切的媒体焦点，就地应用并重定尺寸
            if (mode == "compact" && _pendingMediaFocus)
            {
                _pendingMediaFocus = false;
                _focus = "media";
                Retarget();
            }
            return;
        }
        if (mode == "compact" && _pendingMediaFocus)
        {
            _pendingMediaFocus = false;
            _focus = "media";   // 下面的尺寸表会取到 CompactMediaW
        }
        _mode = mode;
        _fromW = _islandW;
        _fromH = _islandH;
        (_toW, _toH) = mode switch
        {
            "expanded" => (ExpandedW, ExpandedH),
            "alert" => (AlertW, AlertH),
            "confirm" => (ConfirmW, ConfirmH),
            _ => CompactTarget(),
        };
        _morphT = 0;
        UpdateBackdrop(DateTime.UtcNow, force: true);   // 岛矩形变了，按新范围重新采一次背景
    }

    private void Tick()
    {
        SyncLyrics();
        UpdateMediaFocus();
        UpdateBackdrop(DateTime.UtcNow);
        // 通知过期（或开关被关掉）：撤下并按新形态重定尺寸。
        // 必须放在 _paused / 无计划的提前 return 之前——通知不归计划管
        if (_toast is not null && !ToastActive)
        {
            SetToast(null);
            if (_mode == "compact") Retarget();
        }
        // 组合模式宽度随内容（曲目/歌词/媒体起停）变化：每拍核对，变了才重启形变
        if (CompositeActive && !ToastActive) Retarget();
        if (_paused)
        {
            _target = null;
            if (_mode == "alert") SetMode("compact");
            return;
        }
        var now = DateTime.Now;
        var target = _demo ?? Scheduler.NextTarget(_cfg, now);
        // 「取消本次」保留到该目标时间过去之后才解除，否则下一帧就会被当作新目标重新武装
        if (_skip is not null && now > _skip.Value) _skip = null;
        if (target is null)
        {
            _target = null;
            if (_mode == "alert") SetMode("compact");
            return;
        }
        _target = target;
        double left = (target.Value - now).TotalSeconds;
        bool alertOn = now >= target.Value.AddMinutes(-WarnMinutes) && _skip is null;
        if (_mode == "alert")
        {
            if (left <= 0 && _skip is null) Finish();
            else if (!alertOn) SetMode("compact");
        }
        else if (alertOn)
        {
            SetMode("alert");
        }
        if (_mode == "alert" && left <= 10)
        {
            long sec = (long)left;
            if (sec != _lastBeep)
            {
                _lastBeep = sec;
                try { Console.Beep(880, 70); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 液态玻璃自适应：按节拍抓一次岛背后的桌面亮度，决定用浅色玻璃（深字）还是深色玻璃（白字）。
    ///
    /// 只有 theme=liquid-glass、开着自适应、岛可见时才采样。实测一次约 5.5ms（整块 BitBlt 进
    /// 复用 DIB + 直接读内存），1 秒一次 ≈ 0.5% 单核；静止画面不会重复着色，只在结论变化时换材质。
    /// 必须在岛线程调用：BackdropSampler 的 DIB 不是线程安全的，也不该和绘制抢同一块内存。
    /// </summary>
    private void UpdateBackdrop(DateTime nowUtc, bool force = false)
    {
        if (_backdrop is null || !IslandPalette.IsLiquidGlass(_cfg.Theme)) return;
        if (_glassOverride is not null) return;                 // 诊断强制态：不采样
        if (!_cfg.GlassAdaptive)
        {
            if (_glassDark) { _glassDark = false; _palCache = null; }
            return;
        }
        if (!force && nowUtc < _nextBackdropAt) return;
        _nextBackdropAt = nowUtc.AddMilliseconds(BackdropIntervalMs);
        if (!_host.Visible) return;

        var (ix, iy, iw, ih) = Island();
        // 采的是"岛周围那一圈桌面"，并把岛自己（含阴影外扩 22px）从统计里剔除——
        // 实测 DWM 下 BitBlt 会把本进程的分层窗一起采进来，直接采岛矩形等于照镜子。
        int bandH = Math.Min(ShellH, ih + 90);
        var sample = _backdrop.Sample(
            _shellX, _shellY, ShellW, bandH,
            new Services.SampleRect(_shellX + ix - 22, _shellY + iy - 22, iw + 44, ih + 44));
        if (sample is not { } s || !s.Known) return;            // 抓不到就保持当前材质，不自作主张
        _backdropLum = s.Luminance;
        // 最亮分区代表"最不利的区域"：平均亮度会被大片暗色稀释，只看平均会漏掉半明半暗的壁纸
        bool dark = IslandPalette.PreferDarkGlass(
            new SKColor(s.R, s.G, s.B), _cfg.Opacity, _glassDark, out _glassContrast);
        if (s.BrightestCell > 0.82 && _cfg.Opacity < 70) dark = false;   // 有很亮的区域且玻璃薄：浅材质更稳
        if (dark != _glassDark)
        {
            _glassDark = dark;
            _palCache = null;                                    // 立刻换色，不等缓存过期
        }
    }

    /// <summary>诊断用：最近一次背景采样与自适应结论。</summary>
    internal (double Luminance, double Contrast, bool Dark, bool Known) BackdropState
        => (_backdropLum, _glassContrast, _glassDark, !double.IsNaN(_backdropLum));

    /// <summary>离屏渲染用：强制液态玻璃的深浅材质（null = 交回自适应）。</summary>
    internal void ForceGlassDark(bool? dark)
    {
        _glassOverride = dark;
        _palCache = null;
    }

    /// <summary>
    /// 离屏渲染用：注入一次背景采样结果（不真抓屏），驱动自适应决策。
    /// </summary>
    internal void InjectBackdrop(byte r, byte g, byte b)
    {
        _backdropLum = IslandPalette.RelativeLuminance(new SKColor(r, g, b));
        _glassDark = IslandPalette.PreferDarkGlass(new SKColor(r, g, b), _cfg.Opacity, _glassDark, out _glassContrast);
        _palCache = null;
    }

    private void Finish()
    {
        if (_demo is null)
        {
            if (_cfg.AlsoPauseMedia) PauseMediaSafe();
            var act = PowerActions.Get(_cfg.Action);
            // pause_media 走 SMTC 真暂停；PowerActions.PauseMedia 的按键是 toggle，会把已暂停的媒体放起来
            if (act.Key == "pause_media") PauseMediaSafe();
            else act.Run();
        }
        ExitRequested?.Invoke();
        _host.Close();
    }

    private void PauseMediaSafe()
    {
        if (MediaActive) _ = _media.PauseAsync();
        else PowerActions.PauseMedia();
    }

    private void RenderFrame() => _host.Render(PaintScene);

    private static SKColor Fade(SKColor color, float factor)
        => color.WithAlpha((byte)Math.Clamp(Math.Round(color.Alpha * Math.Clamp(factor, 0f, 1f)), 0, 255));

    private float OpacityFactor
        => Math.Clamp(_cfg.Opacity, 40, 100) / 100f;

    private SKColor BackgroundAlpha(SKColor color, byte alpha)
        => color.WithAlpha((byte)Math.Clamp(Math.Round(alpha * OpacityFactor), 0, 255));

    /// <summary>统一绘制卡片/面板表面：填充、细边框、顶部内高光。</summary>
    private void DrawMaterialSurface(SKCanvas canvas, SKRect r, float radius, SKColor fill)
    {
        using (var bg = new SKPaint { Color = fill, IsAntialias = true })
            canvas.DrawRoundRect(r, radius, radius, bg);
        using (var bd = new SKPaint
        {
            Color = Pal.Border,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        })
            canvas.DrawRoundRect(r, radius, radius, bd);
        using (var hi = new SKPaint { Color = Pal.Highlight, IsAntialias = true })
            canvas.DrawRoundRect(
                new SKRect(r.Left + radius * 0.4f, r.Top + 1, r.Right - radius * 0.4f, r.Top + 2.2f),
                1.1f,
                1.1f,
                hi);
    }

    private void DrawMaterialCard(SKCanvas canvas, SKRect r, float radius)
        => DrawMaterialSurface(canvas, r, radius, Pal.Card);

    /// <summary>
    /// 把当前状态画到任意 canvas 上。与窗口无关，因此可被离屏渲染（诊断出图 / 自测）复用。
    /// </summary>
    internal void PaintScene(SKCanvas canvas, SKImageInfo info)
    {
        var (x, y, w, h) = Island();
        // 窗口始终整壳，岛画在中心
        float s = info.Width / (float)ShellW;
        _lastScale = s;
        var rect = new SKRect(x * s, y * s, (x + w) * s, (y + h) * s);
        float radius = (float)Math.Min(_islandH / 2, MaxRadius) * s;

        // 岛投影使用调色板 alpha，透明度滑杆也会同步压低阴影。
        using (var shadow = new SKPaint
        {
            Color = Pal.Shadow,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26 * s),
        })
            canvas.DrawRoundRect(rect.Left, rect.Top + 14 * s, rect.Width, rect.Height, radius, radius, shadow);

        DrawMaterialSurface(canvas, rect, radius, Pal.Body);

        if (_mode == "compact")
            DrawCompact(canvas, rect, s);
        else if (_mode == "expanded")
            DrawExpanded(canvas, rect, s);
        else if (_mode == "alert")
            DrawAlert(canvas, rect, s);
        else if (_mode == "confirm")
            DrawConfirm(canvas, rect, s);
    }


    // ---- 供诊断使用的状态读写（不改变正常运行行为）----

    /// <summary>离屏渲染用：当前形态。</summary>
    internal string Mode => _mode;

    /// <summary>离屏渲染用：切到指定形态（不带动画）。</summary>
    internal void ForceMode(string mode)
    {
        _mode = mode;
        (_islandW, _islandH) = mode switch
        {
            "expanded" => (ExpandedW, ExpandedH),
            "alert" => (AlertW, AlertH),
            "confirm" => (ConfirmW, ConfirmH),
            // 与真实路径同一套宽度规则（通知自适应 &gt; 媒体加宽 &gt; 时钟），出图不会和真机差一截
            _ => CompactTarget(),
        };
        _morphT = 1;
    }

    /// <summary>离屏渲染用：直接给一个目标时间，让 alert 态有内容。</summary>
    internal void SetTarget(DateTime target) => _target = target;

    /// <summary>离屏渲染用：强制媒体焦点。</summary>
    internal void ForceFocus(string focus) => _focus = focus;

    /// <summary>离屏渲染用：切到指定页（0=计划 1=性能 2=天气 3=事项 4=月份 5=快捷）。</summary>
    internal void ForcePage(int page) => _page = Math.Clamp(page, 0, PageNames.Length - 1);

    /// <summary>离屏渲染用：摆出「正在长按第 actionIndex 个内置动作、已按 heldMs 毫秒」的样子。
    /// actionIndex 按 QuickActions.All 下标（诊断沿用），内部换算成快捷页网格单元。</summary>
    internal void ForceQuickPress(int actionIndex, double heldMs = 0)
    {
        int n = QuickActions.All.Count;
        actionIndex = ((actionIndex % n) + n) % n;
        var g = QuickGridGeom();
        _pressIdx = actionIndex < QuickActions.FirstDestructiveIndex
            ? actionIndex
            : g.DangerStart + (actionIndex - QuickActions.FirstDestructiveIndex);
        _pressAt = DateTime.UtcNow.AddMilliseconds(-heldMs);
    }

    /// <summary>离屏渲染用：注入性能/天气数据，便于诊断出图。</summary>
    internal void InjectData(PerfMetrics? perf = null, WeatherInfo? weather = null)
    {
        if (perf is not null) _perf = perf;
        if (weather is not null) _weather = weather;
    }

    /// <summary>
    /// 离屏渲染用：注入假频谱（0..1 × 5）。传 null 恢复读真实服务。
    /// </summary>
    internal void InjectSpectrum(float[]? bands) => _spectrumOverride = bands;

    /// <summary>离屏渲染用：注入假音量行（level=null 恢复读真实设备）。</summary>
    internal void InjectVolume(float? level, bool muted = false)
        => _volumeOverride = level is null ? null : (level.Value, muted);

    /// <summary>离屏渲染用：注入歌词（null 恢复读真实服务）。当前位置仍取自注入的媒体状态。</summary>
    internal void InjectLyrics(LyricLine[]? lines) => _lyricsOverride = lines;

    /// <summary>离屏渲染用：摆出退出确认框（含倒计时标签）。</summary>
    internal void ForceExitConfirm()
    {
        _confirmAt = DateTime.UtcNow;
        ForceMode("confirm");
    }

    /// <summary>离屏渲染用：注入一条系统通知（宽度按文本自适应）。</summary>
    internal void InjectToast(ToastData? toast) => SetToast(toast);

    private static void DrawText(SKCanvas c, string text, float x, float y, float size, SKColor color, SKFontStyleWeight weight = SKFontStyleWeight.Medium)
        // 逐段选字体：Segoe UI 既无 CJK 也无 ⏻/⏮ 这类符号字形，必须按字符回退，否则是豆腐块
        => AppFonts.Draw(c, text, x, y, size, color, weight);

    private static float MeasureText(string text, float size, SKFontStyleWeight weight = SKFontStyleWeight.Medium)
        => AppFonts.Measure(text, size, weight);

    /// <summary>封面绘制：系统真封面 → 内置应用图标 → 品牌色块（紧凑态：8px 圆角、无投影）。</summary>
    private void DrawArt(SKCanvas canvas, SKRect art, float scale)
        => DrawArtStyled(canvas, art, 8 * scale, scale);

    /// <summary>
    /// 封面绘制（展开三样式共用）：半径/倾斜/投影可调。
    /// 位图解析与紧凑态同源：B站/PotPlayer 等会话通常不带真封面，内置站标优先。
    /// </summary>
    private void DrawArtStyled(SKCanvas canvas, SKRect art, float radius, float scale, float tiltDeg = 0f, bool shadow = false)
    {
        string appId = _media.State.AppId;
        var art2 = Services.AppIcons.PreferBundled(appId)
            ? IconBitmap(appId) ?? CoverBitmap()
            : CoverBitmap() ?? IconBitmap(appId);
        canvas.Save();
        if (shadow)
        {
            canvas.Save();
            if (tiltDeg != 0f) canvas.RotateDegrees(tiltDeg, art.MidX, art.MidY);
            using var sh = new SKPaint
            {
                Color = Fade(Pal.Shadow, Pal.Dark ? 0.8f : 0.45f),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12 * scale),
            };
            canvas.DrawRoundRect(art, radius, radius, sh);
            canvas.Restore();
        }
        if (tiltDeg != 0f) canvas.RotateDegrees(tiltDeg, art.MidX, art.MidY);
        var clip = new SKPath();
        clip.AddRoundRect(art, radius, radius);
        canvas.ClipPath(clip);
        if (art2 is null)
        {
            Services.AppIcons.DrawFallback(canvas, art, appId);
        }
        else
        {
            using var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(art2, art, cp);
        }
        canvas.Restore();
    }

    /// <summary>按引用缓存解码结果：SMTC 每 500ms 换一次 byte[]，可把解码从每帧降到每 500ms。</summary>
    private SKBitmap? CoverBitmap()
    {
        var bytes = _media.State.Thumb;
        if (bytes is not { Length: > 64 })
        {
            ClearCoverCache();
            return null;
        }
        if (!ReferenceEquals(bytes, _coverSrc))
        {
            ClearCoverCache();
            try { _coverBmp = SKBitmap.Decode(bytes); } catch { _coverBmp = null; }
            _coverSrc = bytes;
        }
        return _coverBmp;
    }

    private void ClearCoverCache()
    {
        _coverBmp?.Dispose();
        _coverBmp = null;
        _coverSrc = null;
    }

    private SKBitmap? IconBitmap(string appId)
    {
        if (!string.Equals(appId, _iconKey, StringComparison.Ordinal))
        {
            _iconBmp?.Dispose();
            _iconBmp = null;
            _iconKey = appId;
            if (!string.IsNullOrEmpty(appId))
            {
                try { _iconBmp = Services.AppIcons.TryLoad(appId, 64); } catch { _iconBmp = null; }
            }
        }
        return _iconBmp;
    }

    private void DrawCompact(SKCanvas canvas, SKRect r, float s)
    {
        // 系统通知优先占住胶囊：点一下即可唤醒来源应用，不打断时钟太久
        if (ToastActive)
        {
            DrawToast(canvas, r, s);
            return;
        }

        // 组合模式：时间/硬件/媒体同屏，无旁挂切换（模块全显，不需要切焦点）
        if (CompositeActive)
        {
            DrawCompactComposite(canvas, r, s);
            return;
        }

        bool media = _focus == "media" && MediaActive;
        // 紧凑态只显示内容（时间 / 媒体 / 通知），不放任何功能按钮，内容区不用给球让位
        float contentRight = r.Right - 16 * s;
        if (media)
        {
            // HTML 实测：封面 36×36 @(14,8)；标题 12.5px / 来源 10px，两行均起 x60；频谱贴右 13
            var art = new SKRect(r.Left + 14 * s, r.Top + 8 * s, r.Left + 50 * s, r.Top + 44 * s);
            DrawArt(canvas, art, s);
            DrawMediaTitle(canvas, r, s);
            DrawText(canvas, Ellipsize(MediaSubLine(), 10 * s, r.Right - 60 * s - 46 * s),
                r.Left + 60 * s, r.Top + 38 * s, 10 * s, Pal.Sub);
            DrawSpectrumAt(canvas, r.Right - 13 * s, r.Top + 26 * s, s);
        }
        else
        {
            // 首页：时间 + 右侧双行日期；倒计时 / 已暂停时 ClockLabel 是状态文本 → 居中显示
            string label = ClockLabel();
            float textSize = 25 * s;
            bool counting = _paused || (_target is { } t && (t - DateTime.Now).TotalSeconds > 0);
            if (counting)
            {
                // 倒计时 / 已暂停：状态文本居中（HTML 里是时间+日期并排，倒计时单独居中）
                float lw = MeasureText(label, textSize, SKFontStyleWeight.SemiBold);
                float start = r.Left + Math.Max(0, (r.Width - lw) / 2f);
                DrawText(canvas, label, start, r.Top + 36 * s, textSize, Pal.Fg, SKFontStyleWeight.SemiBold);
            }
            else
            {
                // HTML 实测：时间 25px @x65 基线 36；日期两行 10px @x141 基线 23.5 / 37
                DrawText(canvas, label, r.Left + 65 * s, r.Top + 36 * s, textSize, Pal.Fg, SKFontStyleWeight.SemiBold);
                var now = DateTime.Now;
                string d1 = $"{now.Month}月{now.Day}日";
                string d2 = $"周{WdNames[((int)now.DayOfWeek + 6) % 7]}";
                DrawText(canvas, d1, r.Left + 141 * s, r.Top + 23.5f * s, 10 * s, Pal.Sub);
                DrawText(canvas, d2, r.Left + 141 * s, r.Top + 37 * s, 10 * s, Pal.Dim);
            }
        }

        // 旁挂：媒体与计划同时存在时，可在紧凑态切换"显示时间 / 显示媒体"
        if (MediaActive && TimerActive)
        {
            using var ear = new SKPaint { Color = Pal.Track, IsAntialias = true };
            var er = new SKRect(r.Right - 78 * s, r.Top + 8 * s, r.Right - 42 * s, r.Top + 44 * s);
            canvas.DrawCircle(er.MidX, er.MidY, 18 * s, ear);
            DrawText(canvas, _focus == "media" ? "⏱" : "♪", er.MidX - 8 * s, er.MidY + 5 * s, 13 * s, Pal.Fg);
        }
    }

    /// <summary>
    /// 组合模式：时间 / 硬件 / 媒体三个模块同屏，宽度已由 Retarget 按内容定好。
    /// 点击胶囊仍然展开面板（保留我们的多页设计；上游是内联传输键 + 不展开，这里不照搬）。
    /// </summary>
    private void DrawCompactComposite(SKCanvas canvas, SKRect r, float s)
    {
        // 模块与内容一起随「胶囊大小」缩放：否则 1.4x 时岛变宽、内容还按原尺寸，右侧留一大片空白
        float cs = CompactContentScale(s);
        bool media = _cfg.CompositeMedia && MediaActive;
        var (total, slots) = CompositeLayout(_cfg.CompositeClock, _cfg.CompositeHardware,
            media, CompactClockSlotW(), CompositeHwW(), CompositeMediaW());
        if (slots.Length == 0 || total <= 0)
        {
            // 兜底：三个模块全关（Normalize 会拦住，这里防御性退回时钟）
            DrawCompositeClock(canvas, r, cs);
            return;
        }
        int i = 0;
        if (_cfg.CompositeClock) DrawCompositeClock(canvas, SlotRect(r, slots[i++], cs), cs);
        if (_cfg.CompositeHardware) DrawCompositeHardware(canvas, SlotRect(r, slots[i++], cs), cs);
        if (media) DrawCompositeMedia(canvas, SlotRect(r, slots[i], cs), cs);
        // 模块间细分隔线（对齐设计提案：分隔线代替纯间距）
        for (int g = 0; g < slots.Length - 1; g++)
        {
            float gx = r.Left + (float)(slots[g].X + slots[g].W + CompositeGap / 2f) * cs;
            using var sep = new SKPaint { Color = Pal.Track, IsAntialias = true };
            canvas.DrawRect(new SKRect(gx, r.MidY - 11 * cs, gx + cs, r.MidY + 11 * cs), sep);
        }
    }

    /// <summary>紧凑态内容缩放 = 画布缩放（DPI）× 配置的胶囊缩放。</summary>
    private float CompactContentScale(float s) => s * (float)_cfg.CompactScale;

    /// <summary>组合模式·时间模块：定宽槽内居中（倒计时变化时位置不跳）。</summary>
    private void DrawCompositeClock(SKCanvas canvas, SKRect slot, float s)
    {
        string label = ClockLabel();
        float size = CompClockSize * s;
        float w = MeasureText(label, size, SKFontStyleWeight.SemiBold);
        DrawText(canvas, label, slot.MidX - w / 2, slot.MidY + 7 * s, size,
            Pal.Fg, SKFontStyleWeight.SemiBold);
    }

    /// <summary>组合模式·硬件模块：CPU / 内存两行（标签 + 迷你条 + 定宽百分比）。</summary>
    private void DrawCompositeHardware(SKCanvas canvas, SKRect slot, float s)
    {
        double cpu = _perf?.Cpu ?? 0;
        double mem = _perf?.MemPct ?? 0;
        DrawHwRow(canvas, slot, s, slot.MidY - 9 * s, "CPU", cpu, Pal.Accent);
        DrawHwRow(canvas, slot, s, slot.MidY + 10 * s, "内存", mem, Pal.Sub);
    }

    private void DrawHwRow(SKCanvas canvas, SKRect slot, float s, float cy, string tag,
        double pct, SKColor barColor)
    {
        float tagSize = CompHwTagSize * s;
        DrawText(canvas, tag, slot.Left, cy + 3.5f * s, tagSize, Pal.Sub);
        float tagW = Math.Max(MeasureText("CPU", tagSize), MeasureText("内存", tagSize));
        float barX = slot.Left + tagW + 6 * s;
        var track = new SKRect(barX, cy - 2 * s, barX + CompHwBarW * s, cy + 2 * s);
        using (var tp = new SKPaint { Color = Pal.Track, IsAntialias = true })
            canvas.DrawRoundRect(track, 2 * s, 2 * s, tp);
        double v = Math.Clamp(pct, 0, 100);
        var fill = new SKRect(track.Left, track.Top,
            track.Left + (float)(track.Width * v / 100), track.Bottom);
        using (var fp = new SKPaint { Color = barColor, IsAntialias = true })
            canvas.DrawRoundRect(fill, 2 * s, 2 * s, fp);
        // 百分比画在「100%」定宽槽里居中：数字从 9% 跳到 10% 时后面的内容不跟着晃
        string pctText = $"{(int)Math.Round(v)}%";
        float pctSize = CompHwPctSize * s;
        float slotW = MeasureText("100%", pctSize);
        float pw = MeasureText(pctText, pctSize);
        DrawText(canvas, pctText, track.Right + 6 * s + (slotW - pw) / 2, cy + 3.5f * s,
            pctSize, Pal.Fg);
    }

    /// <summary>组合模式·媒体模块：封面 + 歌词/标题（省略号）+ 频谱。</summary>
    private void DrawCompositeMedia(SKCanvas canvas, SKRect slot, float s)
    {
        var art = new SKRect(slot.Left, slot.MidY - CompMediaArt / 2 * s,
            slot.Left + CompMediaArt * s, slot.MidY + CompMediaArt / 2 * s);
        DrawArt(canvas, art, s);
        float textLeft = art.Right + 8 * s;
        float textRight = slot.Right - CompSpectrumW * s - 8 * s;
        float bandW = textRight - textLeft;
        if (bandW > 12 * s)
        {
            float size = 14 * s;
            DrawText(canvas, Ellipsize(CompositeMediaText(), size, bandW, SKFontStyleWeight.SemiBold),
                textLeft, slot.MidY + 5 * s, size, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
        DrawSpectrumAt(canvas, slot.Right - 2 * s, slot.MidY, s);
    }

    /// <summary>媒体胶囊第二行：播放状态 · 来源（HTML 原型的 t2 行）。</summary>
    private string MediaSubLine()
        => $"{StatusText(_media.State.Status)} · {AppIcons.FriendlyName(_media.State.AppId)}";

    /// <summary>媒体胶囊的标题带：封面之后、频谱之前（绘制与几何断言共用）。</summary>
    internal static SKRect MediaTitleBand(SKRect r, float s)
        => new(r.Left + 60 * s, r.Top + 10 * s, r.Right - 46 * s, r.Top + 30 * s);

    /// <summary>自测用：文本测量包装（省略断言需要同一把尺子）。</summary>
    internal static float MeasureForTest(string text, float size,
        SKFontStyleWeight weight = SKFontStyleWeight.Medium) => MeasureText(text, size, weight);

    /// <summary>超宽文本省略号截断（宽度达标才收尾）。</summary>
    internal static string Ellipsize(string text, float size, float maxW,
        SKFontStyleWeight weight = SKFontStyleWeight.Medium)
    {
        if (MeasureText(text, size, weight) <= maxW) return text;
        while (text.Length > 1 && MeasureText(text + "…", size, weight) > maxW)
            text = text[..^1];
        return text + "…";
    }

    /// <summary>
    /// 媒体胶囊标题：放得下就居中；放不下且在播放 → 跑马灯（右缘进入、左缘滚出、无缝循环）；
    /// 暂停 → 静止显示开头并省略。
    /// </summary>
    private void DrawMediaTitle(SKCanvas canvas, SKRect r, float s)
    {
        string title = _media.State.Title;
        if (string.IsNullOrWhiteSpace(title)) return;
        var band = MediaTitleBand(r, s);
        float size = 12.5f * s;
        float ty = r.Top + 23 * s;
        float tw = MeasureText(title, size, SKFontStyleWeight.SemiBold);

        if (tw <= band.Width)
        {
            // 左对齐（与 HTML 原型的 t1 行一致；居中会让短标题看起来"飘"在胶囊中间）
            DrawText(canvas, title, band.Left, ty, size, Pal.Fg, SKFontStyleWeight.SemiBold);
            return;
        }
        if (!_media.State.IsPlaying)
        {
            DrawText(canvas, Ellipsize(title, size, band.Width, SKFontStyleWeight.SemiBold),
                band.Left, ty, size, Pal.Fg, SKFontStyleWeight.SemiBold);
            return;
        }
        // 跑马灯：36px/s，整像素推进。浮点相位会让两份文本的亚像素舍入不一致
        // （AppFonts 分段绘制时字形原点取整），字形间来回蹭——看起来在抖。
        const float speed = 36f;
        var now = DateTime.UtcNow;
        float dt = _marqueeAt is { } t ? (float)Math.Min(0.1, (now - t).TotalSeconds) : 0f;
        _marqueeAt = now;
        int gap = (int)Math.Round(56 * s);
        int spacing = (int)tw + gap;      // 两份文本的固定整数间距
        _marqueePhase += dt * speed * s;
        if (_marqueePhase >= spacing) _marqueePhase %= spacing;
        int drawX = (int)band.Right - (int)_marqueePhase;
        canvas.Save();
        canvas.ClipRect(band);
        DrawText(canvas, title, drawX, ty, size, Pal.Fg, SKFontStyleWeight.SemiBold);
        DrawText(canvas, title, drawX + spacing, ty, size, Pal.Fg, SKFontStyleWeight.SemiBold);
        canvas.Restore();
    }

    /// <summary>
    /// 媒体胶囊右侧的 5 段频谱。真实频谱可用时用 WASAPI 采样（attack 0.75 / release 0.12 平滑）；
    /// 服务不可用或配置关闭时回退假动画，功能不劣化。暂停时柱子回落到底噪高度。
    /// </summary>
    private void DrawSpectrum(SKCanvas canvas, SKRect r, float s)
        => DrawSpectrumAt(canvas, r.Right, r.MidY, s);

    /// <summary>频谱按「右缘 + 中线」绘制，媒体胶囊与组合模式的媒体模块共用。</summary>
    private void DrawSpectrumAt(SKCanvas canvas, float right, float midY, float s)
    {
        using var bar = new SKPaint { Color = Pal.Accent, IsAntialias = true };
        // 关闭开关时即使有注入数据也不画真频谱（诊断靠这条出「关掉后」的对照帧）
        bool real = _cfg.Spectrum && (_spectrumOverride is not null || _spectrum is { Available: true });

        if (real)
        {
            float[] src = _spectrumOverride ?? _spectrum!.Bands;
            for (int i = 0; i < 5; i++)
            {
                float target = _media.State.IsPlaying ? src[i] : 0f;
                // 快起慢落：上升跟手，下落有余晖，视觉更接近硬件频谱表
                _bars[i] += (target - _bars[i]) * (target > _bars[i] ? 0.75f : 0.12f);
            }
            for (int i = 0; i < 5; i++)
            {
                float lv = Math.Max(0.06f, _bars[i]); // 底噪高度，全 0 时仍可见五根柱
                float bh = 13 * s * lv;
                float bx = right - 34 * s + i * 4.6f * s;
                canvas.DrawRoundRect(new SKRect(bx, midY - bh / 2, bx + 2.4f * s, midY + bh / 2), 1.2f * s, 1.2f * s, bar);
            }
        }
        else
        {
            // 回退：原假动画（正弦摆动），WASAPI 不可用时保底
            float t = (float)_clock.Elapsed.TotalMilliseconds / 200f;
            for (int i = 0; i < 4; i++)
            {
                float lv = _media.State.IsPlaying ? 0.3f + 0.7f * MathF.Sin(t + i) : 0.2f;
                float bh = 12 * s * Math.Abs(lv);
                float bx = right - 28 * s + i * 4 * s;
                canvas.DrawRoundRect(new SKRect(bx, midY - bh / 2, bx + 2 * s, midY + bh / 2), 1, 1, bar);
            }
        }
    }

    private void DrawTip(SKCanvas canvas, SKRect r, float s, string text)
    {
        float size = 13 * s;
        float tw = MeasureText(text, size);
        float cy = r.Bottom + 21 * s;
        var box = new SKRect(r.MidX - tw / 2 - 12 * s, cy - 14 * s, r.MidX + tw / 2 + 12 * s, cy + 14 * s);
        using (var bg = new SKPaint { Color = Fade(Pal.Body, 0.94f), IsAntialias = true })
            canvas.DrawRoundRect(box, 14 * s, 14 * s, bg);
        using (var edge = new SKPaint
               {
                   Color = BackgroundAlpha(Pal.Danger, 170),
                   IsAntialias = true,
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
               })
            canvas.DrawRoundRect(box, 14 * s, 14 * s, edge);
        DrawText(canvas, text, box.Left + 12 * s, cy + 5 * s, size, Pal.Fg);
    }

    /// <summary>
    /// 通知胶囊：图标（AUMID → 内置站标，否则品牌色块）+ 两行文本（标题粗体 / 正文常规）；
    /// 只有标题时单行。宽度由 <see cref="ToastWidth"/> 按文本自适应。
    /// </summary>
    private void DrawToast(SKCanvas canvas, SKRect r, float s)
    {
        var t = _toast!;
        // 应用图标块（圆角方 + 底色），比裸图标更贴近设计提案
        var block = new SKRect(r.Left + 14 * s, r.Top + 11 * s, r.Left + 44 * s, r.Top + 41 * s);
        string key = t.Aumid.Length > 0 ? t.Aumid : t.App;
        DrawMaterialSurface(canvas, block, 8 * s, Pal.Card);
        var icon = new SKRect(block.Left + 5 * s, block.Top + 5 * s, block.Right - 5 * s, block.Bottom - 5 * s);
        var bmp = IconBitmap(key);
        if (bmp is not null) canvas.DrawBitmap(bmp, icon);
        else Services.AppIcons.DrawFallback(canvas, icon, key);

        float left = r.Left + 55 * s, right = r.Right - 16 * s;
        float bandW = right - left;
        if (bandW <= 24 * s) return;   // 极窄兜底：宁可不画也不叠字
        if (t.Body.Length > 0)
        {
            DrawText(canvas, Ellipsize(t.Title, ToastTitleSize * s, bandW, SKFontStyleWeight.SemiBold),
                left, r.Top + 23 * s, ToastTitleSize * s, Pal.Fg, SKFontStyleWeight.SemiBold);
            DrawText(canvas, Ellipsize(t.Body, ToastBodySize * s, bandW),
                left, r.Top + 38 * s, ToastBodySize * s, Pal.Sub);
        }
        else
        {
            DrawText(canvas, Ellipsize(t.OneLine, ToastTitleSize * s, bandW, SKFontStyleWeight.SemiBold),
                left, r.MidY + 5 * s, ToastTitleSize * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
    }

    /// <summary>当前展开媒体页样式（Normalize 保证只可能是 a/b/c）。</summary>
    private string MediaStyleKey => _cfg.MediaStyle is "b" or "c" ? _cfg.MediaStyle : "a";

    /// <summary>
    /// 展开媒体页的一套控件矩形与排版参数（绘制与命中测试共用，按 <c>media_style</c> 计算）。
    /// A 精修 / B 沉浸 / C 氛围三套布局全在这里，杜绝绘制与命中两套坐标漂移。
    /// 所有坐标都已乘缩放 s（窗口像素）。
    /// </summary>
    internal sealed record MediaChrome(
        SKRect Cover, float CoverRadius, float CoverTiltDeg,
        SKRect TitleBox, float TitleSize, bool TwoLineTitle,
        float TitleY1, float TitleY2, float SubY, float SubSize,
        SKRect SrcChip, SKRect Seek, float TimesY,
        SKRect LyrBox, float LyrCurY, float LyrNextY, float LyrCurSize, float LyrNextSize, bool LyrLeft,
        SKRect BgBar, SKRect Home, SKRect VolGlyph, SKRect VolTrack,
        SKRect Prev, SKRect Play, SKRect Next, float PlayRadius);

    /// <summary>按样式计算媒体页布局。绘制与命中都必须从这里取矩形。</summary>
    private MediaChrome ChromeFor(SKRect r, float s)
    {
        float L = r.Left, T = r.Top, R = r.Right, B = r.Bottom;
        switch (MediaStyleKey)
        {
            case "b":   // 沉浸：封面左上，玻璃控制条收底
            {
                var bar = new SKRect(L + 16 * s, B - 68 * s, R - 16 * s, B - 16 * s);
                float barCy = bar.MidY;
                return new MediaChrome(
                    Cover: new SKRect(L + 20 * s, T + 20 * s, L + 116 * s, T + 116 * s), 20 * s, 0f,
                    TitleBox: new SKRect(L + 134 * s, T + 48 * s, R - 44 * s, T + 104 * s),
                    18 * s, true, T + 68 * s, T + 94 * s, T + 122 * s, 12.5f * s,
                    SrcChip: ChipAt(L + 134 * s, T + 20 * s, 24 * s, s),
                    Seek: new SKRect(L + 26 * s, T + 212 * s, R - 26 * s, T + 240 * s),
                    TimesY: T + 246 * s,
                    LyrBox: new SKRect(L + 26 * s, T + 150 * s, R - 26 * s, T + 208 * s),
                    LyrCurY: T + 172 * s, LyrNextY: T + 200 * s, LyrCurSize: 15.5f * s, LyrNextSize: 12.5f * s, LyrLeft: true,
                    BgBar: bar,
                    Home: new SKRect(r.MidX - 38 * s, barCy - 13 * s, r.MidX + 38 * s, barCy + 13 * s),
                    VolGlyph: new SKRect(R - 136 * s, barCy - 12 * s, R - 112 * s, barCy + 12 * s),
                    VolTrack: new SKRect(R - 104 * s, barCy - 2 * s, R - 34 * s, barCy + 2 * s),
                    Prev: new SKRect(L + 30 * s, barCy - 15 * s, L + 60 * s, barCy + 15 * s),
                    Play: new SKRect(L + 62 * s, barCy - 19 * s, L + 100 * s, barCy + 19 * s),
                    Next: new SKRect(L + 102 * s, barCy - 15 * s, L + 132 * s, barCy + 15 * s),
                    PlayRadius: 19 * s);
            }
            case "c":   // 氛围海报：大封面微倾 + 两行大标题 + 取色光斑背景
            {
                return new MediaChrome(
                    Cover: new SKRect(L + 24 * s, T + 28 * s, L + 128 * s, T + 132 * s), 22 * s, -2.5f,
                    TitleBox: new SKRect(L + 150 * s, T + 36 * s, R - 46 * s, T + 98 * s),
                    21 * s, true, T + 62 * s, T + 89 * s, T + 114 * s, 13 * s,
                    SrcChip: ChipRightAt(R - 52 * s, T + 12 * s, 24 * s, s),
                    Seek: new SKRect(L + 28 * s, T + 226 * s, R - 28 * s, T + 254 * s),
                    TimesY: T + 260 * s,
                    LyrBox: new SKRect(L + 24 * s, T + 166 * s, R - 24 * s, T + 224 * s),
                    LyrCurY: T + 188 * s, LyrNextY: T + 214 * s, LyrCurSize: 15.5f * s, LyrNextSize: 12.5f * s, LyrLeft: false,
                    BgBar: SKRect.Empty,
                    Home: new SKRect(L + 22 * s, B - 46 * s, L + 98 * s, B - 20 * s),
                    VolGlyph: new SKRect(L + 306 * s, B - 42 * s, L + 330 * s, B - 18 * s),
                    VolTrack: new SKRect(L + 338 * s, B - 29 * s, R - 26 * s, B - 25 * s),
                    Prev: new SKRect(L + 149 * s, B - 57 * s, L + 183 * s, B - 23 * s),
                    Play: new SKRect(L + 208 * s, B - 62 * s, L + 252 * s, B - 18 * s),
                    Next: new SKRect(L + 277 * s, B - 57 * s, L + 311 * s, B - 23 * s),
                    PlayRadius: 22 * s);
            }
            default:    // a 精修：结构同旧版（左封面/中进度/底传输），质感重做
            {
                return new MediaChrome(
                    Cover: new SKRect(L + 20 * s, T + 18 * s, L + 104 * s, T + 102 * s), 16 * s, 0f,
                    TitleBox: new SKRect(L + 118 * s, T + 24 * s, R - 20 * s, T + 52 * s),
                    17 * s, false, T + 46 * s, 0f, T + 68 * s, 12.5f * s,
                    SrcChip: ChipAt(L + 118 * s, T + 76 * s, 22 * s, s),
                    Seek: new SKRect(L + 24 * s, T + 136 * s, R - 24 * s, T + 160 * s),
                    TimesY: T + 168 * s,
                    LyrBox: new SKRect(L + 24 * s, T + 180 * s, R - 24 * s, T + 232 * s),
                    LyrCurY: T + 200 * s, LyrNextY: T + 226 * s, LyrCurSize: 15 * s, LyrNextSize: 12 * s, LyrLeft: false,
                    BgBar: SKRect.Empty,
                    Home: PanelSwitchRect(r, s),   // 位置沿用旧「⌂ 面板」矩形（诊断冒烟测试也用它）
                    VolGlyph: new SKRect(L + 308 * s, B - 40 * s, L + 332 * s, B - 16 * s),
                    VolTrack: new SKRect(L + 340 * s, B - 29 * s, R - 26 * s, B - 25 * s),
                    Prev: new SKRect(L + 143 * s, B - 61 * s, L + 177 * s, B - 27 * s),
                    Play: new SKRect(L + 201 * s, B - 67 * s, L + 247 * s, B - 21 * s),
                    Next: new SKRect(L + 271 * s, B - 61 * s, L + 305 * s, B - 27 * s),
                    PlayRadius: 23 * s);
            }
        }
    }

    /// <summary>来源切换 chip（左锚点）。会话 &lt;2 时返回 Empty（不画也不命中）。</summary>
    private SKRect ChipAt(float x, float y, float h, float s)
    {
        float w = SrcChipW(s);
        return w <= 0 ? SKRect.Empty : new SKRect(x, y, x + w, y + h);
    }

    /// <summary>来源切换 chip（右锚点，C 氛围样式放在 ✕ 左侧）。</summary>
    private SKRect ChipRightAt(float rightX, float y, float h, float s)
    {
        float w = SrcChipW(s);
        return w <= 0 ? SKRect.Empty : new SKRect(rightX - w, y, rightX, y + h);
    }

    /// <summary>来源 chip 宽度：文本 + 应用圆点 + 内边距。</summary>
    private float SrcChipW(float s)
    {
        if (_media.Sessions.Count < 2) return 0;
        return MeasureText(SrcChipText(), 11 * s) + 34 * s;
    }

    /// <summary>来源 chip 文本：「‹ 网易云 2/3 ›」（label 截 6 字，与旧切换器一致）。</summary>
    private string SrcChipText()
    {
        var list = _media.Sessions;
        int idx = Math.Clamp(_media.SelectedIndex, 0, list.Count - 1);
        string label = list[idx].Label.Length > 6 ? list[idx].Label[..6] : list[idx].Label;
        return $"‹ {label} {idx + 1}/{list.Count} ›";
    }

    private void DrawExpanded(SKCanvas canvas, SKRect r, float s)
    {
        // 关闭钮（三种媒体样式共用右上角位置）
        DrawText(canvas, "✕", r.Right - 28 * s, r.Top + 24 * s, 14 * s, Pal.Sub);

        if (_focus == "media" && MediaActive)
        {
            float radius = Math.Min((float)(_islandH / 2), (float)MaxRadius) * s;
            var ch = ChromeFor(r, s);
            DrawMediaBackdrop(canvas, r, s, radius);
            DrawMediaInfo(canvas, ch, s);
            if (_media.Sessions.Count > 1) DrawSrcChip(canvas, ch, s);
            DrawSeekbar(canvas, ch, s);
            DrawSeekTimes(canvas, ch, s);
            DrawLyricsBand(canvas, ch, s);
            DrawMediaControls(canvas, ch, s);
        }
        else
        {
            // 氛围光已按用户要求移除：页签下只保留 1px 强调色细线（DrawTabs 里的发色线）
            DrawTabs(canvas, r, s);
            var body = PageBody(r, s);
            switch (_page)
            {
                case 0: DrawPagePlan(canvas, body, s); break;
                case 1: DrawPagePerf(canvas, body, s); break;
                case 2: DrawPageWeather(canvas, body, s); break;
                case 3: DrawPageTasks(canvas, body, s); break;
                case 4: DrawPageMonth(canvas, body, s); break;
                case 5: DrawPageQuick(canvas, body, s); break;
            }
            DrawText(canvas, "滚轮 / 点击页签切换", r.Left + PagePadX * s, r.Top + 303 * s,
                11.5f * s, Pal.Dim);
        }
    }

    /// <summary>展开面板里各页面共用的内容区矩形（绘制与命中测试必须一致，否则点不准）。</summary>
    /// <summary>
    /// 六个页面的内容区：左右内边距 18、顶 59、高 229（424×229）。
    /// 这组数字是从 HTML 原型实测（getBoundingClientRect）得来的规格，不是估的——
    /// 之前凭感觉写的 24/56 让整页比原型紧一截，用户一眼就看出来了。
    /// </summary>
    internal const float PagePadX = 18f, PageTop = 59f, PageBodyH = 229f;

    private static SKRect PageBody(SKRect r, float s)
        => new(r.Left + PagePadX * s, r.Top + PageTop * s,
               r.Right - PagePadX * s, r.Top + (PageTop + PageBodyH) * s);

    /// <summary>
    /// 媒体展开面板左下角的「⌂ 面板」钮：媒体抢占胶囊后，点它切回多页面板（计划/性能/…页签可达）。
    /// 位于传输键左侧的空位（不与 ⏮⏸⏭/音量/歌词重叠）；绘制与命中测试共用，避免坐标漂移。
    /// </summary>
    internal static SKRect PanelSwitchRect(SKRect r, float s)
        => new(r.Left + 16 * s, r.Bottom - 46 * s, r.Left + 84 * s, r.Bottom - 14 * s);

    // ---- 展开媒体页三样式：共享绘制（全部从 ChromeFor 取矩形，命中测试同源）----

    /// <summary>媒体页背景：B=封面模糊铺满，C=取色双光斑，A=用 PaintScene 已铺的面板底色。</summary>
    private void DrawMediaBackdrop(SKCanvas canvas, SKRect r, float s, float radius)
    {
        if (MediaStyleKey == "b") DrawImmersiveBackdrop(canvas, r, s, radius);
        else if (MediaStyleKey == "c") DrawVibrantBackdrop(canvas, r, s, radius);
    }

    /// <summary>
    /// B 沉浸背景：封面高斯模糊 + 遮罩，一次性合成进 <see cref="_blurImg"/>（换曲/换主题/换缩放才重建）。
    /// 每帧只 DrawImage 一次，逐帧成本≈0；没有封面时退化为纯色底。
    /// </summary>
    private void DrawImmersiveBackdrop(SKCanvas canvas, SKRect r, float s, float radius)
    {
        bool dark = Pal.Dark;
        var bmp = CoverBitmap() ?? IconBitmap(_media.State.AppId);
        string key = $"{(bmp is null ? "none" : bmp.GetHashCode())}|{_cfg.Theme}|{_cfg.Opacity}|{s:0.00}|{(int)r.Width}x{(int)r.Height}";
        if (_blurImg is null || key != _blurKey)
        {
            _blurKey = key;
            _blurImg?.Dispose();
            _blurImg = null;
            try
            {
                int w = Math.Max(1, (int)r.Width), h = Math.Max(1, (int)r.Height);
                using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (surf is not null)
                {
                    var c = surf.Canvas;
                    c.Clear(SKColors.Transparent);
                    if (bmp is not null)
                    {
                        // 放大 1.15 倍铺满再模糊：避免边缘模糊采样到透明区发暗
                        float k = Math.Max(w / (float)bmp.Width, h / (float)bmp.Height) * 1.15f;
                        var dest = new SKRect(
                            (w - bmp.Width * k) / 2, (h - bmp.Height * k) / 2,
                            (w + bmp.Width * k) / 2, (h + bmp.Height * k) / 2);
                        using var bp = new SKPaint
                        {
                            IsAntialias = true,
                            FilterQuality = SKFilterQuality.High,
                            ImageFilter = SKImageFilter.CreateBlur(34, 34),
                        };
                        c.DrawBitmap(bmp, dest, bp);
                    }
                    // 遮罩压住模糊层，保证两套主题下文字对比度（对应 HTML 原型的 veil）
                    using (var veil = new SKPaint
                    {
                        Color = BackgroundAlpha(
                            dark ? new SKColor(0, 0, 0) : new SKColor(245, 246, 248),
                            dark ? (byte)175 : (byte)205),
                    })
                        c.DrawRect(0, 0, w, h, veil);
                    _blurImg = surf.Snapshot();
                }
            }
            catch { _blurImg = null; }   // Surface 创建失败等：退化为面板底色
        }
        if (_blurImg is null) return;
        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(r, radius, radius);
            canvas.ClipPath(clip);
            using var imagePaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * OpacityFactor)),
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
            };
            canvas.DrawImage(_blurImg, r.Left, r.Top, imagePaint);
        }
        canvas.Restore();
    }

    /// <summary>C 氛围背景：近黑底 + 封面主/副色两团径向光斑（右上主色、左下副色）。</summary>
    private void DrawVibrantBackdrop(SKCanvas canvas, SKRect r, float s, float radius)
    {
        EnsureVibColors();
        bool dark = Pal.Dark;
        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(r, radius, radius);
            canvas.ClipPath(clip);
            using (var baseFill = new SKPaint
            {
                Color = BackgroundAlpha(
                    dark ? new SKColor(0x0a, 0x0a, 0x0c) : new SKColor(0xfb, 0xfb, 0xfd),
                    255),
                IsAntialias = true,
            })
                canvas.DrawRect(r, baseFill);
            DrawGlow(canvas, new SKPoint(r.Right - 90 * s, r.Top + 60 * s), 250 * s, _vibA, dark ? (byte)185 : (byte)120);
            DrawGlow(canvas, new SKPoint(r.Left + 50 * s, r.Bottom - 10 * s), 200 * s, _vibB, dark ? (byte)140 : (byte)90);
        }
        canvas.Restore();
    }

    /// <summary>
    /// 柔光光斑：模糊实心圆（不用径向渐变——同一个 painter 上 SKShader.CreateRadialGradient
    /// 配 DrawCircle 实测画不出东西，而 MaskFilter 模糊在本工程的岛屿投影/圆环辉光上都验证有效）。
    /// </summary>
    private void DrawGlow(SKCanvas canvas, SKPoint center, float radius, SKColor color, byte alpha)
    {
        using var paint = new SKPaint
        {
            Color = BackgroundAlpha(color, alpha),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * 0.42f),
        };
        canvas.DrawCircle(center, radius * 0.62f, paint);
    }

    /// <summary>
    /// 封面取色：降采样 20×20，按 HSV 色相分 12 桶、以 饱和度×明度 加权，
    /// 取权重最高的两桶（色相拉开才算副色，否则主色偏移 40° 顶上）。换曲才算一次。
    /// </summary>
    private void EnsureVibColors()
    {
        var bmp = CoverBitmap() ?? IconBitmap(_media.State.AppId);
        string key = bmp is null ? "none" : bmp.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key == _vibKey) return;
        _vibKey = key;
        _vibA = new SKColor(0x60, 0xcd, 0xff);
        _vibB = new SKColor(0xa8, 0x55, 0xf7);
        if (bmp is null) return;
        try
        {
            const int n = 20;
            using var small = new SKBitmap(n, n);
            using (var cv = new SKCanvas(small))
                cv.DrawBitmap(bmp, new SKRect(0, 0, n, n), new SKPaint { FilterQuality = SKFilterQuality.Medium });

            var wsum = new double[12];
            var hs = new double[12];
            var ss = new double[12];
            var vs = new double[12];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    var px = small.GetPixel(x, y);
                    px.ToHsv(out float h, out float sat, out float val);
                    if (val < 0.10f) continue;                    // 近黑
                    if (val > 0.97f && sat < 0.08f) continue;     // 近白
                    double w = (double)sat * val;
                    int b = Math.Clamp((int)(h / 30), 0, 11);
                    wsum[b] += w; hs[b] += h * w; ss[b] += sat * w; vs[b] += val * w;
                }
            }
            int i1 = -1, i2 = -1;
            for (int b = 0; b < 12; b++)
            {
                if (wsum[b] <= 0) continue;
                if (i1 < 0 || wsum[b] > wsum[i1]) { i2 = i1; i1 = b; }
                else if (i2 < 0 || wsum[b] > wsum[i2]) i2 = b;
            }

            SKColor Make(int b, double boostS, double boostV)
            {
                if (b < 0 || wsum[b] <= 0) return SKColor.Empty;
                double h = hs[b] / wsum[b];
                double sat = Math.Clamp(ss[b] / wsum[b] * boostS, 0.42, 0.85);
                double val = Math.Clamp(vs[b] / wsum[b] * boostV, 0.40, 0.74);
                return Hsv01((float)h, (float)sat, (float)val);
            }
            float HueDist(int a, int b)
            {
                float d = Math.Abs(a - b) * 30f;
                return Math.Min(d, 360f - d);
            }

            var a = Make(i1, 1.12, 1.05);
            if (!a.Equals(SKColor.Empty)) _vibA = a;
            var b2 = Make(i2, 1.08, 0.95);
            if (!b2.Equals(SKColor.Empty) && HueDist(i1, i2) >= 40)
                _vibB = b2;
            else if (!a.Equals(SKColor.Empty))
            {
                // 没有第二个独立色相：主色偏移 40° 压暗当副色，避免两团光斑重成一个
                a.ToHsv(out float h, out float sat, out float val);
                _vibB = Hsv01((h + 40) % 360, Math.Clamp(sat, 0.40f, 0.80f), Math.Clamp(val * 0.85f, 0.35f, 0.65f));
            }
        }
        catch { /* 取色失败：保留默认两色 */ }
    }

    /// <summary>封面 + 标题 + 副标题（A 单行标题；B/C 两行断行）。</summary>
    private void DrawMediaInfo(SKCanvas canvas, MediaChrome ch, float s)
    {
        var st = _media.State;
        DrawArtStyled(canvas, ch.Cover, ch.CoverRadius, s, ch.CoverTiltDeg, shadow: MediaStyleKey != "a");
        string title = string.IsNullOrWhiteSpace(st.Title) ? "未知" : st.Title;
        if (ch.TwoLineTitle)
        {
            var lines = WrapTwoLines(title, ch.TitleBox.Width, ch.TitleSize, SKFontStyleWeight.SemiBold);
            DrawText(canvas, lines[0], ch.TitleBox.Left, ch.TitleY1, ch.TitleSize, Pal.Fg, SKFontStyleWeight.SemiBold);
            if (lines.Length > 1)
                DrawText(canvas, lines[1], ch.TitleBox.Left, ch.TitleY2, ch.TitleSize, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
        else
        {
            DrawText(canvas, Ellipsize(title, ch.TitleSize, ch.TitleBox.Width, SKFontStyleWeight.SemiBold),
                ch.TitleBox.Left, ch.TitleY1, ch.TitleSize, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
        string sub = MediaStyleKey == "b"
            ? (string.IsNullOrWhiteSpace(st.Artist) ? AppIcons.FriendlyName(st.AppId) : st.Artist)
            : SubLine(st);
        DrawText(canvas, Ellipsize(sub, ch.SubSize, ch.TitleBox.Width),
            ch.TitleBox.Left, ch.SubY, ch.SubSize, Pal.Sub);
    }

    /// <summary>副标题：歌手 · 来源（歌手为空时退回播放状态中文）。</summary>
    private static string SubLine(MediaState st)
    {
        string artist = string.IsNullOrWhiteSpace(st.Artist) ? StatusText(st.Status) : st.Artist;
        return artist + " · " + AppIcons.FriendlyName(st.AppId);
    }

    /// <summary>两行断行标题（中文逐字断行；放不下第三行时第二行打省略号）。纯函数，自测用。</summary>
    internal static string[] WrapTwoLines(string text, float maxW, float size, SKFontStyleWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return new[] { "" };
        if (MeasureText(text, size, weight) <= maxW) return new[] { text };
        var lines = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool overflow = false;
        foreach (var ch in text)
        {
            if (cur.Length > 0 && MeasureText(cur.ToString() + ch, size, weight) > maxW)
            {
                lines.Add(cur.ToString());
                cur.Clear();
                if (lines.Count == 2) { overflow = true; break; }
            }
            cur.Append(ch);
        }
        if (!overflow && cur.Length > 0) lines.Add(cur.ToString());
        if (overflow)
        {
            string second = lines[1];
            while (second.Length > 0 && MeasureText(second + "…", size, weight) > maxW)
                second = second[..^1];
            return new[] { lines[0], second + "…" };
        }
        return lines.ToArray();
    }

    /// <summary>进度条：4px 圆头轨道 + 渐变填充（A/C 主色→白，B 用前景色），悬停出拖动圆点。</summary>
    private void DrawSeekbar(SKCanvas canvas, MediaChrome ch, float s)
    {
        var st = _media.State;
        float ty = ch.Seek.MidY;
        var track = new SKRect(ch.Seek.Left, ty - 2 * s, ch.Seek.Right, ty + 2 * s);
        using (var tp = new SKPaint { Color = Pal.Track, IsAntialias = true })
            canvas.DrawRoundRect(track, 2 * s, 2 * s, tp);
        if (st.DurationMs <= 0) return;
        float p = (float)Math.Min(1, st.PositionMs / (double)st.DurationMs);
        SKColor[] grad = MediaStyleKey == "b"
            ? new[] { Pal.Fg, Pal.Fg }
            : Pal.Dark
                ? new[] { MediaAccent, SKColors.White }
                : new[] { MediaAccent, new SKColor(0x4a, 0xa3, 0xff) };
        float fillW = track.Width * p;
        if (fillW > 0.5f)
        {
            using var fp = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(track.Left, 0), new SKPoint(track.Left + fillW, 0),
                    grad, new float[] { 0f, 1f }, SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + fillW, track.Bottom), 2 * s, 2 * s, fp);
        }
        if (_hover)
        {
            float dx = track.Left + fillW;
            using (var sh = new SKPaint
            {
                Color = Fade(Pal.Shadow, 0.85f),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3 * s),
            })
                canvas.DrawCircle(dx, ty, 7 * s, sh);
            using var dp = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(dx, ty, 5.5f * s, dp);
        }
    }

    /// <summary>进度时间：左=已播，右=-剩余（对齐 Apple Music 习惯）。</summary>
    private void DrawSeekTimes(SKCanvas canvas, MediaChrome ch, float s)
    {
        var st = _media.State;
        if (st.DurationMs <= 0) return;
        DrawText(canvas, Fmt(st.PositionMs), ch.Seek.Left, ch.TimesY, 11 * s, Pal.Sub);
        string neg = "-" + Fmt(st.DurationMs - st.PositionMs);
        DrawText(canvas, neg, ch.Seek.Right - MeasureText(neg, 11 * s), ch.TimesY, 11 * s, Pal.Dim);
    }

    /// <summary>传输键 + ⌂面板 + 音量行（三样式共用；B 的控件收在玻璃条里）。</summary>
    private void DrawMediaControls(SKCanvas canvas, MediaChrome ch, float s)
    {
        bool dark = Pal.Dark;
        var st = _media.State;
        if (!ch.BgBar.IsEmpty)
        {
            // B 沉浸：玻璃控制条（与公共材质同源，透明度可实时调节）
            DrawMaterialSurface(canvas, ch.BgBar, 17 * s, Pal.Card);
        }
        DrawSkipGlyph(canvas, ch.Prev.MidX, ch.Prev.MidY, 15 * s, next: false, Pal.Fg);
        DrawSkipGlyph(canvas, ch.Next.MidX, ch.Next.MidY, 15 * s, next: true, Pal.Fg);
        // 主播放钮：圆形底 + 柔和投影（深色白底黑标 / 浅色墨底白标）
        using (var sh = new SKPaint
        {
            Color = Fade(Pal.Shadow, 0.85f),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8 * s),
        })
            canvas.DrawCircle(ch.Play.MidX, ch.Play.MidY, ch.PlayRadius, sh);
        using (var cp = new SKPaint { Color = dark ? SKColors.White : new SKColor(0x18, 0x18, 0x1a), IsAntialias = true })
            canvas.DrawCircle(ch.Play.MidX, ch.Play.MidY, ch.PlayRadius, cp);
        DrawPlayPauseGlyph(canvas, ch.Play.MidX, ch.Play.MidY, 19 * s, st.IsPlaying,
            dark ? new SKColor(0x0b, 0x0b, 0x0d) : SKColors.White);
        DrawHomeChip(canvas, ch, s);
        DrawVolumeRow(canvas, ch, s);
    }

    /// <summary>上一首/下一首矢量字形（竖条 + 三角，比 emoji 字形锐利且跨字体稳定）。</summary>
    private static void DrawSkipGlyph(SKCanvas c, float cx, float cy, float size, bool next, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        float barW = size * 0.18f, barH = size * 0.62f;
        float edge = size * 0.32f;
        float triH = size * 0.68f;
        using var path = new SKPath();
        if (!next)
        {
            c.DrawRoundRect(new SKRoundRect(
                new SKRect(cx - edge, cy - barH / 2, cx - edge + barW, cy + barH / 2), barW / 2, barW / 2), p);
            path.MoveTo(cx + edge, cy - triH / 2);
            path.LineTo(cx + edge, cy + triH / 2);
            path.LineTo(cx - edge + barW * 0.4f, cy);
        }
        else
        {
            c.DrawRoundRect(new SKRoundRect(
                new SKRect(cx + edge - barW, cy - barH / 2, cx + edge, cy + barH / 2), barW / 2, barW / 2), p);
            path.MoveTo(cx - edge, cy - triH / 2);
            path.LineTo(cx - edge, cy + triH / 2);
            path.LineTo(cx + edge - barW * 0.4f, cy);
        }
        path.Close();
        c.DrawPath(path, p);
    }

    private static void DrawPlayPauseGlyph(SKCanvas c, float cx, float cy, float size, bool playing, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        if (playing)
        {
            float w = size * 0.26f, gap = size * 0.22f, h = size * 0.72f;
            c.DrawRoundRect(new SKRoundRect(new SKRect(cx - gap / 2 - w, cy - h / 2, cx - gap / 2, cy + h / 2), w * 0.35f, w * 0.35f), p);
            c.DrawRoundRect(new SKRoundRect(new SKRect(cx + gap / 2, cy - h / 2, cx + gap / 2 + w, cy + h / 2), w * 0.35f, w * 0.35f), p);
        }
        else
        {
            float h = size * 0.78f, w = size * 0.64f;
            using var path = new SKPath();
            path.MoveTo(cx - w * 0.32f, cy - h / 2);
            path.LineTo(cx + w * 0.68f, cy);
            path.LineTo(cx - w * 0.32f, cy + h / 2);
            path.Close();
            c.DrawPath(path, p);
        }
    }

    /// <summary>「⌂ 面板」胶囊：媒体态唯一回到多页面板的出口。</summary>
    private void DrawHomeChip(SKCanvas canvas, MediaChrome ch, float s)
    {
        var box = ch.Home;
        DrawMaterialSurface(canvas, box, box.Height / 2, Pal.Card);
        float cx = box.Left + 15 * s, cy = box.MidY, u = 5 * s;
        using var hp = new SKPaint
        {
            Color = Pal.Sub, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        using var path = new SKPath();
        path.MoveTo(cx - u, cy - u * 0.1f);
        path.LineTo(cx, cy - u);
        path.LineTo(cx + u, cy - u * 0.1f);
        path.MoveTo(cx - u * 0.72f, cy - u * 0.35f);
        path.LineTo(cx - u * 0.72f, cy + u * 0.9f);
        path.LineTo(cx + u * 0.72f, cy + u * 0.9f);
        path.LineTo(cx + u * 0.72f, cy - u * 0.35f);
        canvas.DrawPath(path, hp);
        DrawText(canvas, "面板", cx + 10 * s, cy + 4.5f * s, 11.5f * s, Pal.Sub);
    }

    /// <summary>
    /// 展开媒体页的音量行：喇叭字形（点击静音切换）+ 可点轨道。
    /// 绘制与命中测试共用 <see cref="MediaChrome"/>.VolGlyph / VolTrack。
    /// </summary>
    private void DrawVolumeRow(SKCanvas canvas, MediaChrome ch, float s)
    {
        // 诊断可注入假音量（真机没声卡时也能出图）
        float? level;
        bool muted;
        if (_volumeOverride is { } ov)
        {
            (level, muted) = ov;
        }
        else
        {
            if (_volumeSvc is null || !_volumeSvc.Available) return;
            level = _volumeSvc.GetVolume();
            if (level is null) return;
            muted = _volumeSvc.IsMuted() == true;
        }

        var glyph = ch.VolGlyph;
        DrawText(canvas, muted ? "🔇" : "🔊", glyph.Left, glyph.MidY + 5 * s, 13 * s, Pal.Sub);

        var track = ch.VolTrack;
        using (var tp = new SKPaint { Color = Pal.Track, IsAntialias = true })
            canvas.DrawRoundRect(track, 2 * s, 2 * s, tp);
        float p = muted ? 0f : level.Value;
        if (p > 0.005f)
        {
            var fill = new SKRect(track.Left, track.Top, track.Left + track.Width * p, track.Bottom);
            using (var fp = new SKPaint { Color = MediaAccent, IsAntialias = true })
                canvas.DrawRoundRect(fill, 2 * s, 2 * s, fp);
        }
    }

    /// <summary>来源切换 chip：应用色点 + 「‹ 网易云 2/3 ›」（绘制与命中共用 MediaChrome.SrcChip）。</summary>
    private void DrawSrcChip(SKCanvas canvas, MediaChrome ch, float s)
    {
        var box = ch.SrcChip;
        if (box.IsEmpty) return;
        DrawMaterialSurface(canvas, box, box.Height / 2, Pal.Card);
        var list = _media.Sessions;
        var cur = list[Math.Clamp(_media.SelectedIndex, 0, list.Count - 1)];
        var (dotA, dotB) = SourceDotColors(cur.AppId);
        var dot = new SKRect(box.Left + 8 * s, box.MidY - 6 * s, box.Left + 20 * s, box.MidY + 6 * s);
        using (var dp = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(dot.Left, dot.Top), new SKPoint(dot.Right, dot.Bottom),
                new[] { dotA, dotB }, new float[] { 0f, 1f }, SKShaderTileMode.Clamp),
        })
            canvas.DrawOval(dot, dp);
        DrawText(canvas, SrcChipText(), box.Left + 26 * s, box.MidY + 4 * s, 11 * s, Pal.Fg);
    }

    /// <summary>来源应用小圆点配色：Edge 青→蓝、网易云红、Chrome 黄→红、其余用强调色。</summary>
    private static (SKColor A, SKColor B) SourceDotColors(string appId)
    {
        string id = appId ?? "";
        if (id.Contains("edge", StringComparison.OrdinalIgnoreCase))
            return (new SKColor(0x35, 0xd0, 0xc8), new SKColor(0x0a, 0x84, 0xff));
        if (id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("netease", StringComparison.OrdinalIgnoreCase))
            return (new SKColor(0xec, 0x41, 0x41), new SKColor(0xb3, 0x23, 0x23));
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            return (new SKColor(0xfb, 0xbc, 0x05), new SKColor(0xea, 0x43, 0x35));
        return (new SKColor(0x60, 0xcd, 0xff), new SKColor(0x0a, 0x84, 0xff));
    }

    // ---- 展开面板：页签 + 六个页面（含「快捷」）----

    /// <summary>页签横向布局（图标 + 文本）。绘制与命中测试共用，避免再次出现两套坐标漂移。
    /// 起点/间距按 HTML 实测：首个在 x=18，页签间距 4。</summary>
    private static (float x, float w)[] LayoutTabs(SKRect r, float s)
    {
        var res = new (float, float)[PageNames.Length];
        float x = r.Left + PagePadX * s;
        for (int i = 0; i < PageNames.Length; i++)
        {
            // padding 0 10 + 图标 11 + gap 5 + 文本
            float w = 10 * s + 11 * s + 5 * s + MeasureText(PageNames[i], 12.5f * s, SKFontStyleWeight.Medium) + 10 * s;
            res[i] = (x, w);
            x += w + 4 * s;
        }
        return res;
    }

    /// <summary>页签条竖直范围：14–48（HTML 实测），页签本体 24 高居中。</summary>
    private static (float top, float bottom) TabBand(SKRect r, float s)
        => (r.Top + 14 * s, r.Top + 48 * s);

    private void DrawTabs(SKCanvas canvas, SKRect r, float s)
    {
        var tabs = LayoutTabs(r, s);
        var (top, bottom) = TabBand(r, s);
        float pillH = 24 * s;
        float pillTop = (top + bottom) / 2f - pillH / 2f;
        for (int i = 0; i < PageNames.Length; i++)
        {
            var (tx, tw) = tabs[i];
            bool on = i == _page;
            var ac = PageAccent(i);
            if (on)
            {
                using var bg = new SKPaint { Color = BackgroundAlpha(ac, 33), IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(tx, pillTop, tx + tw, pillTop + pillH), 8 * s, 8 * s, bg);
            }
            var ic = on ? ac : Pal.Dim;
            float cy = (top + bottom) / 2f;
            DrawTabIcon(canvas, i, tx + 10 * s + 5.5f * s, cy, 5.5f * s, ic);
            DrawText(canvas, PageNames[i], tx + 10 * s + 11 * s + 5 * s, cy + 4.5f * s, 12.5f * s,
                ic, on ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Medium);
        }
        // 页签下的强调色发色线（对齐原型：当前页专属色渐变隐去）
        var lineAc = PageAccent(_page);
        using (var line = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(r.Left + PagePadX * s, 0), new SKPoint(r.Right - 40 * s, 0),
                new[] { BackgroundAlpha(lineAc, 110), BackgroundAlpha(lineAc, 0) },
                new float[] { 0f, 1f }, SKShaderTileMode.Clamp),
        })
            // DrawRect 的 4-float 重载是 (x, y, W, H)——之前把右下角坐标当宽高传，
            // 细线被画成 510×50 的大蓝条（用户截图里的「蓝色的框」）
            canvas.DrawRect(new SKRect(r.Left + PagePadX * s, bottom + 1 * s, r.Right - 40 * s, bottom + 2 * s), line);
    }

    /// <summary>页签小图标（矢量描边，1.5s 线宽；u = 半边长）。每页一枚，颜色随选中态。</summary>
    private static void DrawTabIcon(SKCanvas c, int page, float cx, float cy, float u, SKColor color)
    {
        using var p = new SKPaint
        {
            Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * (u / 5.5f), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        using var path = new SKPath();
        switch (page)
        {
            case 0: // 计划：时钟
                c.DrawCircle(cx, cy, u * 0.82f, p);
                path.MoveTo(cx, cy - u * 0.42f); path.LineTo(cx, cy); path.LineTo(cx + u * 0.34f, cy + u * 0.16f);
                c.DrawPath(path, p);
                break;
            case 1: // 性能：脉搏
                path.MoveTo(cx - u, cy);
                path.LineTo(cx - u * 0.36f, cy);
                path.LineTo(cx - u * 0.12f, cy - u * 0.52f);
                path.LineTo(cx + u * 0.18f, cy + u * 0.52f);
                path.LineTo(cx + u * 0.4f, cy);
                path.LineTo(cx + u, cy);
                c.DrawPath(path, p);
                break;
            case 2: // 天气：云 + 太阳
                c.DrawCircle(cx - u * 0.34f, cy - u * 0.4f, u * 0.34f, p);
                path.MoveTo(cx - u * 0.5f, cy + u * 0.62f);
                path.LineTo(cx + u * 0.66f, cy + u * 0.62f);
                path.AddArc(
                    new SKRect(cx + u * 0.1f, cy + u * 0.02f, cx + u * 0.94f, cy + u * 0.86f), -90, 180);
                path.AddArc(
                    new SKRect(cx - u * 0.42f, cy - u * 0.1f, cx + u * 0.5f, cy + u * 0.62f), -180, 180);
                path.Close();
                c.DrawPath(path, p);
                break;
            case 3: // 日程：列表
                for (int i = -1; i <= 1; i++)
                {
                    float ly = cy + i * u * 0.52f;
                    c.DrawCircle(cx - u * 0.66f, ly, u * 0.11f, new SKPaint { Color = color, IsAntialias = true });
                    path.MoveTo(cx - u * 0.2f, ly); path.LineTo(cx + u * 0.82f, ly);
                    c.DrawPath(path, p);
                    path.Reset();
                }
                break;
            case 4: // 日历
                c.DrawRoundRect(new SKRoundRect(
                    new SKRect(cx - u * 0.8f, cy - u * 0.62f, cx + u * 0.8f, cy + u * 0.72f), u * 0.22f, u * 0.22f), p);
                path.MoveTo(cx - u * 0.8f, cy - u * 0.2f); path.LineTo(cx + u * 0.8f, cy - u * 0.2f);
                c.DrawPath(path, p);
                path.MoveTo(cx - u * 0.34f, cy - u * 0.62f); path.LineTo(cx - u * 0.34f, cy - u * 0.92f);
                c.DrawPath(path, p);
                path.MoveTo(cx + u * 0.34f, cy - u * 0.62f); path.LineTo(cx + u * 0.34f, cy - u * 0.92f);
                c.DrawPath(path, p);
                break;
            default: // 快捷：四宫格
                foreach (var (ox, oy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                    c.DrawRoundRect(new SKRoundRect(
                        new SKRect(cx + ox * u * 0.16f - (ox < 0 ? u * 0.62f : 0), cy + oy * u * 0.16f - (oy < 0 ? u * 0.62f : 0),
                                   cx + ox * u * 0.16f + (ox > 0 ? u * 0.62f : 0), cy + oy * u * 0.16f + (oy > 0 ? u * 0.62f : 0)),
                        u * 0.18f, u * 0.18f), p);
                break;
        }
    }

    /// <summary>页面顶部的同色氛围光（对齐设计提案：每页内容区一层专属径向渐变）。</summary>
    /// <summary>快捷页按钮网格：3 列，绘制与命中测试共用同一套几何。</summary>
    // ---- 「快捷」页：玻璃卡网格（安全动作 + 自定义程序 + 危险动作整行后置）----

    /// <summary>快捷页网格：安全卡（浏览器/命令行/自定义/「＋」槽）按行流排，
    /// 3 个危险动作永远独占最后一行（安全保护，自测断言）。绘制与命中测试共用。</summary>
    internal readonly record struct QuickGrid(SKRect[] Cells, int SafeCount, int SlotIndex, int DangerStart);

    /// <summary>
    /// 快捷页网格（绘制与命中测试共用）。HTML 实测：卡 135×99、列距 145（gap 10）、行距 109（gap 10）、
    /// 网格从内容区顶起（页面内 y22 是提示行，网格紧随其后）。危险动作永远独占最后一行。
    /// </summary>
    internal static QuickGrid QuickGridLayout(SKRect b, float s, int customCount)
    {
        int customs = Math.Clamp(customCount, 0, 6);
        bool hasSlot = customs < 3;
        int safe = 2 + customs + (hasSlot ? 1 : 0);
        int safeRows = (int)Math.Ceiling(safe / 3.0);
        int rows = safeRows + 1;                       // ＋1 = 危险动作行
        float gap = 10 * s, cardW = 135 * s;
        // 高度自适应：2 行时正好 99px（与 HTML 一致）；有自定义程序变 3 行时压缩，
        // 否则 3×99+2×10 = 317px 会超出内容区 207px、把危险动作行挤出岛外
        float cardH = Math.Min(99 * s, (b.Height - gap * (rows - 1)) / rows);
        var cells = new SKRect[safe + 3];
        for (int i = 0; i < safe; i++)
        {
            float x = b.Left + i % 3 * (cardW + gap);
            float y = b.Top + i / 3 * (cardH + gap);
            cells[i] = new SKRect(x, y, x + cardW, y + cardH);
        }
        for (int j = 0; j < 3; j++)
        {
            float x = b.Left + j * (cardW + gap);
            float y = b.Top + safeRows * (cardH + gap);
            cells[safe + j] = new SKRect(x, y, x + cardW, y + cardH);
        }
        return new QuickGrid(cells, safe, hasSlot ? 2 + customs : -1, safe);
    }

    /// <summary>当前快捷页网格（按岛矩形与缩放算，绘制/命中/长按共用）。</summary>
    private QuickGrid QuickGridGeom()
    {
        var (ix, iy, iw, ih) = Island();
        var body = PageBody(new SKRect(ix, iy, ix + iw, iy + ih), _lastScale);
        var grid = new SKRect(body.Left, body.Top + 22 * _lastScale, body.Right, body.Bottom);
        return QuickGridLayout(grid, _lastScale, _cfg.QuickCustoms.Count);
    }

    /// <summary>网格单元 → 动作语义（内置 / 自定义 / 「＋」槽）。</summary>
    private (QuickAction? Act, Config.QuickCustomApp? Custom, bool IsSlot) QuickCell(int i)
    {
        var g = QuickGridGeom();
        if ((uint)i >= (uint)g.Cells.Length) return (null, null, false);
        if (i == g.SlotIndex) return (null, null, true);
        if (i >= g.DangerStart)
            return (QuickActions.All[QuickActions.FirstDestructiveIndex + i - g.DangerStart], null, false);
        if (i is 0 or 1) return (QuickActions.All[i], null, false);
        var customs = _cfg.QuickCustoms;
        int ci = i - 2;
        return (null, ci < customs.Count ? customs[ci] : null, false);
    }

    /// <summary>指针是否落在第 cell 个网格单元上（绘制与命中共用 QuickGridLayout）。</summary>
    private bool ActionHit(double x, double y, int idx, out double cx, out double cy)
    {
        var cells = QuickGridGeom().Cells;
        if (idx < 0 || idx >= cells.Length) { cx = cy = 0; return false; }
        var c = cells[idx];
        cx = c.MidX; cy = c.MidY;
        return c.Contains((float)x, (float)y);
    }

    /// <summary>内置动作的圆钮配色（对齐设计提案）。</summary>
    private static SKColor QuickBallColor(string key) => key switch
    {
        "browser" => new SKColor(0x38, 0xbd, 0xf8),
        "cmd" => new SKColor(0x4a, 0xde, 0x80),
        "sleep" => new SKColor(0x81, 0x8c, 0xf8),
        "restart" => new SKColor(0xfb, 0x92, 0x3c),
        _ => new SKColor(0xf8, 0x71, 0x71),
    };

    /// <summary>内置动作的矢量图标（圆钮内白色描边）。</summary>
    private static void DrawQuickGlyph(SKCanvas c, string key, float cx, float cy, float u, SKColor color)
    {
        using var p = new SKPaint
        {
            Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f * (u / 7f), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        using var path = new SKPath();
        switch (key)
        {
            case "browser": // 地球
                c.DrawCircle(cx, cy, u * 0.72f, p);
                path.MoveTo(cx - u * 0.72f, cy); path.LineTo(cx + u * 0.72f, cy);
                c.DrawPath(path, p);
                path.Reset();
                path.AddOval(new SKRect(cx - u * 0.32f, cy - u * 0.72f, cx + u * 0.32f, cy + u * 0.72f));
                c.DrawPath(path, p);
                break;
            case "cmd": // 终端
                c.DrawRoundRect(new SKRoundRect(
                    new SKRect(cx - u * 0.78f, cy - u * 0.6f, cx + u * 0.78f, cy + u * 0.6f), u * 0.2f, u * 0.2f), p);
                path.MoveTo(cx - u * 0.42f, cy - u * 0.22f); path.LineTo(cx - u * 0.14f, cy); path.LineTo(cx - u * 0.42f, cy + u * 0.22f);
                c.DrawPath(path, p);
                path.Reset();
                path.MoveTo(cx + u * 0.06f, cy + u * 0.26f); path.LineTo(cx + u * 0.46f, cy + u * 0.26f);
                c.DrawPath(path, p);
                break;
            case "sleep": // 月亮
                path.AddArc(new SKRect(cx - u * 0.6f, cy - u * 0.75f, cx + u * 0.6f, cy + u * 0.75f), -55f, 250f);
                path.AddArc(new SKRect(cx - u * 0.05f, cy - u * 0.6f, cx + u * 0.9f, cy + u * 0.6f), 115f, -235f);
                path.Close();
                c.DrawPath(path, new SKPaint { Color = color, IsAntialias = true });
                break;
            case "restart": // 环形箭头
                c.DrawArc(new SKRect(cx - u * 0.68f, cy - u * 0.68f, cx + u * 0.68f, cy + u * 0.68f), -60f, 280f, false, p);
                path.MoveTo(cx + u * 0.62f, cy - u * 0.6f);
                path.LineTo(cx + u * 0.86f, cy - u * 0.1f);
                path.LineTo(cx + u * 0.32f, cy - u * 0.16f);
                path.Close();
                c.DrawPath(path, new SKPaint { Color = color, IsAntialias = true });
                break;
            default: // 关机：电源
                path.MoveTo(cx, cy - u * 0.72f); path.LineTo(cx, cy - u * 0.08f);
                c.DrawPath(path, p);
                c.DrawArc(new SKRect(cx - u * 0.68f, cy - u * 0.68f, cx + u * 0.68f, cy + u * 0.68f), -50f, 280f, false, p);
                break;
        }
    }

    /// <summary>
    /// 「快捷」页：2×3 起步的玻璃方卡（彩色圆钮 + 标签）。危险动作整卡红框 + 长按进度弧；
    /// 「＋」槽位打开自定义程序编辑；自定义卡右键移除由命中层处理。
    /// </summary>
    private void DrawPageQuick(SKCanvas canvas, SKRect b, float s)
    {
        DrawText(canvas, "第一行单击执行 · 危险动作按住 0.9 秒 · 点「＋」添加自定义程序",
            b.Left, b.Top + 11 * s, 10.5f * s, Pal.Dim);
        var g = QuickGridLayout(new SKRect(b.Left, b.Top + 22 * s, b.Right, b.Bottom), s, _cfg.QuickCustoms.Count);
        for (int i = 0; i < g.Cells.Length; i++)
        {
            var cell = g.Cells[i];
            bool pressing = _pressIdx == i;
            bool danger = i >= g.DangerStart;
            bool isSlot = i == g.SlotIndex;
            string label; SKColor ball; string? glyphKey = null; string? letter = null;
            if (isSlot) { label = "添加程序"; ball = Pal.Track; }
            else if (danger)
            {
                var act = QuickActions.All[QuickActions.FirstDestructiveIndex + i - g.DangerStart];
                label = act.Label; ball = QuickBallColor(act.Key); glyphKey = act.Key;
            }
            else if (i is 0 or 1)
            {
                var act = QuickActions.All[i];
                label = act.Label; ball = QuickBallColor(act.Key); glyphKey = act.Key;
            }
            else
            {
                var cu = _cfg.QuickCustoms[i - 2];
                label = cu.Name; ball = ParseColor(cu.Color);
                letter = cu.Name.Length > 0 ? cu.Name[..1].ToUpperInvariant() : "A";
            }

            using (var bg = new SKPaint { Color = pressing ? BackgroundAlpha(Pal.Danger, 90) : Pal.Card, IsAntialias = true })
                canvas.DrawRoundRect(cell, 14 * s, 14 * s, bg);
            // 顶部 1px 内高光（玻璃质感）
            using (var hi = new SKPaint { Color = Pal.Highlight, IsAntialias = true })
                canvas.DrawRoundRect(cell.Left + 2 * s, cell.Top + 1.5f * s, cell.Width - 4 * s, 2 * s, 2 * s, 2 * s, hi);

            // 圆钮/标签按卡高比例定位（不是固定偏移）：卡被压扁时内容仍在卡内
            // 比例取自 HTML 实测：99 高时圆钮中心 40、标签基线 74
            float ballR = Math.Min(14 * s, cell.Height * 0.145f);
            float bcx = cell.MidX, bcy = cell.Top + cell.Height * 0.40f;
            if (isSlot)
            {
                // 虚线框 + 加号
                using (var dash = new SKPaint
                {
                    Color = BackgroundAlpha(Pal.Dim, 160), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * s,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 4 * s, 3 * s }, 0),
                })
                    canvas.DrawRoundRect(cell, 14 * s, 14 * s, dash);
                using (var plus = new SKPaint { Color = Pal.Sub, IsAntialias = true, StrokeWidth = 1.6f * s, StrokeCap = SKStrokeCap.Round })
                {
                    canvas.DrawLine(bcx - 6 * s, bcy, bcx + 6 * s, bcy, plus);
                    canvas.DrawLine(bcx, bcy - 6 * s, bcx, bcy + 6 * s, plus);
                }
            }
            else
            {
                if (danger)
                {
                    using var warn = new SKPaint
                    {
                        Color = BackgroundAlpha(ball, 120), IsAntialias = true,
                        Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f * s,
                    };
                    canvas.DrawRoundRect(cell, 14 * s, 14 * s, warn);
                }
                // 圆钮 + 柔光
                using (var glow = new SKPaint
                {
                    Color = Fade(Pal.Shadow, 0.8f),
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * s),
                })
                    canvas.DrawCircle(bcx, bcy, ballR, glow);
                using (var fill = new SKPaint { Color = ball, IsAntialias = true })
                    canvas.DrawCircle(bcx, bcy, ballR, fill);
                if (glyphKey is not null) DrawQuickGlyph(canvas, glyphKey, bcx, bcy, 8 * s, SKColors.White);
                else
                {
                    float ls = 12 * s;
                    DrawText(canvas, letter ?? "A", bcx - MeasureText(letter ?? "A", ls, SKFontStyleWeight.SemiBold) / 2, bcy + 4.5f * s, ls, SKColors.White, SKFontStyleWeight.SemiBold);
                }
                // 长按进度弧：绕圆钮走满一圈才执行
                if (pressing)
                {
                    double held = (DateTime.UtcNow - _pressAt).TotalMilliseconds;
                    float frac = (float)Math.Clamp(held / QuickActions.HoldMs, 0, 1);
                    using var arc = new SKPaint
                    {
                        Color = BackgroundAlpha(new SKColor(0xff, 0x4d, 0x4d), 255),
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 2.5f * s,
                        StrokeCap = SKStrokeCap.Round,
                    };
                    var box = new SKRect(bcx - ballR - 3.5f * s, bcy - ballR - 3.5f * s, bcx + ballR + 3.5f * s, bcy + ballR + 3.5f * s);
                    canvas.DrawArc(box, -90, 360 * frac, false, arc);
                }
            }
            DrawText(canvas, Ellipsize(label, 11 * s, cell.Width - 10 * s),
                cell.MidX - MeasureText(Ellipsize(label, 11 * s, cell.Width - 10 * s), 11 * s) / 2,
                cell.Top + cell.Height * 0.75f, 11 * s, Pal.Sub);
        }

        string? tip = QuickPressTip();
        if (tip is not null) DrawTip(canvas, b, s, tip);
    }

    /// <summary>「快捷」页底部那行提示：长按中给出进度，误触后给出原因，其余为 null。</summary>
    private string? QuickPressTip()
    {
        if (_pressIdx >= 0)
        {
            var (act, custom, isSlot) = QuickCell(_pressIdx);
            string? label = act?.Label ?? custom?.Name ?? (isSlot ? "添加程序" : null);
            if (label is not null)
            {
                double held = (DateTime.UtcNow - _pressAt).TotalMilliseconds;
                int pct = (int)Math.Clamp(held / QuickActions.HoldMs * 100, 0, 100);
                return $"{label} · 按住不放 {pct}%　松手时还在按钮上才执行";
            }
        }
        return HintActive ? _hint : null;
    }

    /// <summary>
    /// 把 weekdays / dates 渲染成中文说明。
    /// 旧实现是 string.Join 出 "[1,2,3]"，空列表时会画出一对空方括号 —— 视觉上和缺字形的
    /// 「豆腐块」一模一样，被当成乱码。
    /// </summary>
    private static string RulesText(AppConfig cfg)
    {
        var wd = cfg.Weekdays.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(x => x).ToList();
        var ds = cfg.Dates.Where(d => DateTime.TryParse(d, out _)).Distinct().OrderBy(x => x).ToList();

        string w = "";
        if (wd.Count == 7) w = "每天";
        else if (wd.Count > 0)
        {
            bool run = wd.Count > 2 && wd[^1] - wd[0] == wd.Count - 1;
            w = run
                ? $"每周{WdNames[wd[0] - 1]}至{WdNames[wd[^1] - 1]}"
                : "每周" + string.Join("、", wd.Select(d => WdNames[d - 1]));
        }

        var dparts = ds.Select(d =>
        {
            var dt = DateTime.Parse(d);
            return $"{dt.Month}月{dt.Day}日";
        }).ToList();

        if (w.Length == 0 && dparts.Count == 0) return "单次执行 · 不重复";
        if (dparts.Count == 0) return w;
        if (w.Length == 0) return string.Join("、", dparts);
        return $"{w} · 另加 {string.Join("、", dparts)}";
    }

    // ---- 展开面板六页（对齐全页面设计提案）----

    internal static readonly string[] PlanActionKeys = { "shutdown", "lock", "display_off", "pause_media" };

    /// <summary>
    /// 计划页布局（绘制与命中测试共用）。全部数字来自 HTML 原型实测：
    /// 头部 h21；卡片行 y31 高 104（时间卡 150 宽、动作卡起 x159 宽 265）；
    /// 重复行 y144 高 48；星期方块 26×26 起 x50 步长 33；横幅 y201 高 34。
    /// </summary>
    internal readonly record struct PlanLayoutRects(
        SKRect Toggle, SKRect TimeCard, SKRect[] Actions, SKRect AlsoPause, SKRect[] Weekdays, SKRect Banner);

    internal static PlanLayoutRects PlanLayout(SKRect b, float s)
    {
        float L = b.Left;
        var toggle = new SKRect(L + 351 * s, b.Top + 1 * s, L + 388 * s, b.Top + 21 * s);
        float cy = b.Top + 31 * s, cardH = 104 * s;
        var timeCard = new SKRect(L, cy, L + 150 * s, cy + cardH);
        float actL = L + 159 * s, actR = L + 424 * s;
        // 分段器：整体 204 宽起 x172、y64 高 31；四段各自宽（末段「暂停媒体」更宽）
        float segX = L + 172 * s, segY = cy + 33 * s, segH = 23 * s;
        float[] widths = { 42, 42, 42, 64 };
        float wsum = widths.Sum();
        float avail = actR - 20 * s - segX;
        var actions = new SKRect[4];
        float x = segX;
        for (int i = 0; i < 4; i++)
        {
            float w = avail * widths[i] / wsum;
            actions[i] = new SKRect(x, segY, x + w, segY + segH);
            x += w;
        }
        var alsoPause = new SKRect(segX, cy + 70 * s, segX + 37 * s, cy + 90 * s);
        var weekdays = new SKRect[7];
        float repY = b.Top + 155 * s;
        for (int i = 0; i < 7; i++)
        {
            float wx = L + 50 * s + i * 33 * s;
            weekdays[i] = new SKRect(wx, repY, wx + 26 * s, repY + 26 * s);
        }
        var banner = new SKRect(L, b.Top + 201 * s, L + 424 * s, b.Top + 235 * s);
        return new PlanLayoutRects(toggle, timeCard, actions, alsoPause, weekdays, banner);
    }

    /// <summary>iOS 式开关（可 small：同时暂停那颗）。</summary>
    private void DrawToggle(SKCanvas canvas, SKRect r, bool on, SKColor accent)
    {
        using (var bg = new SKPaint { Color = on ? accent : Pal.Track, IsAntialias = true })
            canvas.DrawRoundRect(r, r.Height / 2, r.Height / 2, bg);
        float d = r.Height - 5 * (r.Height / 21f);
        float kx = on ? r.Right - d / 2 - 2.5f * (r.Height / 21f) : r.Left + d / 2 + 2.5f * (r.Height / 21f);
        using (var knob = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawCircle(kx, r.MidY, d / 2, knob);
    }

    private void DrawPagePlan(SKCanvas canvas, SKRect b, float s)
    {
        var ac = PageAccent(0);
        var pl = PlanLayout(b, s);
        bool on = _cfg.Enabled && !_paused;
        float L = b.Left;

        // 头部：标题（15px）+ 状态（10.5px）+ 开关（右缘 37×20）
        DrawText(canvas, "定时计划", L, b.Top + 15 * s, 15 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        string st = _paused ? "已暂停" : on ? "已启用" : "已停用";
        var stColor = _paused ? Pal.Warn : on ? Pal.Ok : Pal.Dim;
        using (var dot = new SKPaint { Color = stColor, IsAntialias = true })
            canvas.DrawCircle(L + 72 * s, b.Top + 10 * s, 3 * s, dot);
        DrawText(canvas, st, L + 79 * s, b.Top + 14 * s, 10.5f * s, stColor);
        DrawToggle(canvas, pl.Toggle, on, ac);

        // 时间卡（150×104）：同色渐变 + 大号时间
        DrawCard(canvas, pl.TimeCard, 14 * s);
        DrawText(canvas, $"{_cfg.Hour:00}:{_cfg.Minute:00}", pl.TimeCard.Left + 15 * s, pl.TimeCard.Top + 53 * s,
            30 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        DrawText(canvas, "执行时刻 · 点击修改", pl.TimeCard.Left + 15 * s, pl.TimeCard.Top + 76 * s, 10 * s, Pal.Dim);

        // 动作卡（265×104）：标题 + 四段选择器 + 同时暂停
        var actCard = new SKRect(L + 159 * s, pl.TimeCard.Top, b.Right, pl.TimeCard.Bottom);
        DrawCard(canvas, actCard, 14 * s);
        DrawText(canvas, "到点动作", actCard.Left + 13 * s, actCard.Top + 18 * s, 10 * s, Pal.Dim);
        for (int i = 0; i < pl.Actions.Length; i++)
        {
            var seg = pl.Actions[i];
            bool sel = _cfg.Action == PlanActionKeys[i];
            using (var segBg = new SKPaint
            {
                Color = sel ? BackgroundAlpha(ac, Pal.Dark ? (byte)48 : (byte)32) : Pal.Track,
                IsAntialias = true,
            })
                canvas.DrawRoundRect(seg, 7 * s, 7 * s, segBg);
            string lb = PowerActions.Get(PlanActionKeys[i]).Label;
            float fs = 11 * s;
            float lw = MeasureText(lb, fs, SKFontStyleWeight.SemiBold);
            if (lw > seg.Width - 6 * s) fs *= (seg.Width - 6 * s) / lw;
            DrawText(canvas, lb, seg.MidX - MeasureText(lb, fs, SKFontStyleWeight.SemiBold) / 2,
                seg.MidY + 4 * s, fs, sel ? ac : Pal.Sub,
                sel ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Medium);
        }
        DrawToggle(canvas, pl.AlsoPause, _cfg.AlsoPauseMedia, ac);
        DrawText(canvas, "到点同时暂停媒体", pl.AlsoPause.Right + 8 * s, pl.AlsoPause.MidY + 4 * s, 11 * s, Pal.Sub);

        // 重复行（424×48）：星期方块 + 指定日期
        var repCard = new SKRect(L, b.Top + 144 * s, L + 424 * s, b.Top + 192 * s);
        DrawCard(canvas, repCard, 14 * s);
        DrawText(canvas, "重复", L + 13 * s, b.Top + 165 * s, 10 * s, Pal.Dim);
        for (int i = 0; i < 7; i++)
        {
            var wd = pl.Weekdays[i];
            bool sel = _cfg.Weekdays.Contains(i + 1);
            using (var wbg = new SKPaint
            {
                Color = sel ? BackgroundAlpha(ac, Pal.Dark ? (byte)44 : (byte)28) : Pal.Card,
                IsAntialias = true,
            })
                canvas.DrawRoundRect(wd, 8 * s, 8 * s, wbg);
            if (sel)
                using (var wbd = new SKPaint { Color = BackgroundAlpha(ac, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 * s })
                    canvas.DrawRoundRect(wd, 8 * s, 8 * s, wbd);
            DrawText(canvas, WdNames[i],
                wd.MidX - MeasureText(WdNames[i], 11 * s, SKFontStyleWeight.SemiBold) / 2,
                wd.MidY + 4 * s, 11 * s, sel ? ac : Pal.Sub,
                sel ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Medium);
        }
        using (var dash = new SKPaint
        {
            Color = Pal.Track, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 * s,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4 * s, 3 * s }, 0),
        })
            canvas.DrawRoundRect(new SKRect(L + 299 * s, b.Top + 155 * s, L + 374 * s, b.Top + 181 * s), 8 * s, 8 * s, dash);
        DrawText(canvas, "＋ 指定日期", L + 310 * s, b.Top + 172 * s, 10 * s, Pal.Dim);

        // 下次执行横幅（424×34）：强调色渐变 + 闪电
        using (var bb = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(pl.Banner.Left, 0), new SKPoint(pl.Banner.Right, 0),
                new[] { BackgroundAlpha(ac, Pal.Dark ? (byte)52 : (byte)30), BackgroundAlpha(ac, 0) },
                new float[] { 0f, 0.78f }, SKShaderTileMode.Clamp),
        })
            canvas.DrawRoundRect(pl.Banner, 11 * s, 11 * s, bb);
        using (var bbd = new SKPaint { Color = BackgroundAlpha(ac, Pal.Dark ? (byte)80 : (byte)60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 * s })
            canvas.DrawRoundRect(pl.Banner, 11 * s, 11 * s, bbd);
        using (var zap = new SKPaint { Color = ac, IsAntialias = true })
        {
            float zx = pl.Banner.Left + 16 * s, zy = pl.Banner.MidY, z = 5.5f * s;
            using var zp = new SKPath();
            zp.MoveTo(zx + z * 0.2f, zy - z);
            zp.LineTo(zx - z * 0.55f, zy + z * 0.15f);
            zp.LineTo(zx + z * 0.02f, zy + z * 0.15f);
            zp.LineTo(zx - z * 0.2f, zy + z);
            zp.LineTo(zx + z * 0.55f, zy - z * 0.12f);
            zp.LineTo(zx - z * 0.02f, zy - z * 0.12f);
            zp.Close();
            canvas.DrawPath(zp, zap);
        }
        string bannerText;
        if (on && _target is { } tgt)
        {
            var left = tgt - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string when = tgt.Date == DateTime.Today ? "今天"
                : tgt.Date == DateTime.Today.AddDays(1) ? "明天"
                : $"{tgt.Month}/{tgt.Day}";
            bannerText = $"下次执行 {when} {tgt:HH:mm} · 剩 {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
        }
        else
        {
            bannerText = "未安排 · 打开开关并选择重复日期";
        }
        DrawText(canvas, bannerText, pl.Banner.Left + 30 * s, pl.Banner.MidY + 4 * s, 11.5f * s, Pal.Fg, SKFontStyleWeight.SemiBold);
    }

    /// <summary>
    /// 玻璃卡：填充（可选同色渐变染）+ 1px 描边 + 顶部内高光。
    /// 后两项对应 HTML 原型 `.card{ border:1px solid rgba(255,255,255,.10);
    /// box-shadow: inset 0 1px 0 rgba(255,255,255,.05) }`——缺了它们卡片会显得又平又糊。
    /// </summary>
    /// <summary>
    /// 玻璃卡：平铺填充 + 1px 描边 + 顶部内高光。
    /// 原型的卡片渐变染（accent 13%）实测只比纯卡片亮 ~14，为避开 SkiaSharp
    /// 渐变着色器的 alpha 放大问题（实测 ×3.8），这里不画染层。
    /// </summary>
    private void DrawCard(SKCanvas canvas, SKRect r, float radius)
        => DrawMaterialCard(canvas, r, radius);

    private void DrawPagePerf(SKCanvas canvas, SKRect b, float s)
    {
        var ac = PageAccent(1);
        if (_perf is null)
        {
            string wait = "正在采集…";
            DrawText(canvas, wait, b.MidX - MeasureText(wait, 13 * s) / 2, b.MidY, 13 * s, Pal.Sub);
            return;
        }
        var m = _perf;
        var layout = PerfLayout(b, s, _cfg.PerfNetwork);
        // HTML 实测：圆环卡 207×165；下方 mini 卡按 135×54、间距 10 排列。
        DrawRingCard(canvas, layout.Cpu, s, "CPU",
            $"{Math.Clamp(m.Cpu, 0, 100):0}", m.Cpu, ac, null);
        DrawRingCard(canvas, layout.Memory, s, "内存",
            $"{Math.Clamp(m.MemPct, 0, 100):0}", m.MemPct, Pal.Accent,
            $"{m.MemUsedGb:0.0} / {m.MemTotalGb:0.0} GB");

        int i = 0;
        if (_cfg.PerfNetwork)
        {
            DrawNetworkCard(canvas, layout.Minis[i++], s, m.NetKbps, m.UploadKbps);
        }
        DrawMiniCard(canvas, layout.Minis[i++], s, "磁盘读", FmtRate(m.DiskReadKbps));
        DrawMiniCard(canvas, layout.Minis[i], s, "开机时长", FmtUptime(m.UptimeSeconds));
    }

    internal readonly record struct PerfLayoutRects(SKRect Cpu, SKRect Memory, SKRect[] Minis);

    /// <summary>性能页布局：网络开关只影响下方卡数量，不改变上方圆环尺寸。</summary>
    internal static PerfLayoutRects PerfLayout(SKRect b, float s, bool showNetwork)
    {
        float ringW = 207 * s, ringH = 165 * s, gap = 10 * s;
        var cpu = new SKRect(b.Left, b.Top, b.Left + ringW, b.Top + ringH);
        var memory = new SKRect(b.Left + ringW + gap, b.Top, b.Right, b.Top + ringH);
        float y = b.Top + 175 * s, h = 54 * s, w = 135 * s;
        int count = showNetwork ? 3 : 2;
        var minis = new SKRect[count];
        float[] starts = { 0, 145, 289 };
        for (int i = 0; i < count; i++)
        {
            float x = b.Left + starts[i] * s;
            minis[i] = new SKRect(x, y, x + w, y + h);
        }
        return new PerfLayoutRects(cpu, memory, minis);
    }

    private void DrawNetworkCard(SKCanvas canvas, SKRect card, float s, double downloadKbps, double uploadKbps)
    {
        DrawCard(canvas, card, 12 * s);
        float size = 10.5f * s;
        float labelW = MeasureText("下载", size);
        float valueX = card.Left + 10 * s + labelW + 7 * s;
        float maxW = card.Right - 9 * s - valueX;
        string down = Ellipsize(FmtRate(downloadKbps), size, maxW, SKFontStyleWeight.SemiBold);
        string up = Ellipsize(FmtRate(uploadKbps), size, maxW, SKFontStyleWeight.SemiBold);
        float y1 = card.MidY - 4.5f * s, y2 = card.MidY + 13.5f * s;
        DrawText(canvas, "下载", card.Left + 10 * s, y1, size, Pal.Dim);
        DrawText(canvas, down, valueX, y1, size, Pal.Fg, SKFontStyleWeight.SemiBold);
        DrawText(canvas, "上传", card.Left + 10 * s, y2, size, Pal.Dim);
        DrawText(canvas, up, valueX, y2, size, Pal.Fg, SKFontStyleWeight.SemiBold);
    }

    internal static string FmtRate(double kbps)
    {
        if (double.IsNaN(kbps) || double.IsInfinity(kbps) || kbps < 0) kbps = 0;
        return kbps >= 1024 ? $"{kbps / 1024:0.0} MB/s" : $"{kbps:0} KB/s";
    }

    internal static string FmtUptime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        long totalMinutes = (long)Math.Floor(seconds / 60);
        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes / 60 % 24;
        long minutes = totalMinutes % 60;
        if (days > 0) return $"{days}天 {hours}小时";
        if (hours > 0) return $"{hours}小时 {minutes}分";
        return $"{minutes}分";
    }

    /// <summary>圆环卡（207×165）：轨道 + 同色辉光弧 + 环心数值 + 卡底标签。位置按 HTML 实测。</summary>
    private void DrawRingCard(SKCanvas canvas, SKRect card, float s, string label, string valueText,
        double pct, SKColor accent, string? sub)
    {
        DrawCard(canvas, card, 14 * s);
        float r = 35 * s;
        float cx = card.MidX, cy = card.Top + 76 * s;
        using (var track = new SKPaint { Color = Pal.Track, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * s, StrokeCap = SKStrokeCap.Round })
            canvas.DrawCircle(cx, cy, r, track);
        double v = Math.Clamp(pct, 0, 100);
        if (v > 0.5)
        {
            var arcRect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            using (var glow = new SKPaint
            {
                Color = BackgroundAlpha(accent, 120), IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 6 * s, StrokeCap = SKStrokeCap.Round,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5 * s),
            })
                canvas.DrawArc(arcRect, -90, (float)(v / 100 * 360), false, glow);
            using var arc = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * s, StrokeCap = SKStrokeCap.Round };
            canvas.DrawArc(arcRect, -90, (float)(v / 100 * 360), false, arc);
        }
        float numSize = 20 * s, pctSize = 9.5f * s;
        float nw = MeasureText(valueText, numSize, SKFontStyleWeight.SemiBold);
        float pw = MeasureText("%", pctSize);
        float startX = cx - (nw + 2 * s + pw) / 2f;
        DrawText(canvas, valueText, startX, cy + 7 * s, numSize, Pal.Fg, SKFontStyleWeight.SemiBold);
        DrawText(canvas, "%", startX + nw + 2 * s, cy + 7 * s, pctSize, Pal.Sub);
        DrawText(canvas, label, cx - MeasureText(label, 10.5f * s) / 2,
            card.Bottom - 41 * s, 10.5f * s, Pal.Sub);
        if (sub is not null)
            DrawText(canvas, sub, cx - MeasureText(sub, 9 * s) / 2, card.Bottom - 18 * s, 9 * s, Pal.Dim);
    }

    /// <summary>小卡（135×54）：标签 + 数值，整体在卡内垂直居中。</summary>
    private void DrawMiniCard(SKCanvas canvas, SKRect card, float s, string label, string value)
    {
        DrawCard(canvas, card, 12 * s);
        DrawText(canvas, label, card.MidX - MeasureText(label, 9.5f * s) / 2, card.MidY - 4 * s, 9.5f * s, Pal.Dim);
        string shown = Ellipsize(value, 13.5f * s, card.Width - 10 * s, SKFontStyleWeight.SemiBold);
        DrawText(canvas, shown, card.MidX - MeasureText(shown, 13.5f * s, SKFontStyleWeight.SemiBold) / 2,
            card.MidY + 16 * s, 13.5f * s, Pal.Fg, SKFontStyleWeight.SemiBold);
    }

    private void DrawPageWeather(SKCanvas canvas, SKRect b, float s)
    {
        var ac = PageAccent(2);
        if (_weather is null)
        {
            string wait = "正在获取天气…";
            DrawText(canvas, wait, b.MidX - MeasureText(wait, 13 * s) / 2, b.MidY, 13 * s, Pal.Sub);
            return;
        }
        var w = _weather;
        if (!w.Ok)
        {
            DrawText(canvas, "天气暂不可用", b.Left, b.Top + 24 * s, 15 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
            DrawText(canvas, Ellipsize(w.Error ?? "无法定位", 11.5f * s, b.Width), b.Left, b.Top + 50 * s, 11.5f * s, Pal.Dim);
            return;
        }
        // HTML 实测：当前卡 217×225（x0/y0）；逐小时卡 197×165（x227/y0）；描述卡 197×50（x227/y175）
        var nowCard = new SKRect(b.Left, b.Top, b.Left + 217 * s, b.Top + 225 * s);
        DrawCard(canvas, nowCard, 14 * s);
        float px = nowCard.Left + 14 * s, py = nowCard.Top + 22 * s;
        using (var pin = new SKPaint { Color = Pal.Sub, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * s })
        {
            canvas.DrawCircle(px, py - 2 * s, 3.2f * s, pin);
            using var p3 = new SKPath();
            p3.MoveTo(px - 3.4f * s, py);
            p3.LineTo(px, py + 4.6f * s);
            p3.LineTo(px + 3.4f * s, py);
            canvas.DrawPath(p3, pin);
        }
        DrawText(canvas, Ellipsize(w.City, 11 * s, nowCard.Width - 40 * s), px + 9 * s, nowCard.Top + 26 * s, 11 * s, Pal.Sub);
        // HTML 实测：大温度基线在卡内 rel128，度数符号与大数字同基线（不是上标）
        DrawText(canvas, $"{w.TempC:0.#}", nowCard.Left + 12 * s, nowCard.Top + 128 * s, 42 * s, Pal.Fg, SKFontStyleWeight.Light);
        float tW = MeasureText($"{w.TempC:0.#}", 42 * s, SKFontStyleWeight.Light);
        DrawText(canvas, "°", nowCard.Left + 12 * s + tW + 2 * s, nowCard.Top + 128 * s, 20 * s, Pal.Sub);
        DrawText(canvas, Ellipsize(w.Desc, 14 * s, nowCard.Width - 28 * s, SKFontStyleWeight.SemiBold),
            nowCard.Left + 14 * s, nowCard.Top + 150 * s, 14 * s, Pal.Fg, SKFontStyleWeight.SemiBold);

        float rx = b.Left + 227 * s;
        var hourCard = new SKRect(rx, b.Top, b.Right, b.Top + 165 * s);
        DrawCard(canvas, hourCard, 14 * s);
        // HTML 实测：5 列（现在 + 未来 4 个整点），内容靠卡片顶部排（标签基线 rel24 / 符号 rel46 / 温度 rel64）
        var hours = w.Hourly ?? Array.Empty<(int Hour, double Temp, double Code)>();
        int n = Math.Min(5, hours.Length);
        float colW = hourCard.Width / Math.Max(1, n);
        for (int i = 0; i < n; i++)
        {
            float colCx = hourCard.Left + colW * i + colW / 2f;
            string hh = i == 0 ? "现在" : $"{hours[i].Hour:00}时";
            string tt = $"{hours[i].Temp:0}°", dd = WmoShort(hours[i].Code);
            DrawText(canvas, hh, colCx - MeasureText(hh, 9 * s) / 2, hourCard.Top + 24 * s, 9 * s, Pal.Dim);
            DrawText(canvas, dd, colCx - MeasureText(dd, 12 * s) / 2, hourCard.Top + 46 * s, 12 * s, ac);
            DrawText(canvas, tt, colCx - MeasureText(tt, 12.5f * s, SKFontStyleWeight.SemiBold) / 2,
                hourCard.Top + 66 * s, 12.5f * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        }
        if (n == 0)
        {
            string none = "暂无预报";
            DrawText(canvas, none, hourCard.MidX - MeasureText(none, 10.5f * s) / 2, hourCard.MidY + 4 * s, 10.5f * s, Pal.Dim);
        }
        // HTML 实测：三张 59×49 小卡（湿度 / 风速 / 云量）
        float mw = 59 * s;
        for (int i = 0; i < 3; i++)
        {
            var mc = new SKRect(rx + i * (mw + 10 * s), b.Top + 175 * s, rx + i * (mw + 10 * s) + mw, b.Top + 225 * s);
            string[] labels = { "湿度", "风速", "云量" };
            string[] vals =
            {
                w.Humidity is { } h ? $"{h:0}%" : "—",
                w.WindKph is { } k ? $"{k:0} km/h" : "—",
                w.Cloud is { } c2 ? $"{c2:0}%" : "—",
            };
            DrawMiniCard(canvas, mc, s, labels[i], vals[i]);
        }
    }

    private static string WmoShort(double code) => code switch
    {
        0 => "晴",
        >= 1 and <= 3 => "多云",
        >= 45 and <= 48 => "雾",
        >= 51 and <= 67 => "雨",
        >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨",
        >= 95 => "雷雨",
        _ => "—",
    };

    /// <summary>
    /// 事项页布局（绘制与命中测试共用，错位就会点不准）。
    /// ≤5 条单列（显示时间）；>5 条双列网格（名称 + 删除钮）。12 条封顶，全部可见。
    /// </summary>
    internal const float TaskRowH = 38f, TaskRowGap = 6f, TaskAddH = 33f, TaskDelW = 24f;

    /// <summary>日程页布局（绘制与命中测试共用）。HTML 实测：行高 38、步长 44、新建条 33 高在 y178。</summary>
    internal static (SKRect[] cells, SKRect[] dels, SKRect add, int cols) TaskLayout(SKRect b, float s, int count)
    {
        int n = Math.Max(0, Math.Min(count, 12));
        int cols = n > 5 ? 2 : 1;
        int rowCount = n == 0 ? 0 : (n + cols - 1) / cols;
        float gap = TaskRowGap * s;
        // 新建条是列表后面的流式元素（HTML 里也是 flex 末项，不是固定 y）：
        // 4 条时正好落在 y178，与原型一致；条数多时行高自动压缩，保证整体不越界
        float availH = b.Height - TaskAddH * s - gap;
        float rowH = TaskRowH * s;
        if (rowCount > 0 && rowCount * (rowH + gap) - gap > availH)
            rowH = Math.Max(20 * s, availH / rowCount - gap);
        float colW = (b.Width - (cols - 1) * gap) / cols;

        var cells = new SKRect[n];
        var dels = new SKRect[n];
        for (int i = 0; i < n; i++)
        {
            int c = i % cols, r = i / cols;
            float x = b.Left + c * (colW + gap);
            float y = b.Top + r * (rowH + gap);
            cells[i] = new SKRect(x, y, x + colW, y + rowH);
            dels[i] = new SKRect(x + colW - TaskDelW * s, y, x + colW, y + rowH);
        }
        float addTop = rowCount == 0 ? b.Top : b.Top + rowCount * (rowH + gap);
        var add = new SKRect(b.Left, addTop, b.Right, addTop + TaskAddH * s);
        return (cells, dels, add, cols);
    }

    private void DrawPageTasks(SKCanvas canvas, SKRect b, float s)
    {
        int count = _cfg.Tasks.Count;
        var (cells, dels, add, cols) = TaskLayout(b, s, count);
        float nameSize = Math.Min(12.5f * s, cells.Length > 0 ? cells[0].Height * 0.4f : 12.5f * s);
        var ac = PageAccent(TasksPageIndex);

        if (count == 0)
        {
            DrawText(canvas, "暂无日程", b.Left, b.Top + 24 * s, 14 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
            DrawText(canvas, "点下面的「＋ 新建日程」，直接在岛上添加", b.Left, b.Top + 50 * s, 11.5f * s, Pal.Dim);
        }
        for (int i = 0; i < cells.Length; i++)
        {
            var t = _cfg.Tasks[i];
            var row = cells[i];
            DrawMaterialCard(canvas, row, 9 * s);

            // 勾选圈：点它完成 / 取消完成（HTML 实测 16×16 起 x13）
            var dc = new SKRect(row.Left + 13 * s, row.MidY - 8 * s, row.Left + 29 * s, row.MidY + 8 * s);
            if (t.Done)
            {
                using var fill = new SKPaint { Color = ac, IsAntialias = true };
                canvas.DrawCircle(dc.MidX, dc.MidY, 8 * s, fill);
                using var ck = new SKPaint
                {
                    Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.6f * s, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                };
                using var p2 = new SKPath();
                p2.MoveTo(dc.MidX - 3.5f * s, dc.MidY + 0.5f * s);
                p2.LineTo(dc.MidX - 1 * s, dc.MidY + 3.2f * s);
                p2.LineTo(dc.MidX + 4 * s, dc.MidY - 2.8f * s);
                canvas.DrawPath(p2, ck);
            }
            else
            {
                using var ring = new SKPaint { Color = Pal.Track, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s };
                canvas.DrawCircle(dc.MidX, dc.MidY, 7.5f * s, ring);
            }

            // 分类色点（HTML 实测 8×8 起 x39）
            using (var p = new SKPaint { Color = ParseColor(t.Color), IsAntialias = true })
                canvas.DrawCircle(row.Left + 43 * s, row.MidY, 4 * s, p);

            // 名称（完成态划线置灰；右端给 删除/分类/时间 让位）
            float nameLeft = row.Left + 57 * s;
            float catW = cols == 1 ? MeasureText(Ellipsize(t.Category, 9 * s, 46 * s), 9 * s) + 14 * s : 0;
            float timeW = cols == 1 ? MeasureText(t.Time, 10.5f * s) + 10 * s : 0;
            float nameRight = dels[i].Left - 8 * s - catW - timeW;
            string name = Ellipsize(t.Name, nameSize, Math.Max(24 * s, nameRight - nameLeft));
            DrawText(canvas, name, nameLeft, row.MidY + nameSize * 0.36f, nameSize, t.Done ? Pal.Dim : Pal.Fg);
            if (t.Done)
            {
                float nw = MeasureText(name, nameSize);
                using var strike = new SKPaint { Color = Pal.Dim, IsAntialias = true, StrokeWidth = 1 * s };
                canvas.DrawLine(nameLeft, row.MidY + 1 * s, nameLeft + nw, row.MidY + 1 * s, strike);
            }

            // 分类 chip + 时间（单列时）
            if (cols == 1)
            {
                float tx = dels[i].Left - 8 * s - timeW;
                float cx2 = tx - catW;
                var chipRect = new SKRect(cx2, row.MidY - 7 * s, cx2 + catW, row.MidY + 7 * s);
                DrawMaterialSurface(canvas, chipRect, 6 * s, Pal.Track);
                DrawText(canvas, Ellipsize(t.Category, 9 * s, catW - 8 * s), cx2 + 7 * s, row.MidY + 3 * s, 9 * s, Pal.Sub);
                DrawText(canvas, t.Time, tx + 4 * s, row.MidY + 4 * s, 10.5f * s, Pal.Sub);
            }
            DrawText(canvas, "✕", dels[i].MidX - MeasureText("✕", 12 * s) / 2, row.MidY + 4 * s, 12 * s, Pal.Dim);
        }

        // ＋ 新建
        bool canAdd = count < 12;
            DrawMaterialSurface(canvas, add, 9 * s, canAdd ? Pal.Card : Pal.Track);
        string addText = canAdd ? "＋ 新建日程" : "日程已满（12）";
        float aw = MeasureText(addText, 12.5f * s, SKFontStyleWeight.SemiBold);
        DrawText(canvas, addText, add.MidX - aw / 2, add.MidY + 5 * s, 12.5f * s,
            canAdd ? ac : Pal.Dim, SKFontStyleWeight.SemiBold);
    }

    private static SKColor ParseColor(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new SKColor(r, g, b);
        return new SKColor(0x00, 0xa0, 0xff);
    }

    /// <summary>日历页相对本月偏移（‹ › 翻月）对应的第一天。</summary>
    private DateTime CalFirst => new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(_calOffset);

    /// <summary>日历页布局（绘制与命中测试共用）：翻月钮 / 星期行基线 / 网格首行中心与格距。</summary>
    internal readonly record struct CalNavRects(SKRect Prev, SKRect Next, float WeekdayY, float GridTop, float CellW, float RowH);

    /// <summary>日历页布局（绘制/命中共用）。HTML 实测：‹ › 24×24 起 y2、星期行 y34（19 高）、
    /// 日期网格从 y53 起、行高 26、步长 28。</summary>
    internal static CalNavRects CalLayout(SKRect b, float s)
    {
        var prev = new SKRect(b.Right - 57 * s, b.Top + 2 * s, b.Right - 33 * s, b.Top + 26 * s);
        var next = new SKRect(b.Right - 28 * s, b.Top + 2 * s, b.Right - 4 * s, b.Top + 26 * s);
        return new CalNavRects(prev, next, b.Top + 51 * s, b.Top + 68 * s, b.Width / 7f, 28 * s);
    }

    private void DrawPageMonth(SKCanvas canvas, SKRect b, float s)
    {
        var monthAc = PageAccent(4);
        var planAc = PageAccent(0);
        var first = CalFirst;
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        var cl = CalLayout(b, s);
        var today = DateTime.Today;

        // HTML 实测：月份标题基线 y19（body-rel）、年份基线 y18、翻月钮在右上 y2
        DrawText(canvas, $"{first.Month} 月", b.Left + 4 * s, b.Top + 20 * s, 15 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        DrawText(canvas, $"{first.Year}",
            b.Left + MeasureText($"{first.Month} 月", 15 * s, SKFontStyleWeight.SemiBold) + 12 * s,
            b.Top + 20 * s, 10.5f * s, Pal.Dim);
        foreach (var (btn, ch) in new[] { (cl.Prev, "‹"), (cl.Next, "›") })
        {
            DrawMaterialSurface(canvas, btn, 7 * s, Pal.Card);
            DrawText(canvas, ch, btn.MidX - MeasureText(ch, 12 * s) / 2, btn.MidY + 4 * s, 12 * s, Pal.Sub);
        }
        for (int i = 0; i < 7; i++)
            DrawText(canvas, WdNames[i],
                b.Left + i * cl.CellW + (cl.CellW - MeasureText(WdNames[i], 9.5f * s)) / 2,
                cl.WeekdayY, 9.5f * s, Pal.Dim);

        int lead = ((int)first.DayOfWeek + 6) % 7;   // 周一为第一列
        for (int d = 1; d <= days; d++)
        {
            int idx = lead + d - 1;
            float cx = b.Left + idx % 7 * cl.CellW + cl.CellW / 2f;
            float cy = cl.GridTop + idx / 7 * cl.RowH;
            var date = new DateTime(first.Year, first.Month, d);
            int iso = ((int)date.DayOfWeek + 6) % 7 + 1;
            bool isToday = date == today;
            // 计划中 = 命中星期规则或显式日期（不看 Enabled：停用时也想看到既定安排）
            bool planned = _cfg.Weekdays.Contains(iso) || _cfg.Dates.Contains(date.ToString("yyyy-MM-dd"));
            string txt = d.ToString();
            float tw = MeasureText(txt, 11 * s);
            if (isToday)
            {
                using var p = new SKPaint { Color = monthAc, IsAntialias = true };
                canvas.DrawCircle(cx, cy, 11 * s, p);
                DrawText(canvas, txt, cx - tw / 2f, cy + 4 * s, 11 * s,
                    Pal.Dark ? new SKColor(0x20, 0x15, 0x00) : SKColors.White, SKFontStyleWeight.SemiBold);
            }
            else
            {
                DrawText(canvas, txt, cx - tw / 2f, cy + 4 * s, 11 * s, Pal.Fg);
            }
            // 有计划的日子在数字下方点一个蓝点（今天已是实心圆，不再叠点）
            if (planned && !isToday)
                using (var dot = new SKPaint { Color = planAc, IsAntialias = true })
                    canvas.DrawCircle(cx, cy + 10.5f * s, 2.6f * s, dot);
        }
        DrawText(canvas, "点日期加入 / 移出计划", b.Left, b.Top + 232 * s, 9.5f * s, Pal.Dim);
        string rules = Ellipsize(RulesText(_cfg), 10 * s, b.Width - 160 * s);
        DrawText(canvas, rules, b.Right - MeasureText(rules, 10 * s), b.Top + 232 * s, 10 * s, Pal.Sub);
    }

    /// <summary>退出确认的两个按钮（绘制与命中测试共用，避免坐标漂移）。</summary>
    internal static (SKRect cancel, SKRect quit) ConfirmButtons(SKRect r, float s)
    {
        float bw = 118 * s, bh = 36 * s, gap = 14 * s;
        float total = bw * 2 + gap;
        float left = r.MidX - total / 2;
        float top = r.Bottom - bh - 18 * s;
        return (new SKRect(left, top, left + bw, top + bh),
                new SKRect(left + bw + gap, top, left + total, top + bh));
    }

    /// <summary>
    /// 退出确认：托盘「退出」触发。画在岛上而不是弹 MessageBox——
    /// 岛是置顶分层窗，永远可见；也就绕开了"从托盘菜单弹模态框弹不出来"的问题。
    /// 右上角 12 秒自动取消环（对齐设计提案）。
    /// </summary>
    private void DrawConfirm(SKCanvas canvas, SKRect r, float s)
    {
        DrawText(canvas, "退出灵云？", r.MidX - MeasureText("退出灵云？", 17 * s, SKFontStyleWeight.SemiBold) / 2,
            r.Top + 52 * s, 17 * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        string sub = "岛将从桌面收起，定时计划停止执行";
        DrawText(canvas, sub, r.MidX - MeasureText(sub, 11.5f * s) / 2, r.Top + 78 * s, 11.5f * s, Pal.Sub);
        // 12 秒自动取消环
        if (_confirmAt is { } at)
        {
            float left01 = (float)Math.Clamp(1 - (DateTime.UtcNow - at).TotalSeconds / ConfirmTimeoutSec, 0, 1);
            float rcx = r.Right - 30 * s, rcy = r.Top + 26 * s, rr = 10.5f * s;
            using (var bg = new SKPaint { Color = Pal.Track, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f * s })
                canvas.DrawCircle(rcx, rcy, rr, bg);
            using (var arc = new SKPaint { Color = Pal.Danger, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f * s, StrokeCap = SKStrokeCap.Round })
                canvas.DrawArc(new SKRect(rcx - rr, rcy - rr, rcx + rr, rcy + rr), -90, 360 * left01, false, arc);
        }

        var (cancel, quit) = ConfirmButtons(r, s);
        DrawMaterialSurface(canvas, cancel, 18 * s, Pal.Card);
        DrawText(canvas, "取消", cancel.MidX - MeasureText("取消", 13 * s) / 2, cancel.MidY + 5 * s,
            13 * s, Pal.Fg, SKFontStyleWeight.SemiBold);

        using (var bg = new SKPaint { Color = Pal.Danger, IsAntialias = true })
            canvas.DrawRoundRect(quit, 18 * s, 18 * s, bg);
        DrawText(canvas, "退出", quit.MidX - MeasureText("退出", 13 * s) / 2, quit.MidY + 5 * s,
            13 * s, SKColors.White, SKFontStyleWeight.SemiBold);
    }

    /// <summary>到点提醒的两个按钮（绘制与命中测试共用）：取消本次 / 立即执行。</summary>
    internal static (SKRect cancel, SKRect exec) AlertButtons(SKRect r, float s)
    {
        float bw = 116 * s, bh = 34 * s, gap = 12 * s;
        float total = bw * 2 + gap;
        float left = r.MidX - total / 2;
        float top = r.Bottom - 50 * s;
        return (new SKRect(left, top, left + bw, top + bh),
                new SKRect(left + bw + gap, top, left + total, top + bh));
    }

    private void DrawAlert(SKCanvas canvas, SKRect r, float s)
    {
        double left = _target is null ? 0 : (_target.Value - DateTime.Now).TotalSeconds;
        left = Math.Max(0, left);
        var action = PowerActions.Get(_cfg.Action);
        // 红色氛围（对齐设计提案：顶部径向红光）
        using (var amb = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(r.MidX, r.Top - 30 * s), 230 * s,
                new[] { BackgroundAlpha(Pal.Danger, Pal.Dark ? (byte)60 : (byte)36), BackgroundAlpha(Pal.Danger, 0) },
                new float[] { 0f, 1f }, SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(r, amb);
        // 呼吸徽标：圆点透明度按秒呼吸
        string badge = $"即将{action.Label}";
        float bw = MeasureText(badge, 13 * s, SKFontStyleWeight.SemiBold);
        float badgeX = r.MidX - (bw + 16 * s) / 2f;
        double breath = 0.55 + 0.45 * Math.Sin(DateTime.UtcNow.Ticks / 5_000_000.0);
        using (var dot = new SKPaint { Color = BackgroundAlpha(Pal.Danger, (byte)(90 + 165 * breath)), IsAntialias = true })
            canvas.DrawCircle(badgeX + 4 * s, r.Top + 62 * s, 4.5f * s, dot);
        DrawText(canvas, badge, badgeX + 16 * s, r.Top + 66 * s, 13 * s, Pal.Danger, SKFontStyleWeight.SemiBold);
        // 大号等宽倒计时（最后 10 秒变红闪烁）
        string dig = left >= 3600
            ? $"{(int)left / 3600:00}:{(int)left % 3600 / 60:00}:{(int)left % 60:00}"
            : $"{(int)left / 60:00}:{(int)left % 60:00}";
        var digColor = left <= 10 && (int)left % 2 == 1 ? BackgroundAlpha(Pal.Danger, 120)
            : left <= 10 ? Pal.Danger : Pal.Fg;
        DrawText(canvas, dig, r.MidX - MeasureText(dig, 42 * s, SKFontStyleWeight.Bold) / 2,
            r.Top + 122 * s, 42 * s, digColor, SKFontStyleWeight.Bold);
        string sub = $"定时计划 · 最后 10 秒将蜂鸣提醒";
        DrawText(canvas, sub, r.MidX - MeasureText(sub, 11 * s) / 2, r.Top + 146 * s, 11 * s, Pal.Sub);

        var (cancel, exec) = AlertButtons(r, s);
        DrawMaterialSurface(canvas, cancel, 17 * s, Pal.Card);
        DrawText(canvas, "取消本次", cancel.MidX - MeasureText("取消本次", 12.5f * s) / 2, cancel.MidY + 4.5f * s,
            12.5f * s, Pal.Fg, SKFontStyleWeight.SemiBold);
        using (var bg = new SKPaint { Color = Pal.Danger, IsAntialias = true })
            canvas.DrawRoundRect(exec, 17 * s, 17 * s, bg);
        DrawText(canvas, "立即执行", exec.MidX - MeasureText("立即执行", 12.5f * s, SKFontStyleWeight.SemiBold) / 2, exec.MidY + 4.5f * s,
            12.5f * s, SKColors.White, SKFontStyleWeight.SemiBold);
    }

    private static string Fmt(long ms)
    {
        long s = Math.Max(0, ms / 1000);
        return $"{s / 60:00}:{s % 60:00}";
    }

    public void Dispose()
    {
        // 只投递 WM_CLOSE：DIB 与窗口都由岛线程自行释放，避免跨线程释放/绘制竞争
        _host.Dispose();
    }
}
