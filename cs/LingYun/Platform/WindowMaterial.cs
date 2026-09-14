using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LingYun.Platform;

/// <summary>
/// 设置窗口的界面材质（对应原型里的三种风格）。
///
/// 可行性差异要说清楚：
///   acrylic 亚克力 —— 系统真模糊。Win11 22H2+ 走 DWMWA_SYSTEMBACKDROP_TYPE；
///                     Win10 1803+ 走 SetWindowCompositionAttribute 的 Acrylic 模糊；
///                     再老的系统退回纯色（不假装）。
///   glass   液态玻璃 —— WPF 窗口没有 backdrop-filter，网页里那套"边缘折射"搬不过来，
///                     所以这里只能做到"亚克力 + 玻璃色调 + 更大圆角"的近似；
///                     真折射只存在于岛（Skia 自绘）那一侧。
///   classic 原生 Windows —— 关掉一切透明，用系统默认控件长相。
/// </summary>
internal static class WindowMaterial
{
    public const string Acrylic = "acrylic";
    public const string Glass = "glass";
    public const string Classic = "classic";

    /// <summary>Win11 22H2 起支持系统背景材质（Mica/Acrylic 的现代接口）。</summary>
    private const int BuildWin11Backdrop = 22621;
    /// <summary>Win10 1803 起支持 SetWindowCompositionAttribute 的亚克力模糊。</summary>
    private const int BuildAcrylicBlur = 17134;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1, DWMSBT_TRANSIENTWINDOW = 3;   // 3 = Acrylic
    private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

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

    /// <summary>
    /// 纯函数（自测用）：按系统版本决定实际能用哪条路。
    /// 返回 "dwm-acrylic" / "composition-acrylic" / "solid"。
    /// </summary>
    internal static string ResolveBackdrop(int osBuild, string material)
        => material switch
        {
            Classic => "solid",
            _ when osBuild >= BuildWin11Backdrop => "dwm-acrylic",
            _ when osBuild >= BuildAcrylicBlur => "composition-acrylic",
            _ => "solid",                       // 老系统不硬凑：直接纯色，看得清最重要
        };

    /// <summary>
    /// 应用材质。返回实际生效的路子（诊断/界面提示用）。
    /// 调用时机：窗口 SourceInitialized 之后（此时才有 HWND），主题或材质变化时再调一次。
    /// </summary>
    public static string Apply(Window window, string material, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return "solid";
        string mode = ResolveBackdrop(Environment.OSVersion.Version.Build, material);
        try
        {
            int corner = material == Classic ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            if (mode == "dwm-acrylic")
            {
                int backdrop = DWMSBT_TRANSIENTWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                return mode;
            }
            if (mode == "composition-acrylic")
            {
                // Win10 的亚克力：GradientColor 是 ABGR；alpha 太大会糊成一片，0x99 是常见值
                var accent = new ACCENTPOLICY
                {
                    AccentState = material == Glass ? ACCENT_ENABLE_BLURBEHIND : ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    AccentFlags = 2,
                    GradientColor = dark ? unchecked((int)0x99141012) : unchecked((int)0x99F0F2F6),
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

            // 纯色：显式关掉（换材质时不会残留上一次的模糊）
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            var off = new ACCENTPOLICY { AccentState = ACCENT_DISABLED, AccentFlags = 0, GradientColor = 0, AnimationId = 0 };
            int sz = Marshal.SizeOf<ACCENTPOLICY>();
            IntPtr p = Marshal.AllocHGlobal(sz);
            try
            {
                Marshal.StructureToPtr(off, p, false);
                var d = new WINCOMPATTRDATA { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = sz };
                SetWindowCompositionAttribute(hwnd, ref d);
            }
            finally { Marshal.FreeHGlobal(p); }
            return "solid";
        }
        catch
        {
            return "solid";   // 材质失败不致命：窗口照常显示
        }
    }
}
