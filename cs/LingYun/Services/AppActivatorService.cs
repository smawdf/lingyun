using System.Diagnostics;
using System.Runtime.InteropServices;
using LingYun.Platform;

namespace LingYun.Services;

/// <summary>
/// 按 AUMID 唤醒/激活应用（系统通知被点击时用）。
/// 移植自 Apache-2.0 项目 NotchPeninsula 的 appactivator.cs（见 THIRD-PARTY.md）。
/// 与上游的差异：把上游的三条路径串成一条回退链（WinRT 包激活 → COM 激活管理器 →
/// 我们已有的按 exe 前台化 → 按进程名/窗口标题模糊匹配），并返回「走了哪一级」的说明，
/// 供诊断打印；不依赖上游的 Logger 与静态设置。
/// 注意：进程以管理员身份运行时 COM 激活管理器的 ActivateApplication 会被系统拒绝（这是 Windows 的限制）。
/// </summary>
public static class AppActivatorService
{
    /// <summary>三级激活。返回 (是否成功, 走了哪条路 / 失败原因合集)。</summary>
    public static async Task<(bool Ok, string How)> ActivateAsync(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return (false, "AUMID 为空");

        var (ok1, how1) = await TryWinRtAsync(aumid);
        if (ok1) return (true, how1);

        var (ok2, how2) = TryCom(aumid);
        if (ok2) return (true, how2);

        // 经典桌面应用（非打包）也能在这里被唤醒：AUMID 常含 exe 名
        if (WindowFocus.FocusApp(aumid)) return (true, "按进程名前台化");

        var (ok3, how3) = TryBringToFrontByName(aumid);
        if (ok3) return (true, how3);

        return (false, $"WinRT: {how1}；COM: {how2}；{how3}");
    }

    // ---------- ① WinRT 包激活（UWP / 打包应用，按 AUMID 精确匹配） ----------

    private static async Task<(bool Ok, string How)> TryWinRtAsync(string aumid)
    {
        try
        {
            // ConfigureAwait(false)：本服务会被诊断入口（WPF UI 线程、消息泵未开）同步等待，
            // 若续体回投到 Dispatcher 就会死锁——所有 await 都别抓上下文
            var work = LaunchViaWinRtAsync(aumid);
            var done = await Task.WhenAny(work, Task.Delay(5000)).ConfigureAwait(false);
            if (!ReferenceEquals(done, work)) return (false, "WinRT 激活超时（5s）");
            return await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "WinRT 异常：" + ex.GetType().Name);
        }
    }

    private static async Task<(bool Ok, string How)> LaunchViaWinRtAsync(string aumid)
    {
        var pm = new Windows.Management.Deployment.PackageManager();
        foreach (var pkg in pm.FindPackagesForUserWithPackageTypes(
                     null,
                     Windows.Management.Deployment.PackageTypes.Main
                     | Windows.Management.Deployment.PackageTypes.Optional))
        {
            try
            {
                foreach (var entry in await pkg.GetAppListEntriesAsync().AsTask().ConfigureAwait(false))
                {
                    if (!string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool launched = await entry.LaunchAsync().AsTask().ConfigureAwait(false);
                    return launched
                        ? (true, "WinRT LaunchAsync（" + (entry.DisplayInfo?.DisplayName ?? aumid) + "）")
                        : (false, "LaunchAsync 返回 false（可能被用户取消）");
                }
            }
            catch { /* 个别包枚举/启动会抛，继续找下一个 */ }
        }
        return (false, "未找到该 AUMID 的应用包");
    }

    // ---------- ② COM 激活管理器（已向 shell 注册 AUMID 的桌面应用） ----------

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }

    private enum ActivateOptions : uint
    {
        None = 0,
        DesignMode = 1,
        NoErrorUI = 2,
        NoSplashScreen = 4,
    }

    private static (bool Ok, string How) TryCom(string aumid)
    {
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            int hr = mgr.ActivateApplication(aumid, string.Empty, ActivateOptions.None, out uint pid);
            return hr == 0
                ? (true, $"COM ActivateApplication（PID {pid}）")
                : (false, $"COM HRESULT 0x{hr:X8}");
        }
        catch (COMException ex)
        {
            return (false, $"COM 异常 0x{ex.HResult:X8}");
        }
        catch (Exception ex)
        {
            return (false, "COM 异常：" + ex.GetType().Name);
        }
    }

    // ---------- ③ 按进程名 / 窗口标题模糊匹配前台化（兜底） ----------

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private static (bool Ok, string How) TryBringToFrontByName(string aumid)
    {
        var needles = new List<string>();
        var stem = aumid.Split('!')[0].Trim();
        if (stem.Length >= 3) needles.Add(stem);
        var friendly = AppIcons.FriendlyName(aumid);
        if (friendly.Length >= 2 && friendly != "未知来源") needles.Add(friendly);
        if (needles.Count == 0) return (false, "无可用匹配名");

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var handle = proc.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                string name = proc.ProcessName ?? "";
                string title = proc.MainWindowTitle ?? "";
                bool hit = needles.Any(n =>
                    name.Contains(n, StringComparison.OrdinalIgnoreCase)
                    || title.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;
                if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                bool ok = SetForegroundWindow(handle);
                return (ok, ok ? $"按名称前台化（{name}）" : "找到窗口但前台化被系统拒绝");
            }
            catch { /* 个别进程拿不到信息，跳过 */ }
            finally { proc.Dispose(); }
        }
        return (false, "未找到匹配的窗口");
    }
}
