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
///   背景模糊则**交给 DWM 合成器**（SetWindowCompositionAttribute 的亚克力）：
///   它是合成器的一部分，窗口移动时模糊跟手、零延迟。
///   曾经试过"自己抓屏 + WPF BlurEffect"，观感能对，但拖动时是卡顿式的——
///   抓屏只有 1Hz、还要 CPU 模糊，这条路对"会移动的窗口"是死路。
///   参考实现：riverar/sample-win32-acrylicblur（WPF 亚克力事实标准样例，微软 Rafael Rivera），
///   它用的就是 AllowsTransparency=True + WindowStyle=None + alpha=1 近透明背景 + accent 模糊。
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

    /// <summary>Win10 1803（17134）起支持 SetWindowCompositionAttribute 的亚克力模糊。</summary>
    private const int BuildAcrylicBlur = 17134;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENTPOLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // ABGR，alpha 就是色调浓度
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 纯函数（自测用）：这一档材质实际怎么实现。
    /// "capture-blur" = 抓屏做背景模糊（自己画）；"tint" = 只做色调（老系统抓不了就退一步）；
    /// "solid" = 纯色（经典档）。
    /// </summary>
    internal static string ResolveBackdrop(int osBuild, string material)
    {
        if (material == Classic) return "solid";
        return osBuild >= BuildAcrylicBlur ? "dwm-acrylic" : "solid";
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

    /// <summary>面板圆角（DIP）。</summary>
    internal static double Radius(string material)
        => material switch { Classic => 0, Glass => 20, _ => 10 };

    /// <summary>是否画落影（经典档不要）。</summary>
    internal static bool HasShadow(string material) => material != Classic;

    /// <summary>
    /// 窗口级设置：深色标题栏属性 + 关掉 DWM 自带圆角（圆角由我们自己的 Border + Clip 画，
    /// DWM 那 ~8px 的圆角会和我们对不齐，四角露馅）+ **让 DWM 去做背景模糊**。
    ///
    /// 模糊为什么交给 DWM：它是合成器的一部分，窗口移动时模糊跟着走、零延迟。
    /// 自己抓屏再糊（曾经的做法）在拖动时必然滞后——抓屏只有 1Hz、还要 CPU 模糊，
    /// 表现就是"模糊背景卡顿式移动"。
    /// 参考实现：riverar/sample-win32-acrylicblur（WPF 亚克力事实标准样例），
    /// 它用的就是 AllowsTransparency=True + WindowStyle=None + 近透明背景 + accent 模糊。
    /// </summary>
    public static void ApplyWindowChrome(Window window, bool dark, string material)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            int tint = TintArgb(material, dark);
            int abgr = unchecked((int)((uint)tint & 0xFF000000
                | (uint)((tint >> 16) & 0xFF)          // R → B
                | (uint)(tint & 0x0000FF00)             // G
                | (uint)((tint & 0xFF) << 16)));        // B → R
            var accent = new ACCENTPOLICY
            {
                AccentState = material == Classic ? ACCENT_DISABLED : ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = material == Classic ? 0 : abgr,
                AnimationId = 0,
            };
            int size = Marshal.SizeOf<ACCENTPOLICY>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WINCOMPATTRDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { /* 设置失败不影响使用 */ }
    }
}
