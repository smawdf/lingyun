using System.IO;
using SkiaSharp;

namespace LingYun.Services;

/// <summary>
/// Skia 字体选择。SkiaSharp 的 DrawText 不做字体回退，而 Segoe UI 既没有 CJK 字形、
/// 也没有 ⏻/⏮/✕ 这类符号字形，直接用会画成豆腐块。
///
/// 做法（思路借鉴 Apache-2.0 项目 NotchPeninsula 的 BuildTextRuns，但字体解析更严）：
/// 按 code point 逐个决定字体——拉丁 → 中文 → 系统字体匹配，再把同字体的连续字符合并成段，
/// 逐段绘制。这样混排字符串（例如 "歌名 🎵"）里中文和 emoji 都能各自拿到正确字体。
/// </summary>
public static class AppFonts
{
    private static readonly Dictionary<SKFontStyleWeight, SKTypeface> Latin = new();
    private static readonly Dictionary<SKFontStyleWeight, SKTypeface> Cjk = new();
    private static readonly Dictionary<int, SKTypeface?> Fallback = new();

    public static SKTypeface LatinTypeface(SKFontStyleWeight weight)
    {
        lock (Latin)
        {
            if (Latin.TryGetValue(weight, out var t)) return t;
            t = SKTypeface.FromFamilyName("Segoe UI", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            Latin[weight] = t;
            return t;
        }
    }

    /// <summary>
    /// 中文首选（雅黑同时覆盖中文/数字/拉丁）。若该字体不存在，FromFamilyName 会退化成默认字体，
    /// 此时 Covers/GetGlyph 会判定它不覆盖中文，于是继续走逐字符回退——无需额外探测字体是否存在。
    /// </summary>
    public static SKTypeface CjkTypeface(SKFontStyleWeight weight)
    {
        lock (Cjk)
        {
            if (Cjk.TryGetValue(weight, out var t)) return t;
            t = SKTypeface.FromFamilyName("Microsoft YaHei UI", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            Cjk[weight] = t;
            return t;
        }
    }

    public static SKTypeface? FallbackTypeface(int codepoint)
    {
        lock (Fallback)
        {
            if (Fallback.TryGetValue(codepoint, out var cached)) return cached;
            SKTypeface? t;
            try { t = SKFontManager.Default.MatchCharacter(codepoint); }
            catch { t = null; }
            Fallback[codepoint] = t;
            return t;
        }
    }

    /// <summary>显式候选字体族，用于 MatchCharacter 也不给力时的兜底。</summary>
    private static readonly string[] AltFamilies =
    {
        "Microsoft YaHei", "SimSun", "Segoe UI Symbol", "Segoe UI Emoji", "Arial Unicode MS",
    };

    private static readonly Dictionary<(string Family, SKFontStyleWeight Weight), SKTypeface?> Families = new();

    private static SKTypeface? FamilyTypeface(string family, SKFontStyleWeight weight)
    {
        lock (Families)
        {
            var key = (family, weight);
            if (Families.TryGetValue(key, out var cached)) return cached;
            SKTypeface? t;
            try { t = SKFontManager.Default.MatchFamily(family); }
            catch { t = null; }
            Families[key] = t;
            return t;
        }
    }

    /// <summary>
    /// 字体是否真的能画出这个码位。
    /// 只判 GetGlyph != 0 不够——那只能说明"有一个字形编号"，不能说明不是 .notdef 方框，
    /// 而 Skia 的 DirectWrite 后端在 matchFamilyStyleCharacter 上并不可靠。
    /// </summary>
    private static bool Usable(SKTypeface tf, int cp)
        => tf.GetGlyph(cp) != 0 && !IsNotdef(tf, cp);

    /// <summary>单个 code point 该用哪个字体（候选链 + 逐级校验，避免拿到只给 .notdef 的字体）。</summary>
    public static SKTypeface Resolve(int cp, SKFontStyleWeight weight)
    {
        var latin = LatinTypeface(weight);
        if (cp is ' ' or '\t' or '\n' or '\r') return latin;
        if (Usable(latin, cp)) return latin;

        var cjk = CjkTypeface(weight);
        if (!ReferenceEquals(cjk, latin) && Usable(cjk, cp)) return cjk;

        var fb = FallbackTypeface(cp);
        if (fb is not null && Usable(fb, cp)) return fb;

        foreach (var family in AltFamilies)
        {
            var t = FamilyTypeface(family, weight);
            if (t is not null && Usable(t, cp)) return t;
        }
        return cjk;
    }

    /// <summary>变体选择符 / ZWJ / 键帽 / 肤色修饰符：必须与前一个字符同段，不能断开。</summary>
    private static bool IsJoinerOrModifier(int cp)
        => cp is 0xFE0F or 0xFE0E or 0x200D or 0x20E3 || (cp >= 0x1F3FB && cp <= 0x1F3FF);

    /// <summary>按真实码位遍历（正确处理代理对，emoji 不会被拆成两个孤立代理码元）。</summary>
    public static IEnumerable<int> CodePoints(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                yield return text[i];
            }
        }
    }

    /// <summary>把文字切成「同一字体」的连续段。</summary>
    public static List<(string Text, SKTypeface Typeface)> Runs(string text, SKFontStyleWeight weight)
    {
        var runs = new List<(string, SKTypeface)>();
        if (string.IsNullOrEmpty(text)) return runs;

        var nodes = new List<(int Start, int Len, SKTypeface Tf)>();
        int i = 0;
        while (i < text.Length)
        {
            int cp = text[i], len = 1;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                len = 2;
            }

            SKTypeface tf;
            if (IsJoinerOrModifier(cp) && nodes.Count > 0)
            {
                // 不可见修饰符跟随前一个字符
                tf = nodes[^1].Tf;
            }
            else
            {
                tf = Resolve(cp, weight);
                // 下一个字符是变体选择符/ZWJ/键帽 → 当前字符要按 emoji 呈现，
                // 别被基础字体（例如 '#'、'⛸'）抢走
                int nx = i + len;
                if (nx < text.Length)
                {
                    int ncp = text[nx];
                    if (char.IsHighSurrogate(text[nx]) && nx + 1 < text.Length && char.IsLowSurrogate(text[nx + 1]))
                        ncp = char.ConvertToUtf32(text[nx], text[nx + 1]);
                    if (ncp is 0xFE0F or 0x200D or 0x20E3)
                    {
                        // 优先取「能同时覆盖基础字符与修饰符」的字体：
                        // 例如键帽 #️⃣ 里 '#' 本身在 Segoe UI 里有字形，但 U+20E3 只有 emoji 字体才有，
                        // 只按 '#' 找字体就会把它错当普通井号。
                        var emoji = FallbackTypeface(ncp) ?? FallbackTypeface(cp);
                        if (emoji is not null && emoji.GetGlyph(cp) != 0 && emoji.GetGlyph(ncp) != 0)
                            tf = emoji;
                        else
                        {
                            var any = FallbackTypeface(cp);
                            if (any is not null && any.GetGlyph(cp) != 0) tf = any;
                        }
                    }
                }
            }

            nodes.Add((i, len, tf));
            i += len;
        }

        int runStart = 0;
        SKTypeface runTf = nodes[0].Tf;
        foreach (var n in nodes)
        {
            if (ReferenceEquals(n.Tf, runTf)) continue;
            runs.Add((text.Substring(runStart, n.Start - runStart), runTf));
            runTf = n.Tf;
            runStart = n.Start;
        }
        runs.Add((text.Substring(runStart), runTf));
        return runs;
    }

    /// <summary>整串宽度（按段累加）。必须与 Draw 用同一套分段，否则居中会算歪。</summary>
    public static float Measure(string text, float size, SKFontStyleWeight weight = SKFontStyleWeight.Medium)
    {
        using var p = new SKPaint { TextSize = size };
        float w = 0;
        foreach (var (run, tf) in Runs(text, weight))
        {
            p.Typeface = tf;
            w += p.MeasureText(run);
        }
        return w;
    }

    /// <summary>逐段绘制（每段用自己覆盖得到的字体）。</summary>
    public static void Draw(SKCanvas canvas, string text, float x, float y, float size, SKColor color,
        SKFontStyleWeight weight = SKFontStyleWeight.Medium)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, TextSize = size };
        float cx = x;
        var runs = Runs(text, weight);
        foreach (var (run, tf) in runs)
        {
            p.Typeface = tf;
            canvas.DrawText(run, cx, y, p);
            cx += p.MeasureText(run);
        }
        Trace?.Invoke(text, x, y, size, DescribeRunsOf(runs, size));
    }

    private static string DescribeRunsOf(List<(string Text, SKTypeface Typeface)> runs, float size)
    {
        if (runs.Count == 0) return "(empty)";
        // 必须带上实际字号：用默认字号量出来的宽度会让布局日志整体失真
        using var p = new SKPaint { TextSize = size };
        var parts = new List<string>(runs.Count);
        foreach (var (run, tf) in runs)
        {
            p.Typeface = tf;
            parts.Add($"{Escape(run)}={tf.FamilyName}({p.MeasureText(run):0.##})");
        }
        return string.Join(" | ", parts);
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    /// <summary>为整串挑一个字体（用于只有一个字形的场合，如品牌字块）。</summary>
    public static SKTypeface Pick(string text, SKFontStyleWeight weight)
    {
        var runs = Runs(text, weight);
        return runs.Count > 0 ? runs[0].Typeface : LatinTypeface(weight);
    }

    // ==================================================================
    // 诊断支持：位图级 .notdef（豆腐块）判定
    //
    // 为什么光看 GetGlyph(cp) != 0 不够：那只能说明字体里"有一个字形编号"，
    // 不能说明画出来不是 .notdef 方框。必须**真画出来比对**。
    // 判据：把该码位与「必定缺字的 U+FFFF」各渲染成位图，逐像素一致即豆腐块。
    // ==================================================================

    /// <summary>渲染探针的位图边长。</summary>
    internal const int ProbeSize = 48;

    private static readonly Dictionary<(IntPtr Typeface, int Cp), bool> NotdefCache = new();
    private static readonly Dictionary<IntPtr, byte[]> NotdefReferenceCache = new();

    /// <summary>绘制文本时的回调（诊断用；生产环境为 null）。参数：text, x, y, size, runs 描述。</summary>
    public static Action<string, float, float, float, string>? Trace;

    /// <summary>把单个码位渲染成 0/1 墨迹位图。</summary>
    internal static byte[] RenderGlyphBitmap(SKTypeface tf, int cp)
    {
        var empty = new byte[ProbeSize * ProbeSize];
        // 孤立代理码元 / 越界码位无法转成字符串
        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return empty;

        var info = new SKImageInfo(ProbeSize, ProbeSize, SKColorType.Gray8, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        if (surface is null) return empty;
        var c = surface.Canvas;
        c.Clear(SKColors.Black);
        using var p = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 32, Typeface = tf };
        c.DrawText(char.ConvertFromUtf32(cp), 4, 36, p);
        c.Flush();
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var bits = new byte[ProbeSize * ProbeSize];
        for (int y = 0; y < ProbeSize; y++)
            for (int x = 0; x < ProbeSize; x++)
                bits[y * ProbeSize + x] = bmp.GetPixel(x, y).Red > 40 ? (byte)1 : (byte)0;
        return bits;
    }

    internal static int InkCount(byte[] bits)
    {
        int n = 0;
        foreach (var b in bits) if (b != 0) n++;
        return n;
    }

    private static byte[] NotdefReference(SKTypeface tf)
    {
        lock (NotdefReferenceCache)
        {
            if (NotdefReferenceCache.TryGetValue(tf.Handle, out var cached)) return cached;
            // U+FFFF 是 noncharacter，任何字体都不会给它真实字形
            var reference = RenderGlyphBitmap(tf, 0xFFFF);
            NotdefReferenceCache[tf.Handle] = reference;
            return reference;
        }
    }

    /// <summary>该字体画这个码位时是否落到了 .notdef（豆腐块）。</summary>
    public static bool IsNotdef(SKTypeface tf, int cp)
    {
        var key = (tf.Handle, cp);
        lock (NotdefCache)
        {
            if (NotdefCache.TryGetValue(key, out var cached)) return cached;
        }

        bool result;
        try
        {
            var bits = RenderGlyphBitmap(tf, cp);
            var reference = NotdefReference(tf);
            bool same = bits.Length == reference.Length;
            if (same)
            {
                for (int i = 0; i < bits.Length; i++)
                    if (bits[i] != reference[i]) { same = false; break; }
            }
            result = same;
        }
        catch
        {
            result = false;
        }

        lock (NotdefCache) NotdefCache[key] = result;
        return result;
    }

    /// <summary>把分段情况描述成一行文本。</summary>
    public static string DescribeRuns(string text, SKFontStyleWeight weight, float size = 12f)
        => DescribeRunsOf(Runs(text, weight), size);

    /// <summary>逐字符审计：每个码位实际用了哪个字体、画出来是不是豆腐块。</summary>
    public static void Audit(TextWriter w, IEnumerable<string> samples, SKFontStyleWeight weight = SKFontStyleWeight.Medium)
    {
        w.WriteLine("# cp\tchar\tfamily\tglyphId\tinkPixels\tnotdefMatch\tnote");
        var seen = new HashSet<int>();
        int tofu = 0, blank = 0, total = 0;

        foreach (var sample in samples)
        {
            foreach (var cp in CodePoints(sample))
            {
                if (cp is ' ' or '\t' or '\n' or '\r') continue;
                if (!seen.Add(cp)) continue;
                total++;

                SKTypeface tf;
                int glyph;
                byte[] bits;
                try
                {
                    tf = Resolve(cp, weight);
                    glyph = tf.GetGlyph(cp);
                    bits = RenderGlyphBitmap(tf, cp);
                }
                catch (Exception ex)
                {
                    w.WriteLine($"U+{cp:X4}\t\t(EXCEPTION)\t\t\t\t{ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                int ink = InkCount(bits);
                bool notdef = IsNotdef(tf, cp);
                bool joiner = IsJoinerOrModifier(cp);
                string note = "";
                if (notdef && ink > 0) { note = "TOFU(.notdef 方框)"; tofu++; }
                else if (ink == 0 && !joiner) { note = "NO-INK(什么也没画出来)"; blank++; }

                w.WriteLine($"U+{cp:X4}\t{char.ConvertFromUtf32(cp)}\t{tf.FamilyName}\t{glyph}\t{ink}\t{notdef}\t{note}");
            }
        }

        w.WriteLine();
        w.WriteLine($"# 合计 {total} 个码位：豆腐块 {tofu} 个，画不出东西 {blank} 个");
    }
}
