# -*- coding: utf-8 -*-
"""把置顶层级 / 页签条吞点击 / 空白收起开关 / 组合时钟带日期 一次性改进 NativeIslandApp。"""
import io

P = r'D:\kaifa\灵云\cs\LingYun\Ui\NativeIslandApp.cs'
s = io.open(P, encoding='utf-8').read()
ok = []


def rep(old, new, why):
    global s
    hit = old in s
    ok.append((why, hit))
    if hit:
        s = s.replace(old, new, 1)


# 1) 页签条空白处吃掉点击（原来穿透到"点空白收起"，用户反馈"点顶部也会回缩"）
rep('''                    TraceClick("tab band but no tab hit");
                }''',
    '''                    // 页签条上的空白也算导航区：吃掉落点，别穿透到"点空白收起"
                    TraceClick("tab band but no tab hit (swallowed)");
                    return;
                }''', 'tab swallow')

# 2) 展示页空白点击按设置决定收不收
rep('''            // 性能/天气等展示页没有可操作控件：点击空白同样收起展开面板。
            SetMode("compact");
            return;''',
    '''            // 展示页（性能/天气…）没有可操作控件：点空白处按设置决定收不收
            CollapseOnBlank();
            return;''', 'collapse gate')

# 3) 辅助函数 + 置顶层级
rep('''    private static double OutExpo(double t) => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);''',
    '''    private static double OutExpo(double t) => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);

    /// <summary>点空白处收起面板（受「点空白处收起」开关控制；关掉后只有 ✕ 能收）。</summary>
    private void CollapseOnBlank()
    {
        if (_cfg.CollapseOnBlank) SetMode("compact");
    }

    /// <summary>
    /// 置顶层级（纯函数，自测用）：always 恒置顶；normal 不置顶；auto 在前台全屏时让位。
    /// 设置窗口打开期间（uiYield）任何模式都不置顶。
    /// </summary>
    internal static bool EffectiveTopmost(string mode, bool uiYield, bool foregroundFullscreen)
        => !uiYield && mode switch
        {
            "normal" => false,
            "auto" => !foregroundFullscreen,
            _ => true,
        };

    /// <summary>窗口矩形是否铺满显示器（留 2px 容差）。纯函数，自测钉住。</summary>
    internal static bool IsFullscreenRect(System.Windows.Rect win, System.Windows.Rect monitor)
        => win.Left <= monitor.Left + 2 && win.Top <= monitor.Top + 2
           && win.Right >= monitor.Right - 2 && win.Bottom >= monitor.Bottom - 2;

    /// <summary>前台窗口是否全屏铺满它所在显示器（auto 模式据此让位）。</summary>
    private bool ForegroundIsFullscreen()
    {
        try
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _host.Hwnd) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Environment.ProcessId) return false;      // 自己的窗口不算
            if (!Native.GetWindowRect(fg, out var wr)) return false;
            IntPtr mon = Native.MonitorFromWindow(fg, 2);        // MONITOR_DEFAULTTONEAREST
            if (mon == IntPtr.Zero) return false;
            var mi = new Native.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>(),
            };
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            return IsFullscreenRect(
                new System.Windows.Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top),
                new System.Windows.Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top));
        }
        catch { return false; }
    }''', 'topmost helpers')

# 4) 500ms 节拍里应用置顶
rep('''                _nextAutoHideAt = now.AddMilliseconds(500);
                UpdateAutoHide(now);''',
    '''                _nextAutoHideAt = now.AddMilliseconds(500);
                UpdateAutoHide(now);
                ApplyTopmost();''', 'topmost tick')

# 5) 让位开关交给 ApplyTopmost 统一决定
rep('    public void SetTopmostYield(bool yield) => Post(() => _host.SetTopmost(!yield));',
    '''    public void SetTopmostYield(bool yield) => Post(() =>
    {
        _topmostYield = yield;
        ApplyTopmost();
    });

    /// <summary>按「置顶层级」设置决定要不要置顶（auto 模式下前台全屏时让位）。</summary>
    private void ApplyTopmost()
    {
        bool top = EffectiveTopmost(_cfg.TopmostMode, _topmostYield,
            _cfg.TopmostMode == "auto" && ForegroundIsFullscreen());
        if (top != _topmostNow)
        {
            _topmostNow = top;
            _host.SetTopmost(top);
        }
    }''', 'yield flag')

rep('    private bool _volPopup;      // 音量竖向弹出条是否展开',
    '''    private bool _topmostYield;        // 设置窗打开期间让位
    private bool _topmostNow = true;   // 当前实际置顶状态
    private bool _volPopup;      // 音量竖向弹出条是否展开''', 'topmost state')

# 6) 组合模式时钟补日期
rep('''    private void DrawCompositeClock(SKCanvas canvas, SKRect slot, float s)
    {
        string label = ClockLabel();
        float size = CompClockSize * s;
        float w = MeasureText(label, size, SKFontStyleWeight.SemiBold);
        DrawText(canvas, label, slot.MidX - w / 2, slot.MidY + 7 * s, size,
            Pal.Fg, SKFontStyleWeight.SemiBold);
    }''',
    '''    private void DrawCompositeClock(SKCanvas canvas, SKRect slot, float s)
    {
        string label = ClockLabel();
        float size = CompClockSize * s;
        float w = MeasureText(label, size, SKFontStyleWeight.SemiBold);
        // 时间 + 日期两行（用户反馈组合模式下日期丢了）；槽太矮时退回只画时间
        float dateSize = 10 * s;
        string date = CompositeDateText();
        float dw = MeasureText(date, dateSize);
        bool twoLine = slot.Height >= 46 * s;
        DrawText(canvas, label, slot.MidX - w / 2, twoLine ? slot.MidY - 1 * s : slot.MidY + 7 * s,
            size, Pal.Fg, SKFontStyleWeight.SemiBold);
        if (twoLine)
            DrawText(canvas, date, slot.MidX - dw / 2, slot.MidY + 15 * s, dateSize, Pal.Dim);
    }

    /// <summary>组合模式里的日期行（与紧凑时钟胶囊同一套文案）。纯函数便于自测。</summary>
    internal static string CompositeDateText(DateTime now)
        => $"{now.Month}月{now.Day}日 周{WdNames[((int)now.DayOfWeek + 6) % 7]}";

    private string CompositeDateText() => CompositeDateText(DateTime.Now);''', 'composite date')

# 7) 时间槽宽要放得下日期（定宽 → 数字不抖）
rep('''    internal static float CompactClockSlotW()
        => Math.Max(MeasureText("88:88", CompClockSize, SKFontStyleWeight.SemiBold),
            Math.Max(MeasureText("已暂停", CompClockSize, SKFontStyleWeight.SemiBold),
                MeasureText("8时88分", CompClockSize, SKFontStyleWeight.SemiBold)));''',
    '''    internal static float CompactClockSlotW()
        => Math.Max(MeasureText("88:88", CompClockSize, SKFontStyleWeight.SemiBold),
            Math.Max(MeasureText("已暂停", CompClockSize, SKFontStyleWeight.SemiBold),
                Math.Max(MeasureText("8时88分", CompClockSize, SKFontStyleWeight.SemiBold),
                    MeasureText("99月99日 周九", 10, SKFontStyleWeight.Medium))));''', 'clock slot width')

io.open(P, 'w', encoding='utf-8', newline='\n').write(s)
for why, hit in ok:
    print(('OK  ' if hit else 'MISS'), why)
