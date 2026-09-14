using System.Windows;
using System.Windows.Media;

namespace LingYun.Ui;

/// <summary>
/// 编辑窗（岛设置/日程编辑/执行时刻/快捷程序）统一落位：优先放到岛壳正下方。
/// 这些窗口原先固定在屏幕顶部居中（Top 60~140），而岛壳 640×400 也在顶部居中，
/// 展开岛体正好盖住窗口标题栏——两边都是黑底看不出被盖，关闭按钮点不到、窗口又没
/// 标题栏拖不动，表现为「打开了关不了」。放不进岛下方（小屏）就回退原位。
/// </summary>
internal static class IslandPopupPlacer
{
    public static void PlaceBelowIsland(Window w, double fallbackTop, (int X, int Y, int W, int H) shell)
    {
        // 先给 Show() 前的窗口一个不会盖住岛的初始位置；SizeToContent.Height 在构造阶段
        // 往往还没有最终值，不能在这里据 MaxHeight 判断是否放得下，否则会错误回退到 Top 60。
        ApplyTop(w, fallbackTop, shell);
        w.Loaded += (_, _) =>
        {
            // Loaded 后 ActualHeight 已确定，再按真实高度校正；这一步解决设置窗曾回到
            // 岛体同一区域、关闭按钮再次被盖住的问题。
            w.Dispatcher.BeginInvoke(
                new Action(() => ApplyTop(w, fallbackTop, shell)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private static void ApplyTop(Window w, double fallbackTop, (int X, int Y, int W, int H) shell)
    {
        var work = SystemParameters.WorkArea;
        double h = w.ActualHeight > 0 ? w.ActualHeight : w.DesiredSize.Height;
        if (h <= 0) h = 240;

        // Left/Top 用 DIP，岛壳坐标与 WPF 工作区在当前进程 DPI 下保持同一单位。
        double scale = 1;
        try { scale = VisualTreeHelper.GetDpi(w).PixelsPerInchX / 96.0; }
        catch { /* 拿不到 DPI 按 100% 算 */ }

        double top = (shell.Y + shell.H) / scale + 12;
        double maxTop = work.Bottom - h - 8;
        // 优先在岛下方；小屏放不下时仍钳在工作区底部，绝不退回岛体覆盖区。
        w.Top = top <= maxTop
            ? top
            : Math.Max(work.Top + 8, maxTop);
    }

    /// <summary>给无边框窗口的标题区挂拖动：没有标题栏的窗口一旦被什么挡住，用户连挪都挪不走。</summary>
    public static void EnableHeaderDrag(System.Windows.UIElement header, Window w)
    {
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1) return;
            try { w.DragMove(); } catch { /* 鼠标已释放等场景拖不动就算了 */ }
        };
    }
}
