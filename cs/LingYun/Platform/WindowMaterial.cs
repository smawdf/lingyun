using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LingYun.Platform;

/// <summary>
/// 设置窗口的界面材质（对应原型里的三种风格）。
///
/// 可行性差异要说清楚：
///   acrylic 亚克力 —— 系统真模糊（Win10 1803+ 的 SetWindowCompositionAttribute 亚克力）。
///   glass   液态玻璃 —— 同一套模糊 + 更薄的玻璃色调 + 大圆角。WPF 窗口没有 backdrop-filter，
///                     网页里那套"边缘折射"搬不过来；界面上会如实标注"这一档是近似"。
///   classic 原生 Windows —— 关掉一切透明，纯色 + 方角 + 系统控件。
///
/// 两个踩过的坑，都写在这里免得再犯：
///   1) DWM 的系统背景材质（DWMWA_SYSTEMBACKDROP_TYPE）配 WindowStyle=None 不可靠，
///      而且 DWM 自带圆角只有 ~8px——和我们要的大圆角对不齐时，四角会露出模糊层，
///      看起来就是"分层黑"。所以圆角改成自己裁窗口区域（SetWindowRgn），DWM 圆角关掉。
///   2) 面板不透明度不能一味加厚：有真模糊时 60% 左右既透又能读；
///      岛那边没有模糊、背后是清晰内容，才需要 85% 那种厚材质。
/// </summary>
internal static class WindowMaterial
{
    public const string Acrylic = "acrylic";
    public const string Glass = "glass";
    public const string Classic = "classic";

    /// <summary>Win10 1803 起支持 SetWindowCompositionAttribute 的亚克力模糊。</summary>
    private const int BuildAcrylicBlur = 17134;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMWCP_DONOTROUND = 1;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENTPOLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>
    /// 纯函数（自测用）：按系统版本决定实际能用哪条路。
    /// 返回 "blur-behind"（系统模糊可用）/ "solid"（纯色）。
    /// </summary>
    internal static string ResolveBackdrop(int osBuild, string material)
        => material == Classic ? "solid"
           : osBuild >= BuildAcrylicBlur ? "blur-behind"
           : "solid";

    /// <summary>面板色调：带 alpha 才叫材质。亚克力厚一点（Windows 自己的亚克力也偏实），玻璃更透。</summary>
    internal static int TintArgb(string material, bool dark)
        => material switch
        {
            Glass => dark ? unchecked((int)0x99121419) : unchecked((int)0x8CFFFFFF),
            Classic => dark ? unchecked((int)0xFF202020) : unchecked((int)0xFFF0F0F0),
            _ => dark ? unchecked((int)0xA61B1C20) : unchecked((int)0xA6F2F4F8),   // acrylic
        };

    /// <summary>亚克力/玻璃用窗口区域裁圆角；经典档方角（radius 0 表示不裁）。</summary>
    public static void ApplyRoundedRegion(Window window, double radiusDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            double scale = 1.0;
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
            int w = (int)Math.Round(window.ActualWidth * scale);
            int h = (int)Math.Round(window.ActualHeight * scale);
            if (w <= 0 || h <= 0) return;
            if (radiusDip < 1)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);   // 不裁
                return;
            }
            int d = (int)Math.Round(radiusDip * 2 * scale);
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            if (rgn != IntPtr.Zero) SetWindowRgn(hwnd, rgn, true);
        }
        catch { /* 裁剪失败不影响使用（只是方角） */ }
    }

    /// <summary>
    /// 应用材质。返回实际生效的路子（诊断/界面提示用）。
    /// 调用时机：窗口 SourceInitialized / 尺寸变化 / 主题或材质变化。
    /// </summary>
    public static string Apply(Window window, string material, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return "solid";
        string mode = ResolveBackdrop(Environment.OSVersion.Version.Build, material);
        try
        {
            // 圆角自己裁（DWM 的 ~8px 圆角和我们的半径对不齐 → 四角会露出模糊层）
            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int backdropNone = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropNone, sizeof(int));
            int darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            // GradientColor 是 ABGR（A 在最高字节）
            int tint = TintArgb(material, dark);
            int abgr = unchecked((int)((uint)tint & 0xFF000000
                | (uint)((tint >> 16) & 0xFF)          // R → B
                | (uint)(tint & 0x0000FF00)
                | (uint)((tint & 0xFF) << 16)));        // B → R

            var accent = new ACCENTPOLICY
            {
                AccentState = mode == "blur-behind"
                    ? (material == Glass ? ACCENT_ENABLE_BLURBEHIND : ACCENT_ENABLE_ACRYLICBLURBEHIND)
                    : ACCENT_DISABLED,
                AccentFlags = 2,
                GradientColor = mode == "blur-behind" ? abgr : 0,
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
            return mode;
        }
        catch
        {
            return "solid";   // 材质失败不致命：窗口照常显示
        }
    }
}
