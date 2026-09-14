using System.IO;
using System.Text;
using LingYun.Config;
using LingYun.Platform;
using LingYun.Services;
using LingYun.Ui;
using SkiaSharp;

namespace LingYun.Diagnostics;

/// <summary>
/// 命令行诊断开关。存在的理由：本项目没有测试工程，且「文字乱码」这类问题
/// 光靠肉眼看截图无法定位——所以把所有判定都做成**文本 + 退出码**。
///
/// 输出：
///   灵云-diag.txt        审计/自测/布局日志（纯文本，可直接贴回）
///   灵云-diag/*.png      离屏渲染的每一帧（给人眼看观感）
///
/// 开关：--font-audit --dump-text --dump-frames --self-test --diag-all
/// </summary>
internal static class Diag
{
    private static readonly string[] Known =
    {
        "--font-audit", "--dump-text", "--dump-frames", "--self-test", "--diag-all", "--diag-no-frames",
        "--diag-monitor", "--toast-probe", "--toast-test", "--diag-quick", "--spectrum-probe", "--marquee-probe",
        "--wake-probe", "--settings-smoke",
    };

    public static bool ShouldRun(string[] args) => args.Any(a => Known.Contains(a));

    private static string BaseDir => AppContext.BaseDirectory;
    private static string LogPath => Path.Combine(BaseDir, "灵云-diag.txt");

    private static string DiagDir
    {
        get
        {
            var d = Path.Combine(BaseDir, "灵云-diag");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>返回进程退出码：全部通过 0，任一失败 1。</summary>
    public static int Run(string[] args)
    {
        bool all = args.Contains("--diag-all");
        bool font = all || args.Contains("--font-audit");
        bool text = all || args.Contains("--dump-text");
        bool frames = (all || args.Contains("--dump-frames")) && !args.Contains("--diag-no-frames");
        bool self = all || args.Contains("--self-test");
        bool mon = all || args.Contains("--diag-monitor");
        bool toastProbe = all || args.Contains("--toast-probe");
        bool toastTest = all || args.Contains("--toast-test");
        bool quick = all || args.Contains("--diag-quick");
        bool spectrumProbe = args.Contains("--spectrum-probe");
        if (toastTest) frames = true;   // 出一张带通知的帧

        var sb = new StringBuilder();
        var w = new StringWriter(sb);
        int failures = 0;

        w.WriteLine("灵云 诊断报告");
        w.WriteLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        w.WriteLine("版本: " + typeof(Diag).Assembly.GetName().Version);
        w.WriteLine("参数: " + string.Join(" ", args));
        w.WriteLine();

        if (text)
        {
            try { failures += DumpText(w); }
            catch (Exception ex) { w.WriteLine("!! --dump-text 异常: " + ex); failures++; }
        }

        if (font)
        {
            try { failures += FontAudit(w); }
            catch (Exception ex) { w.WriteLine("!! --font-audit 异常: " + ex); failures++; }
        }

        if (frames)
        {
            try { failures += DumpFrames(w); }
            catch (Exception ex) { w.WriteLine("!! --dump-frames 异常: " + ex); failures++; }
        }

        if (self)
        {
            try { failures += SelfTest(w); }
            catch (Exception ex) { w.WriteLine("!! --self-test 异常: " + ex); failures++; }
        }

        if (mon)
        {
            try { failures += DumpMonitors(w); }
            catch (Exception ex) { w.WriteLine("!! --diag-monitor 异常: " + ex); failures++; }
        }

        if (quick)
        {
            try { failures += DumpQuickPage(w); }
            catch (Exception ex) { w.WriteLine("!! --diag-quick 异常: " + ex); failures++; }
        }

        if (toastProbe)
        {
            // 必须在线程池上跑：OnStartup 阶段 Dispatcher 还没开始泵消息，
            // 在 UI 线程 await 会让续体永远排不进来（死锁）。
            try { failures += Task.Run(() => ToastProbe(w)).GetAwaiter().GetResult(); }
            catch (Exception ex) { w.WriteLine("!! --toast-probe 异常: " + ex); failures++; }
        }

        if (spectrumProbe)
        {
            try { failures += SpectrumProbe(w, args); }
            catch (Exception ex) { w.WriteLine("!! --spectrum-probe 异常: " + ex); failures++; }
        }

        if (args.Contains("--wake-probe"))
        {
            try { failures += WakeProbe(w, args); }
            catch (Exception ex) { w.WriteLine("!! --wake-probe 异常: " + ex); failures++; }
        }

        // --settings-smoke：构造/显示/关闭一次设置窗口。
        // 自测断言覆盖不到 WPF 界面，但这个窗口是全部新选项的入口——至少保证它不抛。
        if (args.Contains("--settings-smoke"))
        {
            try
            {
                var smokeCfg = new AppConfig { Theme = "system", Composite = true, Opacity = 60 };
                using var smokeMedia = new MediaSessionService();
                using var smokeIsland = new NativeIslandApp(smokeCfg, smokeMedia);
                var win = new Ui.SettingsWindow(smokeCfg, smokeIsland, () => { });
                win.Show();
                win.Close();
                w.WriteLine("========== --settings-smoke ==========");
                w.WriteLine("设置窗口：构造 / 显示 / 关闭 OK（含 system 主题、组合模式、60% 不透明度回填）");
                w.WriteLine();
            }
            catch (Exception ex)
            {
                w.WriteLine("!! --settings-smoke 异常: " + ex);
                failures++;
            }
        }

        if (args.Contains("--marquee-probe"))
        {
            try { failures += MarqueeProbe(w); }
            catch (Exception ex) { w.WriteLine("!! --marquee-probe 异常: " + ex); failures++; }
        }

        w.WriteLine();
        w.WriteLine($"===== 结论: {(failures == 0 ? "全部通过" : failures + " 项失败")} =====");

        try { File.WriteAllText(LogPath, sb.ToString(), new UTF8Encoding(true)); }
        catch { /* 落盘失败就算了 */ }
        try { Console.Write(sb.ToString()); } catch { /* GUI 子系统可能没有控制台 */ }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 真机验证 WASAPI 频谱捕获：跑 N 秒（默认 4s）采样各频段峰值。
    /// 静音时报告「未捕获到声音」但不算失败（探针可能恰好在安静环境跑）；
    /// 捕获不可用（无设备/独占）才算失败。用法：--spectrum-probe [秒数]
    /// </summary>
    private static int SpectrumProbe(TextWriter w, string[] args)
    {
        w.WriteLine("========== --spectrum-probe ==========");
        int seconds = 4;
        int at = Array.IndexOf(args, "--spectrum-probe");
        if (at >= 0 && at + 1 < args.Length
            && int.TryParse(args[at + 1], out var s) && s is >= 1 and <= 30)
            seconds = s;

        var runWeatherCfg = ConfigStore.Load();
        using var svc = new AudioSpectrumService();
        w.WriteLine($"捕获设备: {(svc.Available ? "已启动" : "不可用")}  {svc.FormatInfo}");

        var peaks = new float[5];
        int samples = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var b = svc.Bands;
            for (int i = 0; i < 5; i++)
                if (b[i] > peaks[i]) peaks[i] = b[i];
            samples++;
            Thread.Sleep(20);
        }

        w.WriteLine($"采样 {samples} 次 / {seconds}s，各频段（80/250/600/1500/3500Hz）峰值:");
        w.WriteLine("  [" + string.Join(", ", peaks.Select(p => p.ToString("0.00"))) + "]");
        if (peaks.Any(p => p > 0.05f))
            w.WriteLine("结果: 捕获到声音 ✓ 频谱正常工作");
        else
            w.WriteLine("结果: 未捕获到声音（环境安静，或播放被独占到其他设备）；放首歌后重试即可确认");

        // 顺带检查天气定位链路：手填城市 → 系统定位 → IP，实际用了哪个源
        try
        {
            var weather = new WeatherService(runWeatherCfg.Location, runWeatherCfg.Lat, runWeatherCfg.Lon);
            weather.RefreshAsync().Wait(25000);
            w.WriteLine($"天气定位: 源={weather.LocationSource}  城市={weather.LastCity}");
            weather.Stop();
        }
        catch (Exception ex)
        {
            w.WriteLine("天气定位: 检查失败 " + ex.Message);
        }

        // 顺带检查 SMTC：浏览器/播放器有没有把会话上报给系统（媒体胶囊出现的前提）
        try
        {
            using var media = new MediaSessionService();
            media.StartAsync().Wait(3000);
            Thread.Sleep(1600);   // 等一次 500ms 轮询
            var st = media.State;
            if (st.Active)
                w.WriteLine($"媒体会话: {AppIcons.FriendlyName(st.AppId)} · {st.Status} · {st.Title}"
                            + $"（系统已上报，紧凑态应显示媒体胶囊）");
            else
                w.WriteLine("媒体会话: 无 —— 播放器没有向系统上报 SMTC 会话"
                            + "（媒体胶囊不会出现；B 站网页需在播放中且未静音）");
            w.WriteLine($"会话列表: {media.Sessions.Count} 个");
        }
        catch (Exception ex)
        {
            w.WriteLine("媒体会话: 检查失败 " + ex.Message);
        }

        // 顺带只读检查音量设备路径（不修改系统音量）
        using (var vol = new AudioVolumeService())
        {
            if (vol.Available)
            {
                var lv = vol.GetVolume();
                w.WriteLine($"系统音量: {(lv is null ? "读取失败" : AudioVolumeService.FormatPercent(lv.Value))}"
                            + $"  静音: {(vol.IsMuted() == true ? "是" : "否")}");
            }
            else
            {
                w.WriteLine("系统音量: 设备不可用（无默认播放设备）——岛上的音量行会自动隐藏");
            }
        }

        if (!svc.Available)
        {
            w.WriteLine("!! WASAPI 环回不可用：检查是否存在默认播放设备，或音频被应用独占");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// 跑马灯运动验证：长标题紧凑媒体态离屏渲染两次（间隔 800ms），
    /// 标题带内白色像素重心应向左移动。带图输出到 灵云-diag/marquee-t0/t1.png。
    /// </summary>
    private static int MarqueeProbe(TextWriter w)
    {
        w.WriteLine("========== --marquee-probe ==========");
        var cfg = new AppConfig();
        using var media = new MediaSessionService();
        using var app = new NativeIslandApp(cfg, media);
        media.InjectState(new MediaState
        {
            Active = true,
            Title = "这是一个足够长的标题用来验证媒体胶囊上的跑马灯滚动效果 ABCDEFG 12345",
            AppId = "bilibili.exe",
            Status = "Playing",
        });
        app.ForceFocus("media");
        app.ForceMode("compact");
        app.InjectSpectrum(new float[] { 0.3f, 0.55f, 0.4f, 0.25f, 0.4f });

        double Centroid(SKBitmap bmp)
        {
            double sum = 0, n = 0;
            // 紧凑媒体胶囊：640 壳内 x=170..470；标题带 ≈ 220..424
            for (int y = 12; y < 40; y++)
                for (int x = 224; x < 420; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > 200 && c.Green > 200 && c.Blue > 200) { sum += x; n++; }
                }
            return n == 0 ? -1 : sum / n;
        }

        // 跑马灯相位随启动时钟变化，新实例可能恰好处于"文字还没进带"的空相位——先等到有字
        double[] centroids = new double[2];
        var dir = Path.Combine(BaseDir, "灵云-diag");
        Directory.CreateDirectory(dir);
        // 按应用的真实节奏连续渲染（~16ms/帧）：t0 = 首个非空帧的重心，t1 = 连续跑 ~800ms 后的重心
        double? first = null;
        double last = -1;
        int frames = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 900)
        {
            Thread.Sleep(16);
            using var bmp = new SKBitmap(NativeIslandApp.ShellWidth, NativeIslandApp.ShellHeight);
            using var surface = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height), bmp.GetPixels(), bmp.RowBytes);
            surface.Canvas.Clear(new SKColor(48, 48, 56));
            app.PaintScene(surface.Canvas, new SKImageInfo(bmp.Width, bmp.Height));
            surface.Flush();
            double c = Centroid(bmp);
            if (c >= 0)
            {
                first ??= c;
                last = c;
                if (frames % 20 == 0)
                {
                    using var img = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    using var fs = File.OpenWrite(Path.Combine(dir, $"marquee-t{frames / 20}.png"));
                    data.SaveTo(fs);
                }
                frames++;
            }
        }
        centroids[0] = first ?? -1;
        centroids[1] = last;
        w.WriteLine($"渲染帧数(有字): {frames}，重心 t0={centroids[0]:0.0} t1={centroids[1]:0.0}（Δ={centroids[1] - centroids[0]:0.0}px 左移）");
        w.WriteLine($"标题带白色像素重心: t0={centroids[0]:0.0}  t1={centroids[1]:0.0}（Δ={centroids[0] - centroids[1]:0.0}px 左移）");
        bool moved = centroids[0] > 0 && centroids[1] > 0 && centroids[0] - centroids[1] > 5;
        w.WriteLine("结果: " + (moved ? "跑马灯在滚动 ✓" : "未检测到滚动 ✗"));
        return moved ? 0 : 1;
    }

    // ==================================================================
    // 样本集：岛上实际会出现的全部文字
    // ==================================================================
    private static List<string> Samples()
    {
        var list = new List<string>
        {
            "22:17", "已暂停", "1时23分", "00:23", "23:59",
            "⏱", "♪", "✕", "⏮", "⏸", "▶", "⏭", "未知",
            "计划", "已启用", "已停用",
            "时间 23:00  动作 关机", "星期 [1,2,3,4,5,6,7]  日期 ", "下次 2026-09-10 23:00",
            "滚轮/点击页签见 WPF 设置窗；此处为原生预览",
            "即将关机 · 请保存工作", "还有 15 分钟关机", "取消本次执行",
            "↻ 重启 · 按住不放 66%　松手时还在按钮上才执行",
            "「重启」要按住 0.9 秒才执行", "「重启」已取消", "资源管理器", "任务管理器",
            "CPU  12.3%", "内存 61.0%  (9.8/16.0 GB)", "网络 128 KB/s",
            "北京  26°  晴", "天气不可用",
            "2026年09月  已过 10/30 天 (33%)",
            "一 二 三 四 五 六 日", "周一", "日程", "暂无日程", "日历", "自定义快捷程序", "逐小时", "添加程序",
        };
        foreach (var a in QuickActions.All) list.Add(a.Icon);
        foreach (var a in PowerActions.All.Values) list.Add(a.Icon);
        list.AddRange(new[] { "E", "C", "F", "S", "V", "云" });
        return list;
    }

    // ==================================================================
    // --dump-text ：把字符串摊成码位，分离「字体问题」与「字符串本身被编坏」
    // ==================================================================
    private static int DumpText(TextWriter w)
    {
        w.WriteLine("========== --dump-text ==========");
        w.WriteLine("# 目的：如果这里打印出的码位与源码里的汉字码位一致，说明字符串没被编坏，问题在字体。");
        w.WriteLine();

        foreach (var s in Samples())
        {
            var cps = string.Join(" ", AppFonts.CodePoints(s).Select(cp => "U+" + cp.ToString("X4")));
            w.WriteLine($"{AppFonts.DescribeRuns(s, SKFontStyleWeight.Medium)}");
            w.WriteLine($"    原文=\"{s}\"");
            w.WriteLine($"    码位={cps}");
        }

        // H3 检查：SKPaint 的 TextEncoding 与传入字符串是否一致。
        // 默认若是 Utf8，C# 的 UTF-16 字符串会被按字节误解，典型症状是"每个汉字变成 2~3 个拉丁乱码"。
        w.WriteLine();
        w.WriteLine("--- TextEncoding 一致性检查（H3）---");
        using (var p = new SKPaint { TextSize = 16 })
        {
            void Probe(string s)
            {
                var defEncoding = p.TextEncoding;
                float wDefault = p.MeasureText(s);
                p.TextEncoding = SKTextEncoding.Utf16;
                float w16 = p.MeasureText(s);
                p.TextEncoding = SKTextEncoding.Utf8;
                float w8;
                try { w8 = p.MeasureText(s); } catch { w8 = -1; }
                p.TextEncoding = defEncoding;
                w.WriteLine($"  \"{s}\" 默认={wDefault:0.###} Utf16={w16:0.###} Utf8={w8:0.###} 默认编码={defEncoding}");
            }
            Probe("A");
            Probe("计");
            Probe("计计");
            Probe("Time");
        }
        w.WriteLine();
        return 0;
    }

    // ==================================================================
    // --font-audit ：位图级豆腐块检测（定位乱码的主力）
    // ==================================================================
    private static int FontAudit(TextWriter w)
    {
        w.WriteLine("========== --font-audit ==========");
        w.WriteLine("# notdefMatch=true 且 ink>0 → 该字符被画成了 .notdef 方框（就是乱码）");
        w.WriteLine();
        AppFonts.Audit(w, Samples());
        w.WriteLine();

        // 逐行汇总（便于 grep）
        int tofu = 0;
        foreach (var sample in Samples())
            foreach (var cp in AppFonts.CodePoints(sample))
            {
                if (cp is ' ' or '\t') continue;
                var tf = AppFonts.Resolve(cp, SKFontStyleWeight.Medium);
                var bits = AppFonts.RenderGlyphBitmap(tf, cp);
                if (AppFonts.InkCount(bits) > 0 && AppFonts.IsNotdef(tf, cp)) tofu++;
            }
        w.WriteLine($"# 豆腐块字符出现次数（含重复样本）= {tofu}");
        return tofu == 0 ? 0 : 1;
    }

    // ==================================================================
    // --dump-frames ：离屏渲染每一帧，出 PNG + 文本布局日志
    // ==================================================================
    /// <summary>合成一张测试封面（PNG 字节）：底色 + 两个异色圆，供媒体页 B 模糊 / C 取色管线出图。</summary>
    private static byte[] SynthCover(float hue)
    {
        using var bmp = new SKBitmap(256, 256);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(SKColor.FromHsv(hue, 62, 52));
            using var p2 = new SKPaint { Color = SKColor.FromHsv((hue + 45) % 360, 70, 72), IsAntialias = true };
            cv.DrawCircle(182, 78, 62, p2);
            using var p3 = new SKPaint { Color = SKColor.FromHsv((hue + 300) % 360, 58, 38), IsAntialias = true };
            cv.DrawCircle(70, 192, 72, p3);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static int DumpFrames(TextWriter w)
    {
        w.WriteLine("========== --dump-frames ==========");
        w.WriteLine("# PNG 给人眼看观感；下面的布局日志给机器核对（文本/坐标/逐段字体/宽度）");
        w.WriteLine("#");
        w.WriteLine("# 读图须知：文件名带 -forced 的帧是【诊断强制状态】——它们显式调了");
        w.WriteLine("#   ForceQuickPress(...)，把「快捷」页摆成「正在长按某个危险动作」的样子，");
        w.WriteLine("#   只用于交互外观与安全保护的回归比对，**不代表用户日常看到的样子**。");
        w.WriteLine("#   默认外观：紧凑态永远只有时间/媒体/通知，没有任何功能按钮；");
        w.WriteLine("#   功能（含 6 个快捷动作）只在点击展开后的「快捷」页里。");
        w.WriteLine("#   要验真实交互行为，用 cs/tools/hover_probe.py，别用这些帧下结论。");
        w.WriteLine();

        var cfg = new AppConfig
        {
            Enabled = true,
            Weekdays = new List<int> { 1, 2, 3, 4, 5, 6, 7 },
            Hour = 23,
            Minute = 0,
            Action = "shutdown",
            AlsoPauseMedia = true,
            Tasks = new List<TaskItem>
            {
                new() { Name = "写周报", Time = "20:00" },
                new() { Name = "收拾桌面", Time = "22:30" },
            },
        };

        var media = new MediaSessionService();
        var app = new NativeIslandApp(cfg, media);

        // 第二组：用**真实配置文件**渲染一帧 —— 复现用户实际看到的样子
        // （enabled=false、weekdays 为空时，"星期 []  日期 " 这种空态就是这样来的）
        AppConfig realCfg;
        try { realCfg = ConfigStore.Load(); }
        catch { realCfg = new AppConfig(); }
        var media2 = new MediaSessionService();
        var appReal = new NativeIslandApp(realCfg, media2);

        // 五个页面各出一帧：注入假的性能/天气数据，并造几条日程，保证每页都有内容可查
        var media3 = new MediaSessionService();
        var pagesCfg = new AppConfig
        {
            Enabled = true,
            Hour = 23, Minute = 0,
            Weekdays = new List<int> { 1, 2, 3, 4, 5 },
            Dates = new List<string>(),
            QuickCustoms = new List<QuickCustomApp>
            {
                new() { Name = "网易云", Path = "cloudmusic.exe", Color = "#ec4141" },
            },
            Tasks = new List<TaskItem>
            {
                new() { Name = "写周报", Category = "Work", Time = "18:00" },
                new() { Name = "跑步 5 公里", Category = "Sport", Time = "20:30" },
                new() { Name = "读《设计心理学》", Category = "Read", Time = "22:00" },
                new() { Name = "回复邮件", Category = "Work", Time = "23:00" },
            },
        };
        var appPages = new NativeIslandApp(pagesCfg, media3);
        appPages.InjectData(
            new PerfMetrics(37.5, 62.0, 9.9, 15.9, 843.0, 0, 312.0),
            new WeatherInfo("深圳", 28.4, "多云", true, Hourly: new (int, double, double)[]
            {
                (13, 28.4, 2), (14, 30, 2), (15, 31, 3), (16, 29, 2), (17, 27, 1),
            }));

        // 托盘开关「关掉后」的对照实例：同一份注入数据，但配置里两个开关都是 false
        var offCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, Spectrum = false, Lyrics = false };
        var mediaOff = new MediaSessionService();
        var appOff = new NativeIslandApp(offCfg, mediaOff);

        // 浅色主题对照实例：同一套内容，验证配色切换后的可读性
        var lightCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, Theme = "light" };
        var mediaLight = new MediaSessionService();
        var appLight = new NativeIslandApp(lightCfg, mediaLight);

        // 岛设置大档位实例：胶囊 1.4x、展开 1.25x（钳制内的最大档）
        var bigCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, CompactScale = 1.4, ExpandedScale = 1.25 };
        bigCfg = ConfigStore.Normalize(bigCfg);
        var mediaBig = new MediaSessionService();
        var appBig = new NativeIslandApp(bigCfg, mediaBig);

        // 组合模式实例：时间 + 硬件 + 媒体同屏（自动长度）
        var compCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, Composite = true };
        var mediaComp = new MediaSessionService();
        var appComp = new NativeIslandApp(compCfg, mediaComp);
        // 组合模式（只有时钟 + 硬件；模拟无媒体会话时的形态）
        var compStaticCfg = new AppConfig
        {
            Enabled = true, Hour = 23, Minute = 0, Composite = true, CompositeMedia = false,
        };
        var mediaCompStatic = new MediaSessionService();
        var appCompStatic = new NativeIslandApp(compStaticCfg, mediaCompStatic);
        // 组合模式 + 大档缩放：验证模块槽位与文本在 1.4x 下不重叠
        var compBigCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, Composite = true, CompactScale = 1.4 };
        compBigCfg = ConfigStore.Normalize(compBigCfg);
        var mediaCompBig = new MediaSessionService();
        var appCompBig = new NativeIslandApp(compBigCfg, mediaCompBig);

        // 歌词对照实例：卡拉OK关掉时的旧样式（整句一个颜色）
        var plainCfg = new AppConfig
        {
            Enabled = true, Hour = 23, Minute = 0, Lyrics = true, LyricsKaraoke = false,
        };
        var mediaPlain = new MediaSessionService();
        var appPlain = new NativeIslandApp(plainCfg, mediaPlain);

        // 半透明背景对照实例：不透明度 50%（背景 alpha 减半，文字仍不透明）
        var transCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, Opacity = 50 };
        var mediaTrans = new MediaSessionService();
        var appTrans = new NativeIslandApp(transCfg, mediaTrans);

        // 媒体页样式 B/C 对照实例：注入同一份媒体状态 + 合成封面，验证三样式出图
        var styleBCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, MediaStyle = "b" };
        styleBCfg = ConfigStore.Normalize(styleBCfg);
        var mediaStyleB = new MediaSessionService();
        var appStyleB = new NativeIslandApp(styleBCfg, mediaStyleB);
        var styleCCfg = new AppConfig { Enabled = true, Hour = 23, Minute = 0, MediaStyle = "c" };
        styleCCfg = ConfigStore.Normalize(styleCCfg);
        var mediaStyleC = new MediaSessionService();
        var appStyleC = new NativeIslandApp(styleCCfg, mediaStyleC);

        // 「重启」的下标从动作表算出来，不要写死——动作表会被增删，写死会让长按帧悄悄错位。
        int restartAt = QuickActions.IndexOf("restart");
        if (restartAt < 0) restartAt = 0;
        w.WriteLine($"# 诊断用下标：「重启」={restartAt}");
        w.WriteLine();

        var frames = new (string Name, Action Setup)[]
        {
            ("compact-clock", () => { app.ForceMode("compact"); app.ForceFocus("timer"); }),
            // 岛设置大档位：1.4x 胶囊 + 1.25x 展开面板（配置实例），验证缩放后不裁切
            ("compact-clock-big", () =>
            {
                appBig.ForceMode("compact");
                appBig.ForceFocus("timer");
            }),
            ("expanded-plan-big", () => { appBig.ForceMode("expanded"); appBig.ForceFocus("timer"); }),
            ("page-month-big", () => { appBig.ForceMode("expanded"); appBig.ForcePage(4); }),
            // 媒体胶囊：注入播放态 + 假频谱（0.9/0.65/0.8/0.4/0.55），验证真 5 段频谱的绘制几何
            // 顺序有讲究：先注入媒体状态，ForceMode 才能按真实规则选中加宽胶囊（300 而非 240）
            ("compact-media", () =>
            {
                media.InjectState(new MediaState
                {
                    Active = true,
                    Title = "测试歌名频谱",
                    Artist = "测试歌手",
                    AppId = "chrome",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                app.ForceFocus("media");
                app.ForceMode("compact");
                app.InjectSpectrum(new float[] { 0.9f, 0.65f, 0.8f, 0.4f, 0.55f });
            }),
            // 通知：图标 + 两行 + 按文本自适应加宽。顺序有讲究——先注入通知，ForceMode 才能取到加宽目标
            ("compact-toast", () =>
            {
                app.InjectToast(new ToastData("微信", "张三", "晚上一起吃饭吗", "WeChat", 7));
                app.ForceFocus("timer");
                app.ForceMode("compact");
            }),
            // 长文本通知：宽度应到上限（620×缩放），超长文本以省略号收尾
            ("compact-toast-long", () =>
            {
                app.InjectToast(new ToastData("哔哩哔哩",
                    "你关注的 UP 主发布了新视频",
                    "「用 C# 写一个 Windows 灵动岛」已更新，时长 28 分钟，快来看看吧",
                    "bilibili.exe", 8));
                app.ForceFocus("timer");
                app.ForceMode("compact");
            }),
            // B站走 Always 策略：即使会话带缩略图也用内置站标
            ("compact-media-bilibili", () =>
            {
                app.InjectToast(null);   // 通知已抢过胶囊，这里要还原成纯媒体帧
                media.InjectState(new MediaState
                {
                    Active = true,
                    Title = "【测试】歌名",
                    Artist = "测试UP主",
                    AppId = "bilibili.exe",
                    Status = "Playing",
                    PositionMs = 12_000,
                    DurationMs = 240_000,
                });
                app.ForceFocus("media");
                app.ForceMode("compact");
                app.InjectSpectrum(new float[] { 0.35f, 0.55f, 0.75f, 0.45f, 0.25f });
            }),
            // 开关关掉后的对照帧：频谱回到示意动画（4 根）、歌词带留空
            ("compact-media-nospectrum", () =>
            {
                mediaOff.InjectState(new MediaState
                {
                    Active = true,
                    Title = "测试歌名频谱",
                    Artist = "测试歌手",
                    AppId = "chrome",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                appOff.ForceFocus("media");
                appOff.ForceMode("compact");
                appOff.InjectSpectrum(new float[] { 0.9f, 0.65f, 0.8f, 0.4f, 0.55f });
            }),
            ("expanded-media-nolyrics", () =>
            {
                mediaOff.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                appOff.ForceFocus("media");
                appOff.ForceMode("expanded");
                appOff.InjectVolume(0.4f);
                appOff.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            // 组合模式：时间 + 硬件 + 媒体同屏，宽度按内容自适应（1.5.0 的「自动长度」）。
            // 媒体文本取歌词行（歌词开启且注入），验证「歌词优先于标题」
            ("compact-composite", () =>
            {
                mediaComp.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                appComp.InjectData(new PerfMetrics(37.5, 62.0, 9.9, 15.9, 843.0, 0, 312.0));
                appComp.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
                appComp.InjectSpectrum(new float[] { 0.9f, 0.65f, 0.8f, 0.4f, 0.55f });
                appComp.ForceFocus("media");
                appComp.ForceMode("compact");
            }),
            // 组合模式只有时间 + 硬件（无媒体会话）：宽度应比三模块窄一截
            ("compact-composite-static", () =>
            {
                appCompStatic.InjectData(new PerfMetrics(12.0, 48.0, 7.6, 15.9, 12.0, 0, 3.0));
                appCompStatic.ForceFocus("timer");
                appCompStatic.ForceMode("compact");
            }),
            // 组合模式 + 1.4x 缩放：模块随之放大，槽位间距按缩放同步
            ("compact-composite-big", () =>
            {
                mediaCompBig.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                appCompBig.InjectData(new PerfMetrics(37.5, 62.0, 9.9, 15.9, 843.0, 0, 312.0));
                appCompBig.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n"));
                appCompBig.InjectSpectrum(new float[] { 0.6f, 0.4f, 0.9f, 0.5f, 0.3f });
                appCompBig.ForceFocus("media");
                appCompBig.ForceMode("compact");
            }),
            ("compact-clock-light", () => { appLight.ForceMode("compact"); appLight.ForceFocus("timer"); }),
            // 半透明背景（不透明度 50%）：背景变淡、文字仍清晰
            ("compact-clock-translucent", () =>
            {
                appTrans.ForceMode("compact");
                appTrans.ForceFocus("timer");
            }),            ("expanded-media-light", () =>
            {
                mediaLight.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    // 落在 [01:05,01:20] 句内 1/3 处：浅色主题下也能看到卡拉OK擦拭
                    PositionMs = 70_000,
                    DurationMs = 210_000,
                });
                mediaLight.InjectSessions(Array.Empty<SessionInfo>(), -1);
                appLight.ForceFocus("media");
                appLight.ForceMode("expanded");
                appLight.InjectVolume(0.65f);
                appLight.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n"
                    + "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            ("expanded-plan", () => { app.ForceMode("expanded"); app.ForceFocus("timer"); }),
            // 多会话场景：Edge 看 B 站 + 网易云放歌，展开面板右上角应出现「‹ 网易云 2/3 ›」切换器
            ("expanded-media-sources", () =>
            {
                media.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                media.InjectSessions(new[]
                {
                    new SessionInfo { AppId = "MSEdge!App", Label = "Edge", IsPlaying = true },
                    new SessionInfo { AppId = "cloudmusic.exe", Label = "网易云", IsPlaying = true },
                    new SessionInfo { AppId = "PotPlayerMini64.exe", Label = "PotPlayer", IsPlaying = false },
                }, 1);
                app.ForceFocus("media");
                app.ForceMode("expanded");
                app.InjectVolume(0.65f);
            }),
            // 静音态：喇叭变 🔇、填充归零、右侧标签显示「静音」
            ("expanded-media-muted", () =>
            {
                media.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Paused",
                    PositionMs = 65_000,
                    DurationMs = 210_000,
                });
                media.InjectSessions(Array.Empty<SessionInfo>(), -1);
                app.ForceFocus("media");
                app.ForceMode("expanded");
                app.InjectVolume(0.65f, muted: true);
                app.InjectLyrics(null);
            }),
            // 歌词带：卡拉OK逐字——当前句按进度左亮（Accent）右暗（Dim）、下一句灰。
            // 位置取 70s，落在 [01:05,01:20] 这句的 1/3 处，进度条应在句中偏左
            ("expanded-media-lyrics", () =>
            {
                media.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 70_000,
                    DurationMs = 210_000,
                });
                media.InjectSessions(Array.Empty<SessionInfo>(), -1);
                app.ForceFocus("media");
                app.ForceMode("expanded");
                app.InjectVolume(0.4f);
                app.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            // 对照帧：关掉卡拉OK → 整句一个颜色（旧样式）
            ("expanded-media-lyrics-plain", () =>
            {
                mediaPlain.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 70_000,
                    DurationMs = 210_000,
                });
                mediaPlain.InjectSessions(Array.Empty<SessionInfo>(), -1);
                appPlain.ForceFocus("media");
                appPlain.ForceMode("expanded");
                appPlain.InjectVolume(0.4f);
                appPlain.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            // 媒体页样式 B（沉浸）：封面模糊铺满 + 玻璃控制条。
            // 注入合成封面（PNG byte[] 走真解码路径），同时驱动模糊背景与歌词/音量管线
            ("expanded-media-style-b", () =>
            {
                mediaStyleB.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 70_000,
                    DurationMs = 210_000,
                    Thumb = SynthCover(280),
                });
                mediaStyleB.InjectSessions(Array.Empty<SessionInfo>(), -1);
                appStyleB.ForceFocus("media");
                appStyleB.ForceMode("expanded");
                appStyleB.InjectVolume(0.4f);
                appStyleB.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            // 媒体页样式 C（氛围海报）：封面取色双光斑 + 大标题 + 微倾封面
            ("expanded-media-style-c", () =>
            {
                mediaStyleC.InjectState(new MediaState
                {
                    Active = true,
                    Title = "夜曲",
                    Artist = "周杰伦",
                    AppId = "cloudmusic.exe",
                    Status = "Playing",
                    PositionMs = 70_000,
                    DurationMs = 210_000,
                    Thumb = SynthCover(120),
                });
                mediaStyleC.InjectSessions(Array.Empty<SessionInfo>(), -1);
                appStyleC.ForceFocus("media");
                appStyleC.ForceMode("expanded");
                appStyleC.InjectVolume(0.4f);
                appStyleC.InjectLyrics(LyricsService.ParseLrc(
                    "[00:00.00]词：黄俊郎\n[00:10.00]一群嗜血的蚂蚁 被腐肉所吸引\n" +
                    "[01:05.00]我面无表情 看孤独的风景\n[01:20.00]失去你 爱恨开始分明\n"));
            }),
            ("confirm-exit", () => { app.ForceExitConfirm(); }),
            ("alert", () =>
            {
                app.ForceMode("alert");
                app.ForceFocus("timer");
                app.SetTarget(DateTime.Now.AddMinutes(9.5));
            }),
            ("expanded-plan-realcfg", () =>
            {
                appReal.ForceMode("expanded");
                appReal.ForceFocus("timer");
            }),
            ("page-plan", () => { appPages.ForceMode("expanded"); appPages.ForcePage(0); }),
            ("page-perf", () => { appPages.ForceMode("expanded"); appPages.ForcePage(1); }),
            ("page-weather", () => { appPages.ForceMode("expanded"); appPages.ForcePage(2); }),
            ("page-tasks", () => { appPages.ForceMode("expanded"); appPages.ForcePage(3); }),
            ("page-month", () => { appPages.ForceMode("expanded"); appPages.ForcePage(4); }),
            // 展开「快捷」页：6 个动作按钮 3 列网格，危险项带红环
            ("page-quick", () => { appPages.ForceMode("expanded"); appPages.ForcePage(5); }),
            // 回归现场：正按在「重启」上、进度 66%。用户早先就是被"随手一点就关机"打到的，
            // 现在危险动作只在展开页里、且必须长按才执行。
            ("page-quick-hold-forced", () =>
            {
                appPages.ForceMode("expanded");
                appPages.ForcePage(5);
                appPages.ForceQuickPress(restartAt, QuickActions.HoldMs * 0.66);   // 正按「重启」，进度 66%
            }),
        };
        var renderers = new Dictionary<string, NativeIslandApp>
        {
            ["confirm-exit"] = app, ["compact-clock"] = app, ["compact-media"] = app, ["compact-media-bilibili"] = app,
            ["compact-toast"] = app, ["compact-toast-long"] = app,
            ["expanded-plan"] = app, ["expanded-media-sources"] = app,
            ["expanded-media-muted"] = app, ["expanded-media-lyrics"] = app, ["alert"] = app,
            ["compact-clock-light"] = appLight, ["expanded-media-light"] = appLight,
            ["compact-clock-big"] = appBig, ["expanded-plan-big"] = appBig, ["page-month-big"] = appBig,
            ["compact-media-nospectrum"] = appOff, ["expanded-media-nolyrics"] = appOff,
            ["compact-composite"] = appComp, ["compact-composite-static"] = appCompStatic,
            ["compact-composite-big"] = appCompBig,
            ["expanded-media-lyrics-plain"] = appPlain,
            ["expanded-media-style-b"] = appStyleB, ["expanded-media-style-c"] = appStyleC,
            ["compact-clock-translucent"] = appTrans,
            ["expanded-plan-realcfg"] = appReal,
            ["page-plan"] = appPages, ["page-perf"] = appPages, ["page-weather"] = appPages,
            ["page-tasks"] = appPages, ["page-month"] = appPages,
            ["page-quick"] = appPages, ["page-quick-hold-forced"] = appPages,
        };

        var traceLines = new List<string>();
        AppFonts.Trace = (text, x, y, size, runs) =>
            traceLines.Add($"{text}\t{Blank(x)}\t{Blank(y)}\t{Blank(size)}\t{runs}");
        try
        {
            foreach (var (name, setup) in frames)
            {
                setup();
                // alert 的倒计时由 _target 驱动，这里再给一次（上面 setup 里给过一次）
                if (name == "alert") app.SetTarget(DateTime.Now.AddMinutes(9.5));

                var target = renderers[name];
                traceLines.Clear();
                var info = new SKImageInfo(
                    NativeIslandApp.ShellWidth, NativeIslandApp.ShellHeight,
                    SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;
                // 透明底在多数看图器里是白/棋盘，先把底刷成中灰，深色胶囊才看得清
                canvas.Clear(new SKColor(0x30, 0x30, 0x38));
                target.PaintScene(canvas, info);
                canvas.Flush();

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                var path = Path.Combine(DiagDir, $"frame-{name}.png");
                File.WriteAllBytes(path, data.ToArray());

                w.WriteLine($"--- {name} ---");
                w.WriteLine($"文件: {path}");
                w.WriteLine("mode\ttext\tx\ty\tsize\truns");
                foreach (var line in traceLines) w.WriteLine("  " + line);
                w.WriteLine();
            }
        }
        finally
        {
            AppFonts.Trace = null;
            app.Dispose();
            media.Dispose();
            appReal.Dispose();
            media2.Dispose();
            appPages.Dispose();
            media3.Dispose();
            appOff.Dispose();
            mediaOff.Dispose();
            appLight.Dispose();
            mediaLight.Dispose();
            appBig.Dispose();
            mediaBig.Dispose();
            appComp.Dispose();
            mediaComp.Dispose();
            appCompStatic.Dispose();
            mediaCompStatic.Dispose();
            appCompBig.Dispose();
            mediaCompBig.Dispose();
            appPlain.Dispose();
            mediaPlain.Dispose();
            appTrans.Dispose();
            mediaTrans.Dispose();
        }

        return 0;
    }

    private static string Blank(float v) => v.ToString("0.##");

    // ==================================================================
    // --diag-monitor ：多显示器枚举与岛的落位
    // ==================================================================
    private static int DumpMonitors(TextWriter w)
    {
        w.WriteLine("========== --diag-monitor ==========");
        int fail = 0;
        var list = Displays.All();
        w.WriteLine($"显示器数量 = {list.Count}");
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            w.WriteLine($"  [{i}]{(m.Primary ? " *主屏" : "      ")} " +
                        $"bounds=({m.Bounds.Left},{m.Bounds.Top})-({m.Bounds.Right},{m.Bounds.Bottom}) " +
                        $"work=({m.Work.Left},{m.Work.Top})-({m.Work.Right},{m.Work.Bottom})");
            if (m.Work.Right <= m.Work.Left || m.Work.Bottom <= m.Work.Top) { w.WriteLine("      !! 工作区无效"); fail++; }
        }
        int primary = list.FindIndex(m => m.Primary);
        if (list.Count > 0 && primary < 0) { w.WriteLine("!! 没有主显示器"); fail++; }

        // 逐个索引算落位，确认不会跑出屏幕（含用户配置的水平/垂直偏移）
        var runCfg = ConfigStore.Load();
        w.WriteLine($"落位检查（岛壳 640x400，offsetX={runCfg.OffsetX} offsetY={runCfg.OffsetY}，" +
                    $"胶囊缩放 {runCfg.CompactScale:0.##}x / 展开缩放 {runCfg.ExpandedScale:0.##}x）:");
        for (int i = 0; i < list.Count; i++)
        {
            var wa = Displays.WorkAreaOf(i, list);
            var (x, y) = Ui.NativeIslandApp.ShellPositionFor(wa.Left, wa.Right, wa.Top, runCfg.OffsetX, runCfg.OffsetY);
            bool inside = x >= wa.Left && x + NativeIslandApp.ShellWidth <= wa.Right && y >= wa.Top;
            w.WriteLine($"  monitor_index={i} -> shellX={x} shellY={y} 岛中心x={x + NativeIslandApp.ShellWidth / 2} " +
                        $"期望中心={(wa.Left + wa.Right) / 2} {(inside ? "OK" : "!! 落位异常")}");
            if (!inside) fail++;
        }
        // 越界索引应安全退回主屏
        var fallback = Displays.WorkAreaOf(99, list);
        var expect = Displays.WorkAreaOf(primary < 0 ? 0 : primary, list);
        bool ok = fallback.Left == expect.Left && fallback.Right == expect.Right;
        w.WriteLine($"越界索引 99 退回主屏 = {(ok ? "OK" : "!! 未退回")}");
        if (!ok) fail++;
        w.WriteLine();
        return fail;
    }

    // ==================================================================
    // --diag-quick ：快捷页安全契约
    // ==================================================================
    private static int DumpQuickPage(TextWriter w)
    {
        w.WriteLine("========== --diag-quick ==========");
        w.WriteLine("# 契约：展开后的「快捷」页里，危险动作（关机/重启/睡眠）只能长按执行；安全动作单击即执行。");
        w.WriteLine("# 背景：这组动作曾以「悬停快捷球」形式出现在紧凑态，随手一点就 shutdown /r /t 0。");
        w.WriteLine("# 现在它们只在点击展开后的「快捷」页里，按钮网格 3 列，紧凑态不放任何功能按钮。");
        w.WriteLine();

        var all = QuickActions.All;
        w.WriteLine($"内置动作总数 = {all.Count}（自定义程序在 quick_custom，不进这张表）　长按确认 = {QuickActions.HoldMs}ms");
        w.WriteLine($"页签顺序 = {string.Join(" / ", NativeIslandApp.PageNames)}　（快捷页下标 = {NativeIslandApp.QuickPageIndex}）");
        for (int i = 0; i < all.Count; i++)
        {
            var a = all[i];
            string col = i < 2 ? "安全行" : "危险行";
            w.WriteLine($"  [{i}] {col} {a.Icon} {a.Key,-9} {a.Label,-6} " +
                        (a.Destructive ? "危险 · 只能长按" : "安全 · 单击即执行"));
        }
        w.WriteLine();

        int fail = 0;
        void Bad(string msg) { w.WriteLine("!! " + msg); fail++; }

        // 安全行（展开第一眼看到的格子）不该是危险动作
        if (!QuickActions.All.Take(2).All(a => !a.Destructive))
            Bad("快捷页安全行含危险动作——展开第一眼就看到关机/重启/睡眠");
        if (QuickActions.DestructiveCount == 0)
            Bad("没有任何动作被标记为危险（关机/重启/睡眠必须标记）");
        foreach (var k in new[] { "shutdown", "restart", "sleep" })
            if (!all.Any(a => a.Key == k && a.Destructive))
                Bad($"{k} 未标记为危险动作");

        var dup = all.GroupBy(a => a.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) Bad("key 重复: " + string.Join(",", dup));

        foreach (var d in all.Where(a => a.Destructive))
        {
            if (QuickActions.ShouldRunOnClick(d)) Bad($"危险动作 {d.Key} 仍可单击执行");
            if (!QuickActions.ShouldRunOnHold(d, QuickActions.HoldMs, true))
                Bad($"危险动作 {d.Key} 长按到期也执行不了");
            if (QuickActions.ShouldRunOnHold(d, QuickActions.HoldMs - 1, true))
                Bad($"危险动作 {d.Key} 按不够时长也能执行");
            if (QuickActions.ShouldRunOnHold(d, QuickActions.HoldMs * 10, false))
                Bad($"危险动作 {d.Key} 长按中把指针移开仍然执行");
        }
        foreach (var s in all.Where(a => !a.Destructive))
            if (!QuickActions.ShouldRunOnClick(s)) Bad($"安全动作 {s.Key} 单击不执行");

        w.WriteLine();
        w.WriteLine("⚠ 下面这些帧是**强制状态**，用来比对交互外观，不代表默认外观。");
        w.WriteLine("   默认外观请以 frame-compact-clock.png 为准（紧凑态无任何按钮）。");
        w.WriteLine("观感核对（--dump-frames 出的帧）:");
        w.WriteLine("  frame-page-quick.png            展开「快捷」页，6 个按钮 3 列网格，危险项带红环");
        w.WriteLine("  frame-page-quick-hold-forced.png 正按「重启」，按钮上有红色进度弧、页底有百分比提示");
        w.WriteLine();
        w.WriteLine("运行时真机验证（离屏帧证明不了默认行为）:");
        w.WriteLine("  cs/tools/hover_probe.py --label off   # 悬停紧凑态应 0 差异点（无球）");
        w.WriteLine("  cs/tools/hover_probe.py --label on    # quick_fan 已移除，此探针仅用于确认无球");
        w.WriteLine();
        return fail;
    }

    // ==================================================================
    // --toast-probe ：系统通知权限探测
    // ==================================================================
    private static async Task<int> ToastProbe(TextWriter w)
    {
        w.WriteLine("========== --toast-probe ==========");
        using var svc = new ToastService();
        await svc.StartAsync();
        var cfg = ConfigStore.Load();
        w.WriteLine($"配置开关(消息通知) = {cfg.Toast}");
        w.WriteLine($"可用 = {svc.Available}");
        w.WriteLine($"状态 = {svc.Status}");
        w.WriteLine($"高水位 Id = {svc.LastSeenId}（启动时已有通知的最大 Id，比它新才会弹）");
        var cur = svc.Current;
        w.WriteLine(cur is null
            ? "当前通知 = 无（此刻操作中心里没有比高水位更新的通知）"
            : $"当前通知 = [{(cur.Aumid.Length > 0 ? cur.Aumid : "无 AUMID")}] {cur.App} / {cur.Title} / {cur.Body}（Id {cur.Id}）");
        w.WriteLine(svc.Available
            ? "已获得通知访问权限：通知到达时接管胶囊约 6 秒，点击唤醒对应应用。"
            : "未授权：需在「设置 → 系统 → 通知」里允许应用访问通知。不影响其他功能。");
        w.WriteLine();
        // 没授权不算失败——这只是可选的增强功能
        return 0;
    }

    // ==================================================================
    // --wake-probe <AUMID> ：验证通知点击的「唤醒应用」三级链（真机用）
    // ==================================================================
    private static int WakeProbe(TextWriter w, string[] args)
    {
        w.WriteLine("========== --wake-probe ==========");
        int i = Array.IndexOf(args, "--wake-probe");
        string aumid = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        if (aumid.Length == 0)
        {
            w.WriteLine("用法：--wake-probe <AUMID>，例如 --wake-probe MSEdge!App");
            w.WriteLine();
            return 0;
        }
        // 必须在线程池上跑：Diag.Run 在 WPF UI 线程且消息泵未开，
        // 直接 await 会把续体排进 Dispatcher 队列再也回不来（死锁）
        var (ok, how) = Task.Run(() => AppActivatorService.ActivateAsync(aumid))
            .GetAwaiter().GetResult();
        w.WriteLine($"AUMID = {aumid}");
        w.WriteLine($"结果 = {(ok ? "成功" : "失败")}");
        w.WriteLine($"路径 = {how}");
        w.WriteLine();
        // 激活失败不算诊断失败：本机没装这个应用也会失败，这是环境问题不是代码回归
        return 0;
    }

    // ==================================================================
    // --self-test ：零额外工程的回归断言（退出码即判据）
    // ==================================================================
    private static int SelfTest(TextWriter w)
    {
        w.WriteLine("========== --self-test ==========");
        int failed = 0, passed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; w.WriteLine($"PASS  {name}"); }
            else { failed++; w.WriteLine($"FAIL  {name}  {detail}"); }
        }

        // 字体：Measure 必须等于逐段宽度之和（否则居中会算歪）
        foreach (var s in new[] { "计划", "22:17", "时间 23:00  动作 关机", "歌名 🎵", "混合 abc 中文 🔒 123 😀 结束" })
        {
            float whole = AppFonts.Measure(s, 16, SKFontStyleWeight.SemiBold);
            float sum = 0;
            foreach (var (run, tf) in AppFonts.Runs(s, SKFontStyleWeight.SemiBold))
            {
                using var p = new SKPaint { TextSize = 16, Typeface = tf };
                sum += p.MeasureText(run);
            }
            Check($"Measure == 逐段之和  \"{s}\"", Math.Abs(whole - sum) < 0.01f, $"whole={whole:0.###} sum={sum:0.###}");
        }

        // 字体：每个分段自己的字体必须覆盖自己那段（不允许出现豆腐块）
        foreach (var s in Samples())
        {
            bool ok = true;
            string bad = "";
            foreach (var (run, tf) in AppFonts.Runs(s, SKFontStyleWeight.Medium))
            {
                foreach (var cp in AppFonts.CodePoints(run))
                {
                    if (cp is ' ' or '\t') continue;
                    if (tf.GetGlyph(cp) == 0) { ok = false; bad = $"{run} U+{cp:X4} via {tf.FamilyName}"; break; }
                }
                if (!ok) break;
            }
            if (!ok) Check($"分段字体覆盖  \"{s}\"", false, bad);
        }
        Check("分段字体覆盖（全部样本）", true);

        // 调度：关闭时不产生目标
        var off = new AppConfig { Enabled = false, Weekdays = new List<int> { 1 } };
        Check("Scheduler 关闭→NextTarget=null", Scheduler.NextTarget(off, DateTime.Now) is null);

        // 调度：每天 23:00，应返回今天或明天的 23:00
        var on = new AppConfig
        {
            Enabled = true,
            Weekdays = new List<int> { 1, 2, 3, 4, 5, 6, 7 },
            Hour = 23,
            Minute = 0,
        };
        var t = Scheduler.NextTarget(on, new DateTime(2026, 9, 10, 12, 0, 0));
        Check("Scheduler 每天23:00 → 当天23:00",
            t is not null && t.Value == new DateTime(2026, 9, 10, 23, 0, 0), $"实际={t}");

        var t2 = Scheduler.NextTarget(on, new DateTime(2026, 9, 10, 23, 30, 0));
        Check("Scheduler 已过点 → 次日23:00",
            t2 is not null && t2.Value == new DateTime(2026, 9, 11, 23, 0, 0), $"实际={t2}");

        // 配置归一化：越界值必须被钳制
        var messy = new AppConfig { Hour = 99, Minute = -5, Theme = "neon", Action = "explode" };
        var norm = ConfigStore.Normalize(messy);
        Check("Normalize 钳制 hour/minute/theme/action",
            norm.Hour == 23 && norm.Minute == 0 && norm.Theme == "dark" && norm.Action == "shutdown",
            $"hour={norm.Hour} minute={norm.Minute} theme={norm.Theme} action={norm.Action}");

        // 性能数据：网速默认展示；上下行速率和 CPU 计算都要能处理回退/无效采样。
        Check("性能页网速：默认开启且可关闭",
            new AppConfig().PerfNetwork
            && !ConfigStore.Normalize(new AppConfig { PerfNetwork = false }).PerfNetwork);
        Check("性能采样：CPU 空闲/满载计算正确",
            Math.Abs(PerfSampler.ComputeCpuPercent(100, 100, 0)) < 0.001
            && Math.Abs(PerfSampler.ComputeCpuPercent(0, 100, 100) - 100) < 0.001
            && Math.Abs(PerfSampler.ComputeCpuPercent(50, 100, 100) - 75) < 0.001);
        Check("性能采样：CPU/网络异常值不产生负数",
            PerfSampler.ComputeCpuPercent(-1, 100, 100) == 0
            && PerfSampler.ComputeNetworkRate(1000, 900, 1) == 0
            && PerfSampler.ComputeNetworkRate(1000, 2000, 0) == 0
            && PerfSampler.ComputeNetworkRate(1000, 2024, 1) > 0);
        Check("性能页速率格式：KB/MB 边界和非法值",
            NativeIslandApp.FmtRate(0) == "0 KB/s"
            && NativeIslandApp.FmtRate(1024) == "1.0 MB/s"
            && NativeIslandApp.FmtRate(-1) == "0 KB/s"
            && NativeIslandApp.FmtRate(double.NaN) == "0 KB/s");
        var perfBody = new SkiaSharp.SKRect(0, 0, 424, 229);
        var perfOn = NativeIslandApp.PerfLayout(perfBody, 1, true);
        var perfOff = NativeIslandApp.PerfLayout(perfBody, 1, false);
        bool perfOnBounds = perfOn.Cpu.Left >= perfBody.Left && perfOn.Cpu.Top >= perfBody.Top
            && perfOn.Memory.Right <= perfBody.Right && perfOn.Memory.Bottom <= perfBody.Bottom
            && perfOn.Minis.All(r => r.Left >= perfBody.Left && r.Top >= perfBody.Top
                && r.Right <= perfBody.Right && r.Bottom <= perfBody.Bottom);
        bool perfNoOverlap = perfOn.Minis.Zip(perfOn.Minis.Skip(1), (a, b) => a.Right <= b.Left).All(x => x)
            && perfOff.Minis.Zip(perfOff.Minis.Skip(1), (a, b) => a.Right <= b.Left).All(x => x);
        Check("性能页布局：网速开关下卡片不越界不重叠",
            perfOn.Minis.Length == 3 && perfOff.Minis.Length == 2
            && Math.Abs(perfOn.Minis[0].Left - 0) < 0.01
            && Math.Abs(perfOn.Minis[1].Left - 145) < 0.01
            && Math.Abs(perfOn.Minis[2].Left - 289) < 0.01
            && Math.Abs(perfOff.Minis[0].Left - 0) < 0.01
            && Math.Abs(perfOff.Minis[1].Left - 145) < 0.01
            && perfOnBounds && perfNoOverlap);

        var days = new AppConfig { Weekdays = new List<int> { 0, 8, 3, 3, 5 } };
        var nd = ConfigStore.Normalize(days);
        Check("Normalize 星期去重+过滤越界",
            nd.Weekdays.SequenceEqual(new[] { 3, 5 }), $"实际=[{string.Join(",", nd.Weekdays)}]");

        // 日历：每月 1 号的首列必须落在 0..6，且行号不越界（S1 会用到）
        bool calOk = true;
        string calBad = "";
        for (int m = 1; m <= 12; m++)
        {
            var first = new DateTime(2026, m, 1);
            int startCol = ((int)first.DayOfWeek + 6) % 7;   // 周一=0
            int n = DateTime.DaysInMonth(2026, m);
            int lastRow = (startCol + n - 1) / 7;
            if (startCol is < 0 or > 6 || lastRow > 5) { calOk = false; calBad = $"{m}月 startCol={startCol} lastRow={lastRow}"; }
        }
        Check("日历首列/行号 2026 全年不越界", calOk, calBad);

        // 快捷页安全契约：用户就是被「悬停出现东西 → 随手一点 → 电脑重启」打到的
        var danger = new QuickAction("probe-danger", "试", "×", () => { }, Destructive: true);
        var benign = new QuickAction("probe-safe", "试", "√", () => { });
        Check("危险动作单击不执行", !QuickActions.ShouldRunOnClick(danger));
        Check("安全动作单击即执行", QuickActions.ShouldRunOnClick(benign));
        Check("危险动作按不够时长不执行",
            !QuickActions.ShouldRunOnHold(danger, QuickActions.HoldMs - 1, true));
        Check("危险动作按够时长才执行",
            QuickActions.ShouldRunOnHold(danger, QuickActions.HoldMs, true));
        Check("危险动作长按中移开不执行",
            !QuickActions.ShouldRunOnHold(danger, QuickActions.HoldMs + 500, false));
        Check("关机/重启/睡眠均已标记为危险",
            new[] { "shutdown", "restart", "sleep" }
                .All(k => QuickActions.All.Any(a => a.Key == k && a.Destructive)));
        Check("快捷球 key 无重复",
            QuickActions.All.Select(a => a.Key).Distinct().Count() == QuickActions.All.Count);
        // 设计契约：紧凑态只显示（时间/媒体/通知），不放任何功能按钮；
        // 功能入口在展开后的「快捷」页。这两条靠"移除 quick_fan 后仍能编译"本身约束——
        // 若有人把快捷球塞回紧凑态，下面这些结构断言会先亮红灯。
        Check("快捷页存在且下标为 5", NativeIslandApp.PageNames.Contains("快捷") && NativeIslandApp.QuickPageIndex == 5);
        Check("快捷页网格 3 列、危险动作独占最后一行",
            NativeIslandApp.QuickGridLayout(new SkiaSharp.SKRect(0, 0, 412, 200), 1f, 0) is var qg
            && qg.Cells.Length == 6 && qg.DangerStart == 3 && qg.Cells[3].Top > qg.Cells[2].Bottom);
        Check("快捷页顶行（第一眼）不含危险动作",
            QuickActions.All.Take(2).All(a => !a.Destructive)
            && QuickActions.All.Skip(QuickActions.FirstDestructiveIndex).All(a => a.Destructive),
            "顶行=" + string.Join("/", QuickActions.All.Take(2).Select(a => a.Key)));
        // 用户点名移除的三个（原本就是扇出第一屏）。钉住这个决定，别日后被顺手加回来
        var removed = new[] { "explorer", "settings", "taskmgr" };
        Check("已移除 资源管理器/设置/任务管理器",
            removed.All(k => QuickActions.IndexOf(k) < 0),
            "仍在表里=" + string.Join("/", removed.Where(k => QuickActions.IndexOf(k) >= 0)));
        Check("IndexOf 未命中返回 -1", QuickActions.IndexOf("__no_such_key__") == -1);
        Check("FirstDestructiveIndex 指向的确实是危险动作",
            QuickActions.FirstDestructiveIndex < 0
            || QuickActions.All[QuickActions.FirstDestructiveIndex].Destructive);

        // ---- 频谱 DSP（复刻自 NotchPeninsula AudioAnalyzer，Apache-2.0）----
        // 喂合成 80Hz 正弦（对应 band0 底鼓），跑若干块后：band0 应显著高于 band2/3/4，且所有值在 0..1
        {
            float rate = 8000f;
            var state = new (float coeff, float q1, float q2, float weight)[5];
            for (int i = 0; i < 5; i++)
            {
                float freq = Math.Min(AudioSpectrumService.TargetFreqs[i], rate / 2.2f);
                float k = MathF.Round(freq * 256f / rate);
                state[i] = (2f * MathF.Cos(2f * MathF.PI * k / 256f), 0, 0, AudioSpectrumService.Weights[i]);
            }
            var bars = new float[5];
            float peak = 0.1f;
            var rnd = new Random(7);
            for (int blk = 0; blk < 40; blk++)
            {
                for (int n = 0; n < AudioSpectrumService.BlockSize; n++)
                {
                    // 少量白噪让高频段不至于恒 0（纯音下它们本来就接近 0）
                    float sample = 0.25f * MathF.Sin(2f * MathF.PI * 80f * n / rate)
                                   + 0.01f * (float)(rnd.NextDouble() * 2 - 1);
                    for (int j = 0; j < 5; j++)
                    {
                        ref var s = ref state[j];
                        float q0 = sample + s.coeff * s.q1 - s.q2;
                        s.q2 = s.q1;
                        s.q1 = q0;
                    }
                }
                AudioSpectrumService.FinishBlock(state, bars, ref peak);
            }
            Check("频谱 80Hz 正弦 → band0 主导",
                bars[0] > bars[2] && bars[0] > bars[3] && bars[0] > bars[4],
                $"bars=[{string.Join(",", bars.Select(b => b.ToString("0.00")))}]");
            Check("频谱输出全部在 0..1",
                bars.All(b => b is >= 0f and <= 1f),
                $"bars=[{string.Join(",", bars.Select(b => b.ToString("0.00")))}]");
            Check("频谱频段/权重表为 5 项",
                AudioSpectrumService.TargetFreqs.Length == 5 && AudioSpectrumService.Weights.Length == 5);
        }
        // 静音（全 0 输入）时 AGC 不得除零，输出仍应合法
        {
            var state = new (float coeff, float q1, float q2, float weight)[5];
            for (int i = 0; i < 5; i++)
                state[i] = (2f * MathF.Cos(2f * MathF.PI * MathF.Round(AudioSpectrumService.TargetFreqs[i] * 256f / 8000f) / 256f), 0, 0, AudioSpectrumService.Weights[i]);
            var bars = new float[5];
            float peak = 0.0f;
            AudioSpectrumService.FinishBlock(state, bars, ref peak);
            Check("频谱静音不炸（AGC 底噪保护）",
                bars.All(b => float.IsFinite(b) && b is >= 0f and <= 1f));
        }

        // ---- SMTC 多会话选源（思路来自 NotchPeninsula MediaController，Apache-2.0）----
        // 现场：用户在用 Edge 看 B 站，同时网易云在放歌 —— 系统"当前会话"会漂移，
        // 手动选定的来源必须钉住，直到该会话消失才回落。
        {
            var ss = new (string AppId, bool IsPlaying)[]
            {
                ("MSEdge!App", true),
                ("cloudmusic.exe", false),
                ("PotPlayerMini64.exe", true),
            };
            Check("选源：锁定项优先于系统当前",
                MediaSessionService.PickSession(ss, "cloudmusic.exe", systemCurrentIndex: 0) == 1);
            Check("选源：锁定项消失 → 回落系统当前",
                MediaSessionService.PickSession(ss, "gone.exe", systemCurrentIndex: 2) == 2);
            Check("选源：无系统当前 → 取第一个正在播放的",
                MediaSessionService.PickSession(ss, "", systemCurrentIndex: -1) == 0);
            Check("选源：全暂停 → 取第一个（不返回 -1）",
                MediaSessionService.PickSession(
                    new (string, bool)[] { ("a", false), ("b", false) }, "", -1) == 0);
            Check("选源：无会话 → -1",
                MediaSessionService.PickSession(Array.Empty<(string, bool)>(), "x", 0) == -1);
        }
        {
            string clean1 = MediaSessionService.CleanTitle("【测试】歌名_哔哩哔哩_bilibili", out _);
            Check("标题清理：去 B 站后缀", clean1 == "【测试】歌名", clean1);
            string clean2 = MediaSessionService.CleanTitle("正在播放：夜曲 - 周杰伦", out var cArtist2);
            Check("标题清理：拆「正在播放：A - B」", clean2 == "夜曲" && cArtist2 == "周杰伦", $"{clean2}/{cArtist2}");
            string clean3 = MediaSessionService.CleanTitle("正在播放：纯音乐", out var cArtist3);
            Check("标题清理：无艺人时不留悬空分隔符", clean3 == "纯音乐" && cArtist3 == "", $"{clean3}/{cArtist3}");
            string clean4 = MediaSessionService.CleanTitle("普通歌名", out _);
            Check("标题清理：普通标题原样保留", clean4 == "普通歌名", clean4);
            // 补齐上游的尾巴表：优酷各频道 + 短剧大全（此前只覆盖 电视剧/电影/综艺/动漫/纪录片/音乐）
            string clean5 = MediaSessionService.CleanTitle(
                "庆余年-电视剧-高清完整正版视频在线观看-优酷", out _);
            string clean6 = MediaSessionService.CleanTitle("斗罗大陆-游戏-高清完整正版视频在线观看-优酷", out _);
            string clean7 = MediaSessionService.CleanTitle("某短剧-最新热门短剧大全-免费短剧在线观看", out _);
            Check("标题清理：优酷频道尾巴（电视剧/游戏）全覆盖",
                clean5 == "庆余年" && clean6 == "斗罗大陆", $"{clean5}/{clean6}");
            Check("标题清理：短剧大全尾巴", clean7 == "某短剧", clean7);
        }
        Check("友好名：Edge/网易云可读，未知回退短名",
            AppIcons.FriendlyName("MSEdge!App") == "Edge"
            && AppIcons.FriendlyName("cloudmusic.exe") == "网易云"
            && AppIcons.FriendlyName("SomeUnknownPlayer!App") == "SomeUnknownPlayer",
            AppIcons.FriendlyName("SomeUnknownPlayer!App"));
        // 图标映射（阶段 3）：新增站标必须真的能被品牌键命中、素材能解码、Always 策略生效
        Check("图标映射：B站/QQ音乐/PotPlayer 可命中品牌键",
            AppIcons.FriendlyName("bilibili.exe") == "哔哩哔哩"
            && AppIcons.FriendlyName("QQMusic.exe") == "QQ音乐"
            && AppIcons.FriendlyName("PotPlayerMini64.exe") == "PotPlayer");
        Check("图标素材：bilibili/potplayer 可解码",
            AppIcons.TryLoad("bilibili", 64) is not null && AppIcons.TryLoad("potplayer", 64) is not null);
        Check("图标策略：B站/PotPlayer 优先内置，Edge 不抢系统封面",
            AppIcons.PreferBundled("bilibili.exe") && AppIcons.PreferBundled("PotPlayerMini64.exe")
            && !AppIcons.PreferBundled("MSEdge!App"));
        // 音量（阶段 4）：滚轮步进钳位 + 百分比文案
        {
            Check("音量步进：向上 +2% 且钳在 1.0",
                Math.Abs(AudioVolumeService.Step(0.5f, 1) - 0.52f) < 1e-6
                && Math.Abs(AudioVolumeService.Step(0.99f, 1) - 1.0f) < 1e-6);
            Check("音量步进：向下 -2% 且钳在 0",
                Math.Abs(AudioVolumeService.Step(0.5f, -1) - 0.48f) < 1e-6
                && AudioVolumeService.Step(0.01f, -1) == 0f);
            Check("音量文案：四舍五入到整数百分比",
                AudioVolumeService.FormatPercent(0.654f) == "65%"
                && AudioVolumeService.FormatPercent(1f) == "100%"
                && AudioVolumeService.FormatPercent(-1f) == "0%");
        }
        // 歌词（阶段 5）：LRC 解析与「当前句」取值是纯逻辑，边界必须钉死
        {
            var lines = LyricsService.ParseLrc(
                "[00:00.00]第一句\n[00:10.50]第二句\n[01:05.300]第三句\n[ar:周杰伦]\n\n[01:20.00]");
            Check("歌词解析：时间戳/跳过元信息/丢空行",
                lines.Length == 3
                && lines[0].Text == "第一句"
                && Math.Abs(lines[1].Time.TotalSeconds - 10.5) < 1e-6
                && Math.Abs(lines[2].Time.TotalSeconds - 65.3) < 1e-6,
                $"解析出 {lines.Length} 行");
            var multi = LyricsService.ParseLrc("[00:12.00][01:12.00]重复句");
            Check("歌词解析：一行多时间标签展开成两行",
                multi.Length == 2 && multi[1].Time.TotalSeconds == 72);
            Check("歌词取句：早于第一句 → -1", LyricsService.LineIndexAt(lines, TimeSpan.Zero.Add(TimeSpan.FromSeconds(-1))) == -1);
            Check("歌词取句：正好卡在行首取该行",
                LyricsService.LineIndexAt(lines, TimeSpan.FromSeconds(10.5)) == 1);
            Check("歌词取句：两句之间取前一句",
                LyricsService.LineIndexAt(lines, TimeSpan.FromSeconds(30)) == 1);
            Check("歌词取句：最后一句之后取最后一行",
                LyricsService.LineIndexAt(lines, TimeSpan.FromMinutes(3)) == 2);
            Check("歌词曲目键：同曲同键、异曲异键",
                LyricsService.TrackKey("夜曲", "周杰伦") == LyricsService.TrackKey(" 夜曲 ", "周杰伦")
                && LyricsService.TrackKey("夜曲", "周杰伦") != LyricsService.TrackKey("夜曲", "其他"));
        }
        // 托盘开关（阶段 6）：开关必须落到配置对象上，且能落盘/读回
        {
            var togglesCfg = new AppConfig();
            using var togglesIsland = new NativeIslandApp(togglesCfg, new MediaSessionService());
            bool defaultsOn = togglesIsland.SpectrumEnabled && togglesIsland.LyricsEnabled;
            togglesIsland.SpectrumEnabled = false;
            togglesIsland.LyricsEnabled = false;
            bool offApplied = !togglesCfg.Spectrum && !togglesCfg.Lyrics;
            togglesIsland.LyricsEnabled = true;
            Check("托盘开关：读写落在配置对象上",
                defaultsOn && offApplied && togglesCfg.Lyrics && !togglesCfg.Spectrum);

            // 紧凑媒体标题：省略助手 + 标题带几何（带必须在封面之后、频谱之前）
            {
                Check("标题省略：短文本原样", NativeIslandApp.Ellipsize("短名", 15f, 500f) == "短名");
                string longText = "这是一个特别特别特别长的歌曲标题 definitely 超出宽度";
                string cut = NativeIslandApp.Ellipsize(longText, 15f, 120f);
                Check("标题省略：长文本以…收尾且宽度达标",
                    cut.EndsWith("…") && cut.Length < longText.Length
                    && NativeIslandApp.MeasureForTest(cut, 15f) <= 120f + 0.5f,
                    $"cut='{cut}'");
                var bandBox = new SkiaSharp.SKRect(0, 0, 300, 52);
                var band = NativeIslandApp.MediaTitleBand(bandBox, 1f);
                // 封面 36 宽起 x14（HTML 实测）→ 标题带起 x60；右缘给频谱留 46
                Check("媒体标题带：位于封面（左 60）之后、频谱（右 46）之前",
                    Math.Abs(band.Left - 60) < 0.01f && Math.Abs(band.Right - 254) < 0.01f
                    && band.Width > 100);
            }

            // 进度推进（SMTC Position 是快照，播放中必须本地推进，暂停/拖动/换曲才看 SMTC）
            {
                const long dur = 300_000;
                Check("进度推进：播放中按墙钟走",
                    MediaSessionService.AdvancePosition(60_000, 60_000, dur, playing: true, elapsedMs: 500, trackChanged: false) == 60_500);
                Check("进度推进：暂停冻结在 SMTC 位置",
                    MediaSessionService.AdvancePosition(60_000, 60_000, dur, playing: false, elapsedMs: 9_000, trackChanged: false) == 60_000);
                Check("进度推进：拖动（跳变 >1.5s）以 SMTC 为准",
                    MediaSessionService.AdvancePosition(60_000, 200_000, dur, playing: true, elapsedMs: 500, trackChanged: false) == 200_000);
                Check("进度推进：换曲强制对齐 SMTC",
                    MediaSessionService.AdvancePosition(280_000, 1_000, dur, playing: true, elapsedMs: 500, trackChanged: true) == 1_000);
                Check("进度推进：钳在时长内、不为负",
                    MediaSessionService.AdvancePosition(299_500, 299_500, dur, playing: true, elapsedMs: 1_000, trackChanged: false) == dur
                    && MediaSessionService.AdvancePosition(0, 0, dur, playing: false, elapsedMs: 1_000, trackChanged: false) == 0);
            }

            // 「⌂ 面板」钮：媒体态回多页面板的唯一出口，位于左下角空位，
            // 必须与传输键（中下三键）、音量行、进度时间标签互不重叠（0.95–1.25 缩放都成立）
            {
                bool okAll = true;
                string detail = "";
                foreach (var scale in new[] { 0.95f, 1f, 1.25f })
                {
                    var panel = new SkiaSharp.SKRect(0, 0, (float)(460 * scale), (float)(320 * scale));
                    var ps = NativeIslandApp.PanelSwitchRect(panel, 1f);
                    double mid = panel.MidX, cy = panel.Bottom - 56;
                    bool overlapsTransport =
                        ps.Right > mid - 80 - 8 && ps.Top < cy + 40 && ps.Bottom > cy - 20;
                    bool overlapsVolume =
                        ps.Top < panel.MidY + 66 && ps.Bottom > panel.MidY + 38 && ps.Right > panel.Left + 56;
                    bool inside = ps.Left >= panel.Left && ps.Bottom <= panel.Bottom;
                    if (overlapsTransport || overlapsVolume || !inside)
                    {
                        okAll = false;
                        detail = $"scale={scale} transport={overlapsTransport} volume={overlapsVolume} inside={inside}";
                        break;
                    }
                }
                Check("面板钮：左下角空位，不与传输键/音量行重叠（三档缩放）", okAll, detail);
            }

            // 媒体激活沿（用户场景：无计划 + 浏览器播 B 站 → 媒体胶囊和频谱必须自己出现）
            {
                var edgeCfg = new AppConfig();   // enabled=false：没有任何计划
                var edgeMedia = new MediaSessionService();
                var edgeIsland = new NativeIslandApp(edgeCfg, edgeMedia);
                edgeIsland.UpdateMediaFocus();   // 初始：无媒体
                Check("媒体焦点：无媒体时保持时钟", edgeIsland.Focus == "timer");
                edgeMedia.InjectState(new MediaState
                {
                    Active = true, Title = "测试视频", AppId = "bilibili.exe", Status = "Playing",
                });
                edgeIsland.UpdateMediaFocus();
                Check("媒体焦点：开始播放自动切到媒体（无需计划存在）", edgeIsland.Focus == "media",
                    edgeIsland.Focus);
                edgeMedia.InjectState(new MediaState { Active = false });
                edgeIsland.UpdateMediaFocus();
                Check("媒体焦点：媒体停止回落时钟焦点", edgeIsland.Focus == "timer", edgeIsland.Focus);
                // 会话恢复后即使焦点被异常路径改回 timer，无计划场景下一拍必须纠正（用户实测踩过）
                edgeMedia.InjectState(new MediaState
                    { Active = true, Title = "x", AppId = "bilibili.exe", Status = "Playing" });
                edgeIsland.UpdateMediaFocus();
                edgeIsland.ForceFocus("timer");   // 模拟焦点被异常路径改掉
                edgeIsland.UpdateMediaFocus();    // 无边沿，但无计划 → 持续保证
                Check("媒体焦点：无计划时持续保证媒体（不靠边沿）", edgeIsland.Focus == "media",
                    edgeIsland.Focus);
                // 有计划时尊重手动切换：不出现沿就不抢回
                var planCfg = new AppConfig { Enabled = true, Weekdays = { 1, 2, 3, 4, 5, 6, 7 } };
                using var planIsland = new NativeIslandApp(planCfg, edgeMedia);
                planIsland.UpdateMediaFocus();    // 出现沿 → media
                planIsland.ForceFocus("timer");   // 用户手动切回时钟
                planIsland.UpdateMediaFocus();    // 无出现沿 → 不抢
                Check("媒体焦点：有计划时尊重手动切换", planIsland.Focus == "timer", planIsland.Focus);
            }

            // 岛设置：几何字段的钳制与落位（纯函数，不依赖窗口）
            {
                var g = new AppConfig { CompactScale = 9, ExpandedScale = 0.1, OffsetX = 9999, OffsetY = -5 };
                g = ConfigStore.Normalize(g);
                Check("岛设置：缩放/偏移钳制回默认",
                    g.CompactScale == 1.0 && g.ExpandedScale == 1.0 && g.OffsetX == 0 && g.OffsetY == 8,
                    $"scale={g.CompactScale}/{g.ExpandedScale} off={g.OffsetX}/{g.OffsetY}");
                var g2 = ConfigStore.Normalize(new AppConfig
                    { CompactScale = 1.4, ExpandedScale = 1.2, OffsetX = -200, OffsetY = 120 });
                Check("岛设置：合法值原样保留",
                    g2.CompactScale == 1.4 && g2.ExpandedScale == 1.2 && g2.OffsetX == -200 && g2.OffsetY == 120);

                // 落位：1920 宽工作区，默认居中 640；(640-1920)/2 = -640 + workLeft
                var (cx, cy) = NativeIslandApp.ShellPositionFor(0, 1920, 0, 0, 8);
                Check("岛设置：默认落位顶部居中 +8", cx == (1920 - 640) / 2 && cy == 8, $"({cx},{cy})");
                var (ox, oy) = NativeIslandApp.ShellPositionFor(0, 1920, 0, 5000, 500);
                Check("岛设置：偏移钳制不出工作区",
                    ox == 1920 - 640 && oy == 400,
                    $"({ox},{oy}) 期望 x={1920 - 640}");
            }

            // 日程页布局：单元格/删除钮/新建钮的几何契约（绘制与命中共用 TaskLayout）
            {
                var body = new SkiaSharp.SKRect(24, 56, 24 + 412, 56 + 224);
                var (cells5, dels5, add5, cols5) = NativeIslandApp.TaskLayout(body, 1f, 5);
                Check("日程布局：≤5 条单列、格不重叠且在内容区内",
                    cols5 == 1 && cells5.Length == 5
                    && cells5.Zip(cells5.Skip(1), (a, b) => a.Bottom <= b.Top).All(ok => ok)
                    && cells5.All(r => r.Left >= body.Left && r.Top >= body.Top
                                       && r.Right <= body.Right && r.Bottom <= body.Bottom));
                Check("日程布局：>5 条双列、12 格全部可见且不越界",
                    NativeIslandApp.TaskLayout(body, 1f, 20) is var t12
                    && t12.cols == 2 && t12.cells.Length == 12
                    && t12.cells.All(r => r.Left >= body.Left && r.Top >= body.Top
                                          && r.Right <= body.Right && r.Bottom <= body.Bottom)
                    && t12.cells.Zip(t12.cells.Skip(1), (a, b) => a.Right <= b.Left || a.Bottom <= b.Top)
                        .All(ok => ok));
                Check("日程布局：删除钮贴格右缘、宽度恒定；新建条在列表之后且不重叠、不越界",
                    dels5.All(d => Math.Abs(d.Right - body.Right) < 0.01f
                                    && Math.Abs(d.Width - NativeIslandApp.TaskDelW) < 0.01f)
                    && add5.Top >= cells5[^1].Bottom
                    && add5.Bottom <= body.Bottom + 0.01f);
            }

            Check("SMTC 黑名单：抖音会话被忽略，其他来源不受影响",
                MediaSessionService.IsIgnoredApp("com.douyin.desktop")
                && MediaSessionService.IsIgnoredApp("Douyin.Exe")
                && !MediaSessionService.IsIgnoredApp("MSEdge!App")
                && !MediaSessionService.IsIgnoredApp("cloudmusic.exe"));

            // 展开态点击归属：媒体页可见时，隐藏的六个页面处理器必须让位
            // （它们原先无条件 return，把媒体页的播放/暂停/下一首/进度条点击吃掉了）
            Check("点击归属：媒体页可见时页面处理器一律让位",
                !NativeIslandApp.PageHandlerOwnsClick(true, 0, 0)
                && !NativeIslandApp.PageHandlerOwnsClick(true, 4, 4)
                && !NativeIslandApp.PageHandlerOwnsClick(true, 3, 3)
                && !NativeIslandApp.PageHandlerOwnsClick(true, 5, 5)
                && NativeIslandApp.PageHandlerOwnsClick(false, 0, 0)
                && !NativeIslandApp.PageHandlerOwnsClick(false, 0, 4));

            // AUMID → 进程名：抖音等点分 AppId 曾因回退取末段（desktop.exe）永远跳不过去
            Check("AUMID→进程：抖音点分 AUMID / 浏览器 AUMID 都能命中实际进程",
                LingYun.Platform.WindowFocus.ExeCandidates("com.douyin.desktop").First() == "douyin.exe"
                && LingYun.Platform.WindowFocus.ExeCandidates("MSEdge!App").First() == "msedge.exe"
                && LingYun.Platform.WindowFocus.ExeCandidates("cloudmusic.exe").First() == "cloudmusic.exe");

            // 快捷页网格：安全卡流排 + 危险动作永远独占最后一行（安全保护）
            {
                var qb = new SkiaSharp.SKRect(0, 0, 412, 200);
                var g0 = NativeIslandApp.QuickGridLayout(qb, 1f, 0);
                Check("快捷网格：无自定义 = 6 格、「＋」在安全行末、危险行最后",
                    g0.Cells.Length == 6 && g0.SlotIndex == 2 && g0.DangerStart == 3
                    && g0.Cells[3].Top > g0.Cells[2].Bottom);
                var g1 = NativeIslandApp.QuickGridLayout(qb, 1f, 1);
                Check("快捷网格：1 个自定义 = 7 格、危险行仍是最后一行",
                    g1.Cells.Length == 7 && g1.SlotIndex == 3
                    && g1.Cells[g1.DangerStart].Top > g1.Cells[g1.DangerStart - 1].Bottom);
            }

            // 退出确认框：两个按钮的几何契约（绘制与命中共用，错位就会点不中）
            {
                var box = new SkiaSharp.SKRect(0, 0, (float)NativeIslandApp.ConfirmWidth, (float)NativeIslandApp.ConfirmHeight);
                var (cancel, quit) = NativeIslandApp.ConfirmButtons(box, 1f);
                Check("退出确认：取消在左、退出在右且不重叠",
                    cancel.Right < quit.Left
                    && cancel.Left >= box.Left && quit.Right <= box.Right
                    && cancel.Top >= box.Top && quit.Bottom <= box.Bottom,
                    $"cancel={cancel.Left:0}..{cancel.Right:0} quit={quit.Left:0}..{quit.Right:0}");
                // 视图缩放后命中区必须跟着缩放（否则高 DPI 屏点不中）
                var (c2, q2) = NativeIslandApp.ConfirmButtons(box, 2f);
                Check("退出确认：命中区随缩放同步",
                    Math.Abs((q2.Width / q2.Height) - (quit.Width / quit.Height)) < 0.01
                    && q2.Width > quit.Width);
            }

            var tmpCfg = Path.Combine(Path.GetTempPath(), "lingyun-cfg-selftest.json");
            ConfigStore.Save(new AppConfig { Spectrum = false, Lyrics = false }, tmpCfg);
            var backCfg = ConfigStore.Load(tmpCfg);
            bool persisted = !backCfg.Spectrum && !backCfg.Lyrics
                             && new AppConfig().Spectrum && new AppConfig().Lyrics;
            try { File.Delete(tmpCfg); } catch { /* ignore */ }
            Check("托盘开关：spectrum/lyrics 落盘读回，默认都为开", persisted,
                $"读回 spectrum={backCfg.Spectrum} lyrics={backCfg.Lyrics}");

            // 浅色主题开关 + 两套配色的可读性（WCAG 对比度）
            togglesIsland.LightTheme = true;
            Check("浅色主题开关：落到配置 theme 字段", togglesCfg.Theme == "light");
            togglesIsland.LightTheme = false;
            Check("浅色主题开关：可切回深色", togglesCfg.Theme == "dark");

            var dk = Ui.IslandPalette.DarkTheme;
            var lt = Ui.IslandPalette.LightTheme;
            double dkContrast = Ui.IslandPalette.Contrast(dk.Fg, dk.Body);
            double ltContrast = Ui.IslandPalette.Contrast(lt.Fg, lt.Body);
            double ltSubContrast = Ui.IslandPalette.Contrast(lt.Sub, lt.Body);
            Check("配色：深色为纯黑（用户指定，OLED 友好）",
                dk.Body.Red == 0 && dk.Body.Green == 0 && dk.Body.Blue == 0);
            Check("配色：主文字对比度 ≥ 7（两套都达到 AAA）",
                dkContrast >= 7 && ltContrast >= 7,
                $"深色 {dkContrast:0.0} / 浅色 {ltContrast:0.0}");
            Check("配色：次要文字对比度 ≥ 4.5（至少 AA）", ltSubContrast >= 4.5, $"浅色次文字 {ltSubContrast:0.0}");
            // 提示类文字（Dim）最容易被远程/缩放糊掉，钉住它的对比度
            double dkDim = Ui.IslandPalette.Contrast(dk.Dim, dk.Body);
            double ltDim = Ui.IslandPalette.Contrast(lt.Dim, lt.Body);
            Check("配色：提示文字对比度 ≥ 4.5（两套都要）", dkDim >= 4.5 && ltDim >= 4.5,
                $"深色 Dim {dkDim:0.0} / 浅色 Dim {ltDim:0.0}");
            Check("配色：浅底亮、深底暗",
                Ui.IslandPalette.RelativeLuminance(lt.Body) > 0.8
                && Ui.IslandPalette.RelativeLuminance(dk.Body) < 0.05);
        }

        // 系统通知：抢占规则、自适应宽度、单行文案
        {
            Check("通知：开关关掉 / 非紧凑态 / 过期 都不显示",
                !NativeIslandApp.ShouldShowToast(false, true, 100, 6000)
                && !NativeIslandApp.ShouldShowToast(true, false, 100, 6000)
                && !NativeIslandApp.ShouldShowToast(true, true, 6001, 6000)
                && NativeIslandApp.ShouldShowToast(true, true, 5999, 6000));
            Check("通知：短文本宽度取下限 260", Math.Abs(NativeIslandApp.ToastWidth(0, 1.0) - 260) < 0.01,
                NativeIslandApp.ToastWidth(0, 1.0).ToString("0.#"));
            Check("通知：长文本宽度取上限 620", Math.Abs(NativeIslandApp.ToastWidth(2000, 1.0) - 620) < 0.01,
                NativeIslandApp.ToastWidth(2000, 1.0).ToString("0.#"));
            Check("通知：宽度随文本单调不减",
                NativeIslandApp.ToastWidth(300, 1) <= NativeIslandApp.ToastWidth(400, 1));
            Check("通知：宽度随缩放等比",
                Math.Abs(NativeIslandApp.ToastWidth(300, 1.5) - NativeIslandApp.ToastWidth(300, 1) * 1.5) < 0.01);
            Check("通知：只有标题时单行显示「应用 · 标题」",
                new ToastData("微信", "张三", "").OneLine == "微信 · 张三");
            Check("通知：标题已含应用名时不重复前缀",
                new ToastData("微信", "微信团队：更新完成", "").OneLine == "微信团队：更新完成");
            Check("通知：无标题用正文、无正文用应用名",
                new ToastData("微信", "", "在吗").OneLine == "微信 · 在吗"
                && new ToastData("微信", "", "").OneLine == "微信");
        }

        // 组合模式：布局槽位、自动长度 clamp、缩放跟随
        {
            const float cw = 60, hw = 100, mw = 200;
            var (total3, slots3) = NativeIslandApp.CompositeLayout(true, true, true, cw, hw, mw);
            Check("组合模式：三模块槽位按 时间→硬件→媒体 排列",
                slots3.Length == 3
                && slots3[0].X < slots3[1].X && slots3[1].X < slots3[2].X
                && Math.Abs(slots3[0].W - cw) < 0.01
                && Math.Abs(slots3[1].W - hw) < 0.01
                && Math.Abs(slots3[2].W - mw) < 0.01);
            bool noOverlap = true;
            for (int i = 1; i < slots3.Length; i++)
                if (slots3[i].X < slots3[i - 1].X + slots3[i - 1].W) noOverlap = false;
            Check("组合模式：模块之间无重叠且间距 16", noOverlap
                && Math.Abs(slots3[1].X - (slots3[0].X + slots3[0].W) - 16) < 0.01);
            Check("组合模式：总宽 = 左 16 + 模块 + 间距 + 右 10",
                Math.Abs(total3 - (16 + cw + 16 + hw + 16 + mw + 10)) < 0.01, total3.ToString("0.#"));
            Check("组合模式：单个模块也成立（总宽随模块数递减）",
                NativeIslandApp.CompositeLayout(true, false, false, cw, hw, mw).Total
                < NativeIslandApp.CompositeLayout(true, true, false, cw, hw, mw).Total
                && NativeIslandApp.CompositeLayout(true, true, false, cw, hw, mw).Total < total3);
            Check("组合模式：三模块全关 → 宽度退回时钟胶囊",
                Math.Abs(NativeIslandApp.CompositeWidth(false, false, false, cw, hw, mw, 1.0)
                         - NativeIslandApp.BaseCompactW) < 0.01);
            Check("组合模式：自动长度下限 220 / 上限 900",
                Math.Abs(NativeIslandApp.CompositeWidth(true, false, false, 10, 0, 0, 1.0) - 220) < 0.01
                && Math.Abs(NativeIslandApp.CompositeWidth(true, true, true, 900, 900, 900, 1.0) - 900) < 0.01);
            Check("组合模式：宽度随缩放等比",
                Math.Abs(NativeIslandApp.CompositeWidth(true, true, true, cw, hw, mw, 1.5)
                         - total3 * 1.5) < 0.01);
            // 槽位在岛体矩形内（媒体模块右缘不越界）
            var islandRect = new SKRect(10, 0, (float)(10 + total3 * 1.2), 52);
            var last = NativeIslandApp.SlotRect(islandRect, slots3[2], 1.2f);
            Check("组合模式：最后一个模块右缘不越出岛体",
                last.Right <= islandRect.Right - 10 * 1.2f + 0.6f,
                $"右缘 {last.Right:0.#} vs 岛右 {islandRect.Right:0.#}");
            Check("组合模式：硬件槽宽按「100%」定宽（含标签与进度条）",
                NativeIslandApp.CompositeHwW()
                > NativeIslandApp.MeasureForTest("100%", 10.5f) + 26 + 6);
            Check("组合模式：时钟槽宽 ≥ 单时刻文本（倒计时不撑破槽）",
                NativeIslandApp.CompactClockSlotW()
                >= NativeIslandApp.MeasureForTest("23:59", 22, SKFontStyleWeight.SemiBold) - 0.01);
            // 三模块全关时 Normalize 强制留时间，避免出现一个空壳
            var compEmpty = new AppConfig
            {
                Composite = true, CompositeClock = false, CompositeHardware = false, CompositeMedia = false,
            };
            Check("组合模式：全关时 Normalize 强制保留时间模块",
                ConfigStore.Normalize(compEmpty).CompositeClock);
        }

        // 歌词卡拉OK：进度纯函数（起点 0 / 中点 0.5 / 终点 1 / 越界夹取 / 延迟提前 / 无下一句不猜）
        {
            const long line = 65_000, next = 80_000;   // 15 秒一句
            Check("卡拉OK：句首进度 0", Math.Abs(NativeIslandApp.LyricProgress(line, line, next, 0)) < 0.001);
            Check("卡拉OK：句中进度按比例",
                Math.Abs(NativeIslandApp.LyricProgress(line + 7_500, line, next, 0) - 0.5f) < 0.001);
            Check("卡拉OK：句尾/越界夹到 1",
                Math.Abs(NativeIslandApp.LyricProgress(next + 5_000, line, next, 0) - 1f) < 0.001);
            Check("卡拉OK：延迟补偿 +3s → 歌词提前（同一时刻所处进度更小）",
                NativeIslandApp.LyricProgress(line + 3_000, line, next, 3_000)
                < NativeIslandApp.LyricProgress(line + 3_000, line, next, 0));
            Check("卡拉OK：延迟补偿 -3s → 歌词推后（进度更大）",
                NativeIslandApp.LyricProgress(line + 3_000, line, next, -3_000)
                > NativeIslandApp.LyricProgress(line + 3_000, line, next, 0));
            Check("卡拉OK：无下一句（span≤0）返回 0，不猜",
                Math.Abs(NativeIslandApp.LyricProgress(line + 1_000, line, line, 0)) < 0.001);
            Check("歌词延迟：Normalize 钳制 ±3000ms",
                ConfigStore.Normalize(new AppConfig { LyricDelayMs = 9999 }).LyricDelayMs == 0
                && ConfigStore.Normalize(new AppConfig { LyricDelayMs = 1500 }).LyricDelayMs == 1500);
        }

        // 主题跟随系统 / 背景不透明度 / 闲置自动隐藏
        {
            Check("主题解析：light 恒浅、dark 恒深",
                Ui.IslandPalette.ResolveLight("light", false)
                && !Ui.IslandPalette.ResolveLight("dark", true));
            Check("主题解析：system 跟随系统（两向都要对）",
                Ui.IslandPalette.ResolveLight("system", true)
                && !Ui.IslandPalette.ResolveLight("system", false));
            Check("主题：Normalize 接受 system、拒绝乱值",
                ConfigStore.Normalize(new AppConfig { Theme = "system" }).Theme == "system"
                && ConfigStore.Normalize(new AppConfig { Theme = "purple" }).Theme == "dark");
            Check("媒体页样式：Normalize 拒绝乱值、保留 a/b/c",
                ConfigStore.Normalize(new AppConfig { MediaStyle = "nope" }).MediaStyle == "a"
                && ConfigStore.Normalize(new AppConfig { MediaStyle = "c" }).MediaStyle == "c");
            Check("媒体页标题：短标题单行、长标题两行且第二行带省略号",
                NativeIslandApp.WrapTwoLines("夜曲", 300, 18, SKFontStyleWeight.SemiBold).Length == 1
                && NativeIslandApp.WrapTwoLines(new string('长', 40), 100, 18, SKFontStyleWeight.SemiBold)
                    is { Length: 2 } wrapped && wrapped[1].EndsWith("…", StringComparison.Ordinal));

            var opaque = Ui.IslandPalette.For("dark", 100);
            var half = Ui.IslandPalette.For("dark", 50);
            Check("不透明度：100% 时背景不透明", opaque.Body.Alpha == 255);
            Check("不透明度：50% 时背景 alpha 减半",
                Math.Abs(half.Body.Alpha - 128) <= 1, half.Body.Alpha.ToString());
            Check("不透明度：只压背景，文字/强调色保持不透明",
                half.Fg.Alpha == 255 && half.Accent.Alpha == 255 && half.Dim.Alpha == 255);
            Check("不透明度：Normalize 钳到 40–100",
                ConfigStore.Normalize(new AppConfig { Opacity = 5 }).Opacity == 100
                && ConfigStore.Normalize(new AppConfig { Opacity = 70 }).Opacity == 70);

            Check("自动隐藏：默认关闭时永不隐藏",
                !NativeIslandApp.ShouldAutoHide(false, true, false, false, false, 999, 10));
            Check("自动隐藏：有媒体 / 有弹层 / 鼠标在岛上 / 未超时 都不隐藏",
                !NativeIslandApp.ShouldAutoHide(true, true, true, false, false, 999, 10)
                && !NativeIslandApp.ShouldAutoHide(true, true, false, true, false, 999, 10)
                && !NativeIslandApp.ShouldAutoHide(true, true, false, false, true, 999, 10)
                && !NativeIslandApp.ShouldAutoHide(true, true, false, false, false, 9, 10));
            Check("自动隐藏：展开态不隐藏（只收紧凑态）",
                !NativeIslandApp.ShouldAutoHide(true, false, false, false, false, 999, 10));
            Check("自动隐藏：条件齐了才隐藏",
                NativeIslandApp.ShouldAutoHide(true, true, false, false, false, 11, 10));
            Check("自动隐藏：热区判定（工作区顶部 4px 内恢复）",
                NativeIslandApp.CursorInTopZone(0, 0, 4) && NativeIslandApp.CursorInTopZone(4, 0, 4)
                && !NativeIslandApp.CursorInTopZone(5, 0, 4)
                && NativeIslandApp.CursorInTopZone(1000, 1000, 4));
        }

        w.WriteLine();
        w.WriteLine($"{passed} 通过, {failed} 失败");
        return failed;
    }
}
