using System.Runtime.InteropServices;

namespace LingYun.Platform;

/// <summary>
/// 全局鼠标左/右/中键**按下**的观察者（低级钩子 WH_MOUSE_LL）。
///
/// 用途：点**岛外**的桌面空白也能收起展开面板 —— 岛自己的窗口收不到落在别处的点击。
/// 只观察、不拦截（回调里照常 CallNextHookEx 放行），不需要管理员权限。
///
/// **为什么必须有这条专用线程（踩过的坑，别再合并掉）**：
///   系统会把**每一次鼠标事件**都交给"装钩子的那条线程"处理，并等它返回。
///   最初我把钩子装在岛的渲染线程上 —— 那条线程绝大多数时间在绘制和 Sleep(16)，
///   没在抽消息，于是每个鼠标事件都要排队等下一帧 → **整台机器的鼠标都变慢变卡**。
///   所以这里自己开一条线程，它只做一件事：SetWindowsHookEx 之后死抽消息。
///   回调里也绝不能做重活（不查 Win32、不读配置、不碰 COM），只读几个缓存字段。
///
/// 另外两点同样必须守住：
///   1. 委托要保活（被 GC 回收后系统回调会崩）；
///   2. 回调**绝不能抛异常** —— 抛了系统会悄悄摘掉钩子，表现为"功能偶尔失灵"。
/// </summary>
internal sealed class GlobalClickWatcher : IDisposable
{
    private readonly Native.HookProc _proc;   // 保活：别让 GC 回收这个委托
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>
    /// 点击回调（屏幕坐标，**在钩子线程上执行 → 必须极短**，只读缓存字段）。
    /// 返回 true 表示"这次点击我处理了"（本类从不拦截事件本身）。
    /// </summary>
    public Func<int, int, bool>? OnClick { get; set; }

    public GlobalClickWatcher() => _proc = Callback;

    /// <summary>钩子是否装上（装不上只是少一个便利，不影响其它功能）。</summary>
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>启动专用线程并装钩子；返回是否成功（等钩子真正装上再返回）。</summary>
    public bool Install()
    {
        if (_thread is not null) return Installed;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            try { _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, IntPtr.Zero, 0); }
            catch { _hook = IntPtr.Zero; }
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            // 这条线程的**全部工作**就是抽消息：抽得越快，系统鼠标延迟越小
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "lingyun-mousehook" };
        _thread.Start();
        ready.Wait(2000);
        return Installed;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    OnClick?.Invoke(data.pt.x, data.pt.y);
                }
            }
        }
        catch
        {
            // 这里绝不能抛：抛出去系统会摘掉钩子，用户完全看不出来为什么"有时不灵"
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { Native.UnhookWindowsHookEx(_hook); } catch { /* 退出路径，失败不致命 */ }
            _hook = IntPtr.Zero;
        }
        // 让钩子线程的消息循环退出（PostThreadMessage 一个 WM_QUIT）
        if (_thread is not null && _threadId != 0)
        {
            try { Native.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); }
            catch { /* ignore */ }
            _thread = null;
        }
    }
}
