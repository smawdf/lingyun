using System.Runtime.InteropServices;
using SkiaSharp;

namespace LingYun.Platform;

/// <summary>
/// Win32 分层透明窗 + Skia 绘制 + UpdateLayeredWindow。
/// 实现思路对齐 NotchPeninsula（Apache-2.0）：固定/形变画布、逐像素 alpha，不走 WPF 合成。
/// </summary>
public sealed class LayeredIslandHost : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hdcScreen, _hdcMem, _hBitmap, _hOld;
    private IntPtr _bits;
    private int _w, _h;
    private int _x, _y;
    private Native.WndProc? _proc; // 防 GC
    private bool _tracking;
    private volatile bool _closed;

    public IntPtr Hwnd => _hwnd;
    public int Width => _w;
    public int Height => _h;

    /// <summary>WM_CLOSE 已处理、窗口已在创建线程内销毁。</summary>
    public bool IsClosed => _closed;

    public event Action<int, int>? MouseMove;
    public event Action<int, int>? MouseDown;
    public event Action<int, int>? MouseUp;
    public event Action? MouseLeave;
    /// <summary>显示器配置/分辨率变化（WM_DISPLAYCHANGE）。此时需要重新定位岛。</summary>
    public event Action? DisplayChanged;
    /// <summary>滚轮。(x, y, delta)，坐标相对窗口左上角；delta &gt; 0 为向前（上）滚。</summary>
    public event Action<int, int, int>? MouseWheel;
    public event Action? Closed;

    public void Create(int width, int height, int x, int y)
    {
        _w = width;
        _h = height;
        _x = x;
        _y = y;
        _proc = WndProc;
        var hInst = GetModuleHandle(null!);
        var wc = new Native.WNDCLASS
        {
            style = 0,
            lpfnWndProc = _proc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInst,
            hIcon = IntPtr.Zero,
            hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszMenuName = "",
            lpszClassName = "LingYunLayeredIsland",
        };
        Native.RegisterClass(ref wc);

        _hwnd = Native.CreateWindowEx(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE,
            wc.lpszClassName, "灵云",
            Native.WS_POPUP | Native.WS_VISIBLE,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed");

        AllocBitmap(width, height);
        // 首帧空图，保证 layered 窗口可显示
        Present(SKColors.Transparent);
    }

    private void AllocBitmap(int w, int h)
    {
        FreeBitmap();
        _hdcScreen = Native.GetDC(IntPtr.Zero);
        _hdcMem = Native.CreateCompatibleDC(_hdcScreen);
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _hBitmap = Native.CreateDIBSection(_hdcScreen, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection failed");
        _hOld = Native.SelectObject(_hdcMem, _hBitmap);
    }

    private void FreeBitmap()
    {
        if (_hOld != IntPtr.Zero)
        {
            Native.SelectObject(_hdcMem, _hOld);
            _hOld = IntPtr.Zero;
        }
        if (_hBitmap != IntPtr.Zero)
        {
            Native.DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }
        // 必须一并置空：否则 Render() 的守卫会放行，Skia 会写进已释放的 DIB 内存
        _bits = IntPtr.Zero;
        if (_hdcMem != IntPtr.Zero)
        {
            Native.DeleteDC(_hdcMem);
            _hdcMem = IntPtr.Zero;
        }
        if (_hdcScreen != IntPtr.Zero)
        {
            Native.ReleaseDC(IntPtr.Zero, _hdcScreen);
            _hdcScreen = IntPtr.Zero;
        }
    }

    /// <summary>用 Skia 在画布上绘制并推送到分层窗。</summary>
    public void Render(Action<SKCanvas, SKImageInfo> draw)
    {
        if (_hwnd == IntPtr.Zero || _bits == IntPtr.Zero) return;
        var info = new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, _bits, info.RowBytes);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        draw(canvas, info);
        canvas.Flush();
        PresentRaw();
    }

    private void Present(SKColor fill)
    {
        Render((c, _) => c.Clear(fill));
    }

    private void PresentRaw()
    {
        // pptDst 是「分层窗的新屏幕位置」。传 (0,0) 会把窗口拽到屏幕左上角，
        // 必须回传窗口当前坐标，否则岛会跑到 (0,0)。
        var ppt = new Native.POINT(_x, _y);
        var psize = new Native.SIZE(_w, _h);
        var ppr = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        Native.UpdateLayeredWindow(_hwnd, _hdcScreen, ref ppt, ref psize, _hdcMem, ref ppr, 0, ref blend, Native.ULW_ALPHA);
    }

    public void Move(int x, int y, int? w = null, int? h = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        _x = x;
        _y = y;
        if (w is int nw && h is int nh && (nw != _w || nh != _h))
        {
            _w = nw;
            _h = nh;
            AllocBitmap(_w, _h);
        }
        // MoveWindow via SetWindowPos-like: use CreateWindow size through SetWindowPos
        // SWP_SHOWWINDOW 只在可见时带：否则自动隐藏期间任何 Move（改设置/换屏）都会把岛弹回来
        uint flags = SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : 0u);
        SetWindowPos(_hwnd, _topmost ? HWND_TOPMOST : HWND_NOTOPMOST, x, y, _w, _h, flags);
    }

    private bool _topmost = true;

    /// <summary>
    /// 岛设置等编辑窗打开期间把岛降到非置顶（SetUiYield）：否则岛每次 Move 都重申 TOPMOST，
    /// 会一直压在同样置顶的编辑窗上面——两边都是黑底，岛体把窗口标题栏和关闭按钮盖住，
    /// 表现就是「设置窗口关不了」。窗口全部关掉后恢复置顶。
    /// </summary>
    public void SetTopmost(bool on)
    {
        _topmost = on;
        if (_hwnd == IntPtr.Zero || _closed) return;
        SetWindowPos(_hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>岛当前是否可见（自动隐藏用）。</summary>
    public bool Visible => _visible;

    /// <summary>
    /// 显示 / 隐藏岛窗。隐藏后窗口收不到鼠标消息，恢复要靠上层轮询光标热区。
    /// SW_SHOWNOACTIVATE：只显示不抢焦点（岛是 NOACTIVATE 的置顶窗）。
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero || _closed || _visible == visible) return;
        _visible = visible;
        try { ShowWindow(_hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE); }
        catch { /* 显示/隐藏失败不影响绘制 */ }
    }

    private bool _visible = true;

    /// <summary>lParam/wParam 取低 32 位：IntPtr.ToInt32 在高位非零时会抛 OverflowException，
    /// 而原生消息语义就是截断（例如负屏幕坐标的符号扩展），所以必须用 unchecked。</summary>
    private static int Lo32(IntPtr value) => unchecked((int)(long)value);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new Native.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                        dwFlags = 2, // TME_LEAVE
                        hwndTrack = hWnd,
                        dwHoverTime = 0,
                    };
                    Native.TrackMouseEvent(ref tme);
                    _tracking = true;
                }
                MouseMove?.Invoke((short)(Lo32(lParam) & 0xFFFF), (short)((Lo32(lParam) >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case Native.WM_LBUTTONDOWN:
                MouseDown?.Invoke((short)(Lo32(lParam) & 0xFFFF), (short)((Lo32(lParam) >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case Native.WM_LBUTTONUP:
                MouseUp?.Invoke((short)(Lo32(lParam) & 0xFFFF), (short)((Lo32(lParam) >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case Native.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return IntPtr.Zero;
            case Native.WM_MOUSEWHEEL:
                // 坐标是屏幕坐标（分层窗没有客户区偏移概念），转成窗口内坐标
                MouseWheel?.Invoke(
                    (short)(Lo32(lParam) & 0xFFFF) - _x,
                    (short)((Lo32(lParam) >> 16) & 0xFFFF) - _y,
                    (short)((Lo32(wParam) >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case Native.WM_MOUSELEAVE:
                _tracking = false;
                MouseLeave?.Invoke();
                return IntPtr.Zero;
            case Native.WM_CLOSE:
                Native.KillTimer(hWnd, 1);
                FreeBitmap();
                _closed = true;
                _hwnd = IntPtr.Zero;
                Closed?.Invoke();
                // 只允许创建线程销毁窗口；跨线程调用会失败并泄漏 HWND
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                _closed = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void PumpOnce()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Native.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        // 只请求关闭：窗口与 DIB 统一由创建线程在 WM_CLOSE 中释放。
        // 跨线程直接 DestroyWindow/DeleteObject 会失败，且与渲染线程竞争。
        Close();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
