using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LingYun.Platform;

/// <summary>
/// 设置窗口的界面材质（对应原型里的三种风格）。
///
/// 架构（这是踩过坑之后定下来的，别再改回去）：
///   设置窗口用 **AllowsTransparency=true**，也就是和岛一样走"逐像素 alpha 的分层窗"。
///   原因：普通（非分层）WPF 窗口里，Background=Transparent 的像素对 DWM 来说是不透明黑——
///   圆角外和边缘那一圈会直接变黑（用户看到的"黑框"）；而系统亚克力
///   （SetWindowCompositionAttribute / DWMWA_SYSTEMBACKDROP_TYPE）在无边框窗口上本来就
///   边缘 artifact 多，还和分层窗互斥。
///
///   所以背景模糊不求系统，自己抓、自己糊：抓窗口背后那块屏幕（BackdropSampler），
///   在 WPF 里用它当面板背景并加 BlurEffect——和岛的做法同源。
///   抓屏前提是**把自己从捕获里排除**（WDA_EXCLUDEFROMCAPTURE），否则抓到的就是自己。
///
/// 三档材质只是参数不同（模糊半径 + 色调 alpha + 圆角 + 阴影），都由我们自己画：
///   acrylic 亚克力：模糊 34px，色调 ~65%，圆角 10，带落影
///   glass   液态玻璃：模糊 26px，色调 ~55%，圆角 20，带落影（WPF 做不了边缘折射，
///                     界面上如实标注"这一档是近似"）
///   classic 原生 Windows：不抓屏不模糊，纯色 + 方角 + 无阴影，系统控件长相
/// </summary>
internal static class WindowMaterial
{
    public const string Acrylic = "acrylic";
    public const string Glass = "glass";
    public const string Classic = "classic";

    /// <summary>Win10 2004（19041）起支持 WDA_EXCLUDEFROMCAPTURE，能把窗口从抓屏里排除。</summary>
    private const int BuildExcludeFromCapture = 19041;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// 纯函数（自测用）：这一档材质实际怎么实现。
    /// "capture-blur" = 抓屏做背景模糊（自己画）；"tint" = 只做色调（老系统抓不了就退一步）；
    /// "solid" = 纯色（经典档）。
    /// </summary>
    internal static string ResolveBackdrop(int osBuild, string material)
    {
        if (material == Classic) return "solid";
        return osBuild >= BuildExcludeFromCapture ? "capture-blur" : "tint";
    }

    /// <summary>面板基准色调（带 alpha 才叫材质）：抓屏模糊由 WPF 画，这里只给色调。</summary>
    internal static int TintArgb(string material, bool dark)
        => material switch
        {
            // 玻璃要能明显透出背景（原型就是这观感）；亚克力比它实一点，和 Windows 自己的一致
            Glass => dark ? unchecked((int)0x70101218) : unchecked((int)0x5EFFFFFF),
            Classic => dark ? unchecked((int)0xFF202020) : unchecked((int)0xFFF0F0F0),
            _ => dark ? unchecked((int)0x861A1B20) : unchecked((int)0x7AF2F4F8),   // acrylic
        };

    /// <summary>背景模糊半径（DIP）：亚克力比玻璃糊得更重一点，和原型的手感对齐。</summary>
    internal static double BlurRadius(string material)
        => material == Glass ? 30 : 36;

    /// <summary>面板圆角（DIP）。</summary>
    internal static double Radius(string material)
        => material switch { Classic => 0, Glass => 20, _ => 10 };

    /// <summary>是否画落影（经典档不要）。</summary>
    internal static bool HasShadow(string material) => material != Classic;

    /// <summary>
    /// 抓"窗口背后那块屏幕"，抓之前临时把自己从捕获里排除、抓完立刻恢复。
    ///
    /// 为什么是临时：`WDA_EXCLUDEFROMCAPTURE` 会让这个窗口在**任何**截屏/录屏工具里消失
    /// （实测连开发者自己的 CopyFromScreen 都拍不到它），长期开着不可接受。
    /// 只在 BitBlt 那一瞬间排除，用户的截图/录屏不受影响。
    /// </summary>
    public static byte[]? CaptureBehind(Window window, Services.BackdropSampler sampler,
        int x, int y, int w, int h)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        bool excluded = false;
        try { excluded = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != 0; }
        catch { /* 老系统不支持：那就带着自己一起抓，至少不崩 */ }
        try
        {
            return sampler.CaptureBgra(x, y, w, h);
        }
        finally
        {
            if (excluded)
            {
                try { SetWindowDisplayAffinity(hwnd, WDA_NONE); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 窗口级设置：深色标题栏属性 + 关掉 DWM 自带圆角（圆角由我们自己的 Border + Clip 画，
    /// DWM 那 ~8px 的圆角会和我们对不齐，四角露馅）。
    /// </summary>
    public static void ApplyWindowChrome(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
        }
        catch { /* 设置失败不影响使用 */ }
    }
}
