using System.Runtime.InteropServices;

namespace LingYun.Platform;

/// <summary>
/// 全局鼠标左/右/中键**按下**的观察者（低级钩子 WH_MOUSE_LL）。
///
/// 用途：点**岛外**的桌面空白也能收起展开面板 —— 岛自己的窗口收不到落在别处的点击。
/// 只观察、不拦截（回调里照常 CallNextHookEx 放行），不需要管理员权限。
///
/// 两个必须守住的点：
///   1. 钩子必须装在**有消息循环的线程**上（岛线程就是），回调也在那条线程上执行；
///   2. 回调要极短，而且**绝不能抛异常** —— 抛了系统会悄悄把钩子摘掉，表现为"功能偶尔失灵"。
///      委托还要保活（被 GC 回收后系统回调会崩）。
/// </summary>
internal sealed class GlobalClickWatcher : IDisposable
{
    private readonly Native.HookProc _proc;   // 保活：别让 GC 回收这个委托
    private IntPtr _hook;

    /// <summary>点击回调（屏幕坐标）。返回 true 表示"这次点击我处理了"（本类从不拦截事件本身）。</summary>
    public Func<int, int, bool>? OnClick { get; set; }

    public GlobalClickWatcher() => _proc = Callback;

    /// <summary>钩子是否装上（装不上只是少一个便利，不影响其它功能）。</summary>
    public bool Installed => _hook != IntPtr.Zero;

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        try { _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, IntPtr.Zero, 0); }
        catch { _hook = IntPtr.Zero; }
        return _hook != IntPtr.Zero;
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
            // 这里绝不能抛：抛出去系统会摘掉钩子，而且用户完全看不出来为什么"有时不灵"
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        try { Native.UnhookWindowsHookEx(_hook); } catch { /* 退出路径，失败也不致命 */ }
        _hook = IntPtr.Zero;
    }
}
