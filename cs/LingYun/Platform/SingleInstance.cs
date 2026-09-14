using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace LingYun.Platform;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\灵云_单实例锁";
    private const string PipeName = "lingyun_show";
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; private set; }

    public event Action? ShowRequested;
    /// <summary>第二实例请求退出（`lingyun.exe --exit`）：主实例收到后弹岛内确认框。</summary>
    public event Action? ExitRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        return createdNew;
    }

    public void StartListener()
    {
        if (!IsPrimary) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    int cmd = server.ReadByte();
                    // 'e' = exit，其余（含旧的 's'）都当 show
                    if (cmd == 'e') ExitRequested?.Invoke();
                    else ShowRequested?.Invoke();
                }
                catch
                {
                    await Task.Delay(500, _cts.Token);
                }
            }
        });
    }

    public static void RequestShowExisting() => Send((byte)'s');

    /// <summary>请求正在运行的实例退出（它会在岛上弹确认框，不直接退出）。</summary>
    public static void RequestExitExisting() => Send((byte)'e');

    private static void Send(byte command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            client.WriteByte(command);
        }
        catch { /* 没有主实例 */ }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}

public static class WindowFocus
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder exe, ref int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SW_RESTORE = 9;

    public static bool FocusApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var candidates = ExeCandidates(appId);
        if (candidates.Count == 0) return false;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) return true;
            var path = ExePath(pid);
            if (path.Length == 0) return true;
            // 任一候选命中即可（品牌词 → 实际进程；点分 AUMID 的每一段都可能候选）
            if (!candidates.Any(c => path.EndsWith(c, StringComparison.OrdinalIgnoreCase)))
                return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
        return SetForegroundWindow(found);
    }

    /// <summary>品牌词 → 实际进程名。抖音等的 SMTC AppId 是点分格式（如 com.douyin.desktop），
    /// 不映射的话回退逻辑会取到最后一段 "desktop"，永远匹配不到窗口。</summary>
    private static readonly (string Needle, string Exe)[] BrandExe =
    {
        ("msedge", "msedge.exe"),
        ("chrome", "chrome.exe"),
        ("firefox", "firefox.exe"),
        ("brave", "brave.exe"),
        ("spotify", "spotify.exe"),
        ("cloudmusic", "cloudmusic.exe"),
        ("vlc", "vlc.exe"),
        ("douyin", "douyin.exe"),
        ("bilibili", "bilibili.exe"),
        ("qqmusic", "qqmusic.exe"),
        ("potplayer", "potplayer.exe"),
        ("kugou", "kugou.exe"),
    };

    /// <summary>AUMID → 候选进程名列表（按命中优先级排序）。纯函数，自测用。</summary>
    public static List<string> ExeCandidates(string appId)
    {
        var a = appId.ToLowerInvariant().Trim();
        if (a.Length == 0) return new List<string>();
        foreach (var (needle, exe) in BrandExe)
            if (a.Contains(needle))
                return new List<string> { exe };
        var res = new List<string>();
        var stem = a.Split('!')[0];
        // 点分/下划线 AUMID（org.foo.player、App_player_x）：从后往前每一段都试一遍；
        // 末段可能是 "desktop"/"app" 这类通用词——窗口匹配不到就轮下一个候选
        var parts = stem.Split('.', '/', '\\', '_');
        foreach (var part in parts.Reverse())
            if (part.Length > 2 && !res.Contains(part + ".exe"))
                res.Add(part + ".exe");
        if (stem.Contains('.') && stem.Length > 3) res.Add(stem + ".exe");
        return res;
    }

    /// <summary>兼容旧调用：首个候选（带 .exe）。</summary>
    public static string ExeForAppId(string appId)
        => ExeCandidates(appId).FirstOrDefault() ?? "";

    private static string ExePath(uint pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(512);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }
}
