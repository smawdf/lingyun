using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LingYun.Platform;

/// <summary>
/// 多显示器枚举。原先只用 SPI_GETWORKAREA，那只能拿到主显示器，
/// 副屏用户无论怎么设置岛都待在主屏上。
/// </summary>
public static class Displays
{
    /// <summary>一块显示器的完整矩形与工作区（工作区已排除任务栏）。</summary>
    public sealed record Monitor(bool Primary, Native.RECT Bounds, Native.RECT Work);

    /// <summary>按「主显示器优先，其次左→右、上→下」排序，保证索引稳定。</summary>
    public static List<Monitor> All()
    {
        var list = new List<Monitor>();
        Native.MonitorEnumProc cb = (IntPtr hMon, IntPtr hdcMonitor, ref Native.RECT rcMonitor, IntPtr data) =>
        {
            var mi = new Native.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>(),
                szDevice = "",
            };
            if (Native.GetMonitorInfo(hMon, ref mi))
                list.Add(new Monitor((mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, mi.rcMonitor, mi.rcWork));
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        if (list.Count == 0)
        {
            // 兜底：极端情况下枚举失败，退回 SPI_GETWORKAREA
            var r = new Native.RECT();
            SystemParametersInfo(0x0030, 0, ref r, 0);
            list.Add(new Monitor(true, r, r));
            return list;
        }

        return list
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    /// <summary>取指定索引的工作区；越界或 -1（未设置）时退回主显示器。</summary>
    public static Native.RECT WorkAreaOf(int index, List<Monitor>? monitors = null)
    {
        var list = monitors ?? All();
        if (list.Count == 0) return new Native.RECT { Right = 1920, Bottom = 1080 };
        if ((uint)index >= (uint)list.Count)
            index = list.FindIndex(m => m.Primary);
        if (index < 0) index = 0;
        return list[index].Work;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        ref Native.RECT pvParam, uint fWinIni);
}
