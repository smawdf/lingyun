# -*- coding: utf-8 -*-
"""组合模式加「网速」模块（第 4 个模块：↓/↑ 两行）。"""
import io

P = r'D:\kaifa\灵云\cs\LingYun\Ui\NativeIslandApp.cs'
s = io.open(P, encoding='utf-8').read()
log = []


def rep(old, new, why):
    global s
    hit = old in s
    log.append((why, hit))
    if hit:
        s = s.replace(old, new, 1)


# 1) 布局：增加 net 参数（时间 → 硬件 → 网速 → 媒体）
rep('''    internal static (double Total, (double X, double W)[] Slots) CompositeLayout(
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
    }''',
    '''    internal static (double Total, (double X, double W)[] Slots) CompositeLayout(
        bool clock, bool hw, bool net, bool media, float clockW, float hwW, float netW, float mediaW)
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
        if (net) Add(netW);
        if (media) Add(mediaW);
        if (slots.Count == 0) return (0, Array.Empty<(double X, double W)>());
        return (x - CompositeGap + CompositeRightPad, slots.ToArray());
    }''', 'layout')

rep('''    internal static double CompositeWidth(bool clock, bool hw, bool media,
        float clockW, float hwW, float mediaW, double scale)
    {
        var (total, _) = CompositeLayout(clock, hw, media, clockW, hwW, mediaW);''',
    '''    internal static double CompositeWidth(bool clock, bool hw, bool net, bool media,
        float clockW, float hwW, float netW, float mediaW, double scale)
    {
        var (total, _) = CompositeLayout(clock, hw, net, media, clockW, hwW, netW, mediaW);''', 'width')

# 2) 槽宽：两行「↓ 1.2 MB/s」「↑ 34 KB/s」，标签定宽 + 数值定宽（不抖）
rep('''    internal static float CompositeHwW()''',
    '''    /// <summary>组合模式网速槽宽（基准像素）：标签 ↓/↑ + 定宽数值（按 "999.9 MB/s" 预留 → 数字跳动不抖）。</summary>
    internal static float CompositeNetW()
    {
        float tag = Math.Max(MeasureText("↓", CompHwTagSize), MeasureText("↑", CompHwTagSize));
        return tag + 5 + MeasureText("999.9 MB/s", CompHwPctSize);
    }

    internal static float CompositeHwW()''', 'net width')

# 3) 目标宽度调用点
rep('''        => CompositeWidth(_cfg.CompositeClock, _cfg.CompositeHardware,''',
    '''        => CompositeWidth(_cfg.CompositeClock, _cfg.CompositeHardware, _cfg.CompositeNetwork,''', 'target width call')

# 4) 绘制
rep('''        bool media = _cfg.CompositeMedia && MediaActive;
        var (total, slots) = CompositeLayout(_cfg.CompositeClock, _cfg.CompositeHardware,
            media, CompactClockSlotW(), CompositeHwW(), CompositeMediaW());''',
    '''        bool media = _cfg.CompositeMedia && MediaActive;
        var (total, slots) = CompositeLayout(_cfg.CompositeClock, _cfg.CompositeHardware, _cfg.CompositeNetwork,
            media, CompactClockSlotW(), CompositeHwW(), CompositeNetW(), CompositeMediaW());''', 'draw layout')

rep('''        if (_cfg.CompositeHardware) DrawCompositeHardware(canvas, SlotRect(r, slots[i++], cs), cs);
        if (media) DrawCompositeMedia(canvas, SlotRect(r, slots[i], cs), cs);''',
    '''        if (_cfg.CompositeHardware) DrawCompositeHardware(canvas, SlotRect(r, slots[i++], cs), cs);
        if (_cfg.CompositeNetwork) DrawCompositeNetwork(canvas, SlotRect(r, slots[i++], cs), cs);
        if (media) DrawCompositeMedia(canvas, SlotRect(r, slots[i], cs), cs);''', 'draw net')

rep('''    private void DrawHwRow(SKCanvas canvas, SKRect slot, float s, float cy, string tag,''',
    '''    /// <summary>组合模式·网速模块：↓/↑ 两行，数值定宽（跳字时宽度不抖）。</summary>
    private void DrawCompositeNetwork(SKCanvas canvas, SKRect slot, float s)
    {
        double down = _perf?.NetKbps ?? 0;
        double up = _perf?.UploadKbps ?? 0;
        DrawNetRow(canvas, slot, s, slot.MidY - 9 * s, "↓", down);
        DrawNetRow(canvas, slot, s, slot.MidY + 10 * s, "↑", up);
    }

    private void DrawNetRow(SKCanvas canvas, SKRect slot, float s, float cy, string tag, double kbps)
    {
        float tagSize = CompHwTagSize * s;
        DrawText(canvas, tag, slot.Left, cy + 3.5f * s, tagSize, Pal.Sub);
        float tagW = MeasureText(tag, tagSize);
        string text = FmtRate(kbps);
        float size = CompHwPctSize * s;
        DrawText(canvas, text, slot.Left + tagW + 5 * s, cy + 3.5f * s, size, Pal.Fg);
    }

    private void DrawHwRow(SKCanvas canvas, SKRect slot, float s, float cy, string tag,''', 'draw rows')

io.open(P, 'w', encoding='utf-8', newline='\n').write(s)
for why, hit in log:
    print(('OK  ' if hit else 'MISS'), why)
