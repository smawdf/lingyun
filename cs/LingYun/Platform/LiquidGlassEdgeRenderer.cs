using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LingYun.Platform;

/// <summary>
/// 液态玻璃边缘折射：只抓窗口外扩的一小圈屏幕像素，并把它折射回圆角内侧。
/// 中心区域不写任何像素，因此不会把设置窗口的控件再盖一层。
/// </summary>
internal sealed class LiquidGlassEdgeRenderer : IDisposable
{
    private const int CaptureMarginPx = 48;
    private const double DispersionPx = 2.4;
    private const double DetailStepPx = 8;
    private const double DetailBoost = 0.6;
    private const double EdgeBandDip = 12;

    private IntPtr _hdcScreen;
    private IntPtr _hdcMem;
    private IntPtr _hBitmap;
    private IntPtr _hOld;
    private IntPtr _bits;
    private int _captureW;
    private int _captureH;
    private bool _disposed;

    internal readonly record struct EdgeProbe(bool InBand, double Distance, double Nx, double Ny)
    {
        internal static EdgeProbe None => new(false, double.PositiveInfinity, 0, 0);
    }

    /// <summary>
    /// 从窗口外侧屏幕像素生成一张透明底的边缘环带图。
    /// source 是 top-down BGRA，窗口左上角对应 source 的 (margin, margin)。
    /// </summary>
    internal static byte[] RenderRingFromBgra(
        int width, int height, int radius, int band, int opacityPercent,
        byte[] source, int sourceWidth, int sourceHeight, int margin, int panelLuminance = 226)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return Array.Empty<byte>();
        if (source.Length < checked(sourceWidth * sourceHeight * 4))
            throw new ArgumentException("BGRA source is smaller than its dimensions", nameof(source));

        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        band = Math.Clamp(band, 1, Math.Max(1, Math.Min(width, height) / 2));
        int opacity = Math.Clamp(opacityPercent, 0, 100);
        var pixels = new byte[checked(width * height * 4)];

        void PaintPixel(int px, int py)
        {
            var edge = Probe(px + 0.5, py + 0.5, width, height, radius, band);
            if (!edge.InBand) return;

            // p is inside the glass. Move from p to its boundary, then a few pixels
            // out into the desktop; that is the part which makes the edge visibly bend.
            double outside = RefractionOffset(edge.Distance, band);
            double bx = px + 0.5 + edge.Nx * (edge.Distance + outside);
            double by = py + 0.5 + edge.Ny * (edge.Distance + outside);
            ReadRgb(source, sourceWidth, sourceHeight,
                margin + bx + edge.Nx * DispersionPx,
                margin + by + edge.Ny * DispersionPx,
                out byte red, out _, out _);
            ReadRgb(source, sourceWidth, sourceHeight,
                margin + bx, margin + by,
                out _, out byte green, out _);
            ReadRgb(source, sourceWidth, sourceHeight,
                margin + bx - edge.Nx * DispersionPx,
                margin + by - edge.Ny * DispersionPx,
                out _, out _, out byte blue);

            // 沿法线再采一个更远的点做对比度增强：背景有结构时（壁纸/图标/窗口边缘）
            // 弯折感明显得多；纯色区域这一项自然归零，不会凭空造出花纹。
            ReadRgb(source, sourceWidth, sourceHeight,
                margin + bx + edge.Nx * DetailStepPx,
                margin + by + edge.Ny * DetailStepPx,
                out _, out byte greenFar, out _);
            double detail = (green - greenFar) * DetailBoost;
            red = Shift(red, detail);
            green = Shift(green, detail);
            blue = Shift(blue, detail);

            // 轻微白色镜面混合，让暗背景上的折射边仍然有玻璃感。
            double t = Math.Clamp(1 - edge.Distance / Math.Max(1, band), 0, 1);
            double whiteMix = 0.10 + 0.14 * t;
            red = MixWhite(red, whiteMix);
            green = MixWhite(green, whiteMix);
            blue = MixWhite(blue, whiteMix);

            // 玻璃棱边：最外侧 1–4px 相对**面板色调**压暗（浅面板）或提亮（深面板）。
            // 为什么必须有这一步：窗外是纯色/浅色壁纸时，"更透"的环带和近白面板几乎同色，
            // 数学上就看不见折射（实测窗外 ~210、浅色玻璃面板 ~226）。棱边的明暗与桌面内容无关，
            // 任何壁纸上都能看到那条玻璃棱，再叠上压缩后的背景与 RGB 色散才是完整的边缘折射。
            double bevel = BevelAmount(edge.Distance, band);
            if (bevel > 0)
            {
                double k = bevel * 0.40;
                if (panelLuminance >= 140)
                {
                    red = Darken(red, k);
                    green = Darken(green, k);
                    blue = Darken(blue, k);
                }
                else
                {
                    red = MixWhite(red, k * 0.8);
                    green = MixWhite(green, k * 0.8);
                    blue = MixWhite(blue, k * 0.8);
                }
            }

            byte alpha = EdgeAlpha(edge.Distance, band, opacity);
            int at = (py * width + px) * 4;
            pixels[at] = Premul(blue, alpha);
            pixels[at + 1] = Premul(green, alpha);
            pixels[at + 2] = Premul(red, alpha);
            pixels[at + 3] = alpha;
        }

        void PaintRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, width);
            y0 = Math.Clamp(y0, 0, height);
            x1 = Math.Clamp(x1, 0, width);
            y1 = Math.Clamp(y1, 0, height);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) PaintPixel(x, y);
        }

        // 直边只扫描窄条；圆角另外扫描四个小方框，避免每 100ms 遍历整张设置页。
        PaintRect(0, 0, width, band + 1);
        PaintRect(0, height - band - 1, width, height);
        PaintRect(0, band, band + 1, height - band);
        PaintRect(width - band - 1, band, width, height - band);
        int corner = (int)Math.Ceiling((double)radius + band + 2);
        PaintRect(0, 0, corner, corner);
        PaintRect(width - corner, 0, width, corner);
        PaintRect(0, height - corner, corner, height);
        PaintRect(width - corner, height - corner, width, height);
        return pixels;
    }

    /// <summary>判断一个窗口内像素是否位于圆角边缘环带，并返回外法线。</summary>
    internal static EdgeProbe Probe(double x, double y, int width, int height, int radius, int band)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
            return EdgeProbe.None;
        double r = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0);
        double b = Math.Max(1, band);

        if (r > 0 && x < r && y < r)
            return CornerProbe(x - r, y - r, r, b);
        if (r > 0 && x >= width - r && y < r)
            return CornerProbe(x - (width - r), y - r, r, b);
        if (r > 0 && x < r && y >= height - r)
            return CornerProbe(x - r, y - (height - r), r, b);
        if (r > 0 && x >= width - r && y >= height - r)
            return CornerProbe(x - (width - r), y - (height - r), r, b);

        if (x >= r && x < width - r)
        {
            if (y < b) return new EdgeProbe(true, y, 0, -1);
            if (y >= height - b) return new EdgeProbe(true, height - y, 0, 1);
        }
        if (y >= r && y < height - r)
        {
            if (x < b) return new EdgeProbe(true, x, -1, 0);
            if (x >= width - b) return new EdgeProbe(true, width - x, 1, 0);
        }
        return EdgeProbe.None;
    }

    private static EdgeProbe CornerProbe(double dx, double dy, double radius, double band)
    {
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001) return EdgeProbe.None;
        // 圆角内侧的环带位于圆弧以内，距离边界为 radius - 圆心距；
        // dx/dy 的单位向量指向左上外侧，正好是折射取样的外法线。
        double distance = radius - length;
        if (distance < 0 || distance > band) return EdgeProbe.None;
        return new EdgeProbe(true, distance, dx / length, dy / length);
    }

    /// <summary>
    /// 边缘折射从边界向外取样的距离。
    /// 关键：这个值必须**随离边界的距离明显变化**（3px → 24px），才能把窗外一条宽背景
    /// 压缩进窄环带里 —— 那才是玻璃边缘的"弯折"观感。之前取成近似恒定值（6→10px），
    /// 结果只是把背景平移了几像素，看着仍然像"一层半透明"。
    /// 上限 + 色散必须小于抓屏外扩的 margin，否则会采到窗口自己。
    /// </summary>
    internal static double RefractionOffset(double distance, double band)
    {
        double t = Math.Clamp(1 - distance / Math.Max(1, band), 0, 1);
        return 3 + 23 * Math.Pow(t, 1.5);
    }

    /// <summary>玻璃棱边强度：只在最外侧 1–4px 起作用，向内侧迅速归零。</summary>
    internal static double BevelAmount(double distance, double band)
    {
        double t = Math.Clamp(1 - distance / Math.Max(1, band), 0, 1);
        return Math.Pow(t, 2.4);
    }

    /// <summary>边缘 alpha：靠近边界更透（露出真实背景），向内侧衰减到材质色调。</summary>
    internal static byte EdgeAlpha(double distance, double band, int opacityPercent)
    {
        double t = Math.Clamp(1 - distance / Math.Max(1, band), 0, 1);
        double material = 26 + 190 * Math.Pow(t, 1.25);
        return (byte)Math.Clamp(Math.Round(material * Math.Clamp(opacityPercent, 0, 100) / 100.0), 0, 255);
    }

    /// <summary>给自测和采样映射用：同一边缘的 RGB 三个取样点略微错开。</summary>
    internal static (double X, double Y) RefractedPoint(
        double x, double y, EdgeProbe edge, double band, double channelOffset = 0)
    {
        if (!edge.InBand) return (x, y);
        double offset = edge.Distance + RefractionOffset(edge.Distance, band) + channelOffset;
        return (x + edge.Nx * offset, y + edge.Ny * offset);
    }

    internal BitmapSource? Capture(IntPtr hwnd, double radiusDip, int opacityPercent, int panelLuminance = 226)
    {
        if (_disposed || hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var source = CaptureSource(hwnd, out int sourceWidth, out int sourceHeight);
        if (source is null) return null;

        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int radius = (int)Math.Round(Math.Clamp(radiusDip, 0, 200) * scale);
        int band = Math.Clamp((int)Math.Round(EdgeBandDip * scale), 9, 26);
        var pixels = RenderRingFromBgra(width, height, radius, band, opacityPercent,
            source, sourceWidth, sourceHeight, CaptureMarginPx, panelLuminance);
        if (pixels.Length == 0) return null;
        var result = BitmapSource.Create(width, height, dpi > 0 ? dpi : 96, dpi > 0 ? dpi : 96,
            PixelFormats.Pbgra32, null, pixels, width * 4);
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    /// <summary>
    /// 抓窗口外扩 CaptureMarginPx 的一整块屏幕（top-down BGRA，窗口左上角在 (margin, margin)）。
    /// 诊断用它做"干净桌面"底图 —— 把窗口藏起来再抓一次，就能拼出真实观感对照图。
    /// </summary>
    internal byte[]? CaptureSource(IntPtr hwnd, out int sourceWidth, out int sourceHeight)
    {
        sourceWidth = sourceHeight = 0;
        if (_disposed || hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        int sw = checked(width + CaptureMarginPx * 2);
        int sh = checked(height + CaptureMarginPx * 2);
        if (!EnsureSurface(sw, sh)) return null;
        if (!Native.BitBlt(_hdcMem, 0, 0, sw, sh, _hdcScreen,
            rect.Left - CaptureMarginPx, rect.Top - CaptureMarginPx, 0x00CC0020))
            return null;

        var source = new byte[checked(sw * sh * 4)];
        Marshal.Copy(_bits, source, 0, source.Length);
        sourceWidth = sw;
        sourceHeight = sh;
        return source;
    }

    private bool EnsureSurface(int width, int height)
    {
        if (_bits != IntPtr.Zero && width == _captureW && height == _captureH) return true;
        FreeSurface();
        _hdcScreen = Native.GetDC(IntPtr.Zero);
        if (_hdcScreen == IntPtr.Zero) return false;
        _hdcMem = Native.CreateCompatibleDC(_hdcScreen);
        if (_hdcMem == IntPtr.Zero) { FreeSurface(); return false; }
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _hBitmap = Native.CreateDIBSection(_hdcScreen, ref bmi, Native.DIB_RGB_COLORS,
            out _bits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            FreeSurface();
            return false;
        }
        _hOld = Native.SelectObject(_hdcMem, _hBitmap);
        _captureW = width;
        _captureH = height;
        return true;
    }

    private static void ReadRgb(byte[] source, int width, int height, double x, double y,
        out byte r, out byte g, out byte b)
    {
        int ix = Math.Clamp((int)Math.Round(x), 0, width - 1);
        int iy = Math.Clamp((int)Math.Round(y), 0, height - 1);
        int at = (iy * width + ix) * 4;
        b = source[at];
        g = source[at + 1];
        r = source[at + 2];
    }

    private static byte MixWhite(byte value, double amount)
        => (byte)Math.Clamp(Math.Round(value * (1 - amount) + 255 * amount), 0, 255);

    private static byte Darken(byte value, double amount)
        => (byte)Math.Clamp(Math.Round(value * (1 - amount)), 0, 255);

    private static byte Shift(byte value, double delta)
        => (byte)Math.Clamp(Math.Round(value + delta), 0, 255);

    private static byte Premul(byte value, byte alpha)
        => (byte)((value * alpha + 127) / 255);

    private void FreeSurface()
    {
        if (_hOld != IntPtr.Zero && _hdcMem != IntPtr.Zero)
        {
            Native.SelectObject(_hdcMem, _hOld);
            _hOld = IntPtr.Zero;
        }
        if (_hBitmap != IntPtr.Zero)
        {
            Native.DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }
        _bits = IntPtr.Zero;
        if (_hdcMem != IntPtr.Zero)
        {
            Native.DeleteDC(_hdcMem);
            _hdcMem = IntPtr.Zero;
        }
        if (_hdcScreen != IntPtr.Zero)
        {
            Native.ReleaseDC(IntPtr.Zero, _hdcScreen);
            _hdcScreen = IntPtr.Zero;
        }
        _captureW = _captureH = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FreeSurface();
    }
}
