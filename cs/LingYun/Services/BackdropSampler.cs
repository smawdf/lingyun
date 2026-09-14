using System.Runtime.InteropServices;
using LingYun.Platform;

namespace LingYun.Services;

/// <summary>统计时要剔掉的矩形（屏幕坐标或缓冲内坐标，由调用方对齐）。</summary>
public readonly record struct SampleRect(int X, int Y, int W, int H)
{
    public bool IsEmpty => W <= 0 || H <= 0;
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>岛背后那一小块桌面的实测结果（平均色 + 亮度）。</summary>
public readonly record struct BackdropSample(
    byte R, byte G, byte B,
    /// <summary>平均色的 WCAG 相对亮度（0..1，与 IslandPalette.RelativeLuminance 同一把尺子）。</summary>
    double Luminance,
    /// <summary>4×3 分区里最亮的一格——只取平均会被大片暗色稀释，最亮格代表"最不利的区域"。</summary>
    double BrightestCell,
    int Width, int Height)
{
    public static readonly BackdropSample Unknown = new(0, 0, 0, double.NaN, double.NaN, 0, 0);

    public bool Known => !double.IsNaN(Luminance);
}

/// <summary>
/// 抓岛背后那一小块桌面，算出平均色/亮度——液态玻璃「自适应」的输入。
///
/// 实现方式很关键：**必须整块 BitBlt 进复用的 DIB，再直接读内存**。
/// 本机实测（2560×1440 @200%，20 核）：
///   整块 BitBlt + 直接读内存       ≈ 5.5 ms
///   逐点 GetPixel 采 36 个屏幕像素 ≈ 200 ms（每次调用都要驱动回读，比重整块抓慢两个数量级）
/// 所以"只采几个点省事"是错的，别改回去。
///
/// 线程约定：只允许岛线程调用（DIB 不是线程安全的，也不该和绘制竞争）。
/// </summary>
public sealed class BackdropSampler : IDisposable
{
    // 采样目标点数：约 4000 个点足够稳定，再多只是浪费
    private const int TargetSamples = 4000;
    private const int CellsX = 4, CellsY = 3;

    private IntPtr _hdcScreen, _hdcMem, _hBitmap, _hOld, _bits;
    private int _w, _h;
    private bool _disposed;

    /// <summary>
    /// 采样屏幕矩形 (x, y, w, h)（物理像素），可指定一块矩形从统计里剔除。
    ///
    /// **为什么要"剔除"**：实测发现 DWM 合成下 BitBlt 无论带不带 CAPTUREBLT，
    /// 都会把本进程的分层窗（岛自己）一起采进来——本机把亮色液态玻璃岛放在采样矩形里，
    /// 两种 ROP 都返回 RGB(221,220,222)（就是岛本身），拿它当"背景亮度"等于照镜子。
    /// 所以真正要采的是**岛周围那一圈桌面**，并把岛（含阴影，外扩一点）从统计里排除。
    /// </summary>
    public BackdropSample? Sample(int x, int y, int w, int h, SampleRect exclude = default)
    {
        if (_disposed || w <= 0 || h <= 0) return null;
        try
        {
            if (!EnsureSurface(w, h)) return null;
            if (!Native.BitBlt(_hdcMem, 0, 0, w, h, _hdcScreen, x, y, 0x00CC0020)) return null;   // SRCCOPY
            // exclude 传的是屏幕坐标，换算成缓冲内坐标
            var skip = exclude.IsEmpty
                ? default
                : new SampleRect(exclude.X - x, exclude.Y - y, exclude.W, exclude.H);
            return Scan(w, h, skip);
        }
        catch
        {
            return null;   // 抓屏失败不该影响岛显示：自适应退回"保持当前材质"
        }
    }

    /// <summary>
    /// 抓一块屏幕原始像素（BGRA，top-down）。给设置窗口当"背景模糊"的素材用：
    /// 那里和岛一样是分层窗、拿不到背后内容，只能自己抓。
    /// 采的是屏幕合成结果——调用方要保证自己那个窗口已经从捕获里排除
    /// （WDA_EXCLUDEFROMCAPTURE，见 WindowMaterial.ExcludeFromCapture）。
    /// </summary>
    public byte[]? CaptureBgra(int x, int y, int w, int h)
    {
        if (_disposed || w <= 0 || h <= 0) return null;
        try
        {
            if (!EnsureSurface(w, h)) return null;
            if (!Native.BitBlt(_hdcMem, 0, 0, w, h, _hdcScreen, x, y, 0x00CC0020)) return null;
            int bytes = w * h * 4;
            var buf = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(_bits, buf, 0, bytes);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按尺寸准备 DIB（同尺寸复用；尺寸变化才重建）。</summary>
    private bool EnsureSurface(int w, int h)
    {
        if (_bits != IntPtr.Zero && w == _w && h == _h) return true;
        FreeSurface();

        _hdcScreen = Native.GetDC(IntPtr.Zero);
        if (_hdcScreen == IntPtr.Zero) return false;
        _hdcMem = Native.CreateCompatibleDC(_hdcScreen);
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,   // top-down：内存里第一行就是屏幕第一行
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _hBitmap = Native.CreateDIBSection(_hdcScreen, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            FreeSurface();
            return false;
        }
        _hOld = Native.SelectObject(_hdcMem, _hBitmap);
        _w = w;
        _h = h;
        return true;
    }

    private unsafe BackdropSample Scan(int w, int h, SampleRect skip)
    {
        int step = 4;
        while ((long)(w / step) * (h / step) > TargetSamples) step += 2;

        Span<long> cellSum = stackalloc long[CellsX * CellsY];
        Span<int> cellN = stackalloc int[CellsX * CellsY];
        long sr = 0, sg = 0, sb = 0, n = 0;

        bool hasSkip = !skip.IsEmpty;
        byte* basePtr = (byte*)_bits;
        for (int y = 0; y < h; y += step)
        {
            byte* row = basePtr + (long)y * w * 4;
            int cy = Math.Min(CellsY - 1, y * CellsY / h);
            for (int x = 0; x < w; x += step)
            {
                if (hasSkip && skip.Contains(x, y)) continue;   // 岛自己那块不参与统计
                byte* px = row + (long)x * 4;   // BGRA
                int b = px[0], g = px[1], r = px[2];
                sr += r; sg += g; sb += b; n++;
                int ci = cy * CellsX + Math.Min(CellsX - 1, x * CellsX / w);
                cellSum[ci] += r * 299 + g * 587 + b * 114;
                cellN[ci]++;
            }
        }
        if (n == 0) return BackdropSample.Unknown;

        byte mr = (byte)(sr / n), mg = (byte)(sg / n), mb = (byte)(sb / n);
        double brightest = 0;
        for (int i = 0; i < cellSum.Length; i++)
        {
            if (cellN[i] == 0) continue;
            // 分区亮度用简单加权（和实测像素一一对应），再换算回 0..1
            double luma = cellSum[i] / (double)cellN[i] / 1000.0;
            if (luma > brightest) brightest = luma;
        }
        return new BackdropSample(
            mr, mg, mb,
            Ui.IslandPalette.RelativeLuminance(new SkiaSharp.SKColor(mr, mg, mb)),
            Math.Clamp(brightest / 255.0, 0, 1),
            w, h);
    }

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
        _bits = IntPtr.Zero;   // 必须与位图一起置空，否则下一帧会读已释放内存
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
        _w = _h = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FreeSurface();
    }
}
