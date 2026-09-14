# -*- coding: utf-8 -*-
"""① ✕ 不再被涂上卡片底色（那个"框"）② 设置窗可拉伸（WM_NCHITTEST 边缘命中）
③ 亚克力档不画自绘包边（消除锯齿区域边与包边叠出的"两层"）。"""
import io

BASE = 'D:/kaifa/灵云/'
log = []


def patch(path, pairs):
    full = BASE + path
    s = io.open(full, encoding='utf-8').read()
    for old, new, why in pairs:
        hit = old in s
        log.append((path.split('/')[-1], why, hit))
        if hit:
            s = s.replace(old, new, 1)
    io.open(full, 'w', encoding='utf-8', newline='\n').write(s)


patch('cs/LingYun/Ui/SettingsWindow.cs', [
    # ① 图标按钮单独一列：不涂卡片底色、不描边（✕ 就是一枚字形）
    ('''    private readonly List<Button> _pushButtons = new();''',
     '''    private readonly List<Button> _pushButtons = new();
    /// <summary>纯图标按钮（✕）：不涂卡片底色、不描边——涂了就是一个"框"。</summary>
    private readonly List<Button> _bareButtons = new();''', 'bare list'),
    ('''        close.Click += (_, _) => Close();
        _pushButtons.Add(close);   // 不加入就会被默认模板接管——悬停时出现方形边框''',
     '''        close.Click += (_, _) => Close();
        _bareButtons.Add(close);   // 加入"裸按钮"：拿扁平模板但**不涂底色**，否则背后会有一块方框''', 'close bare'),
    ('''        foreach (var b in _pushButtons)
        {
            b.Foreground = _fg;
            b.Background = _card;
            b.BorderBrush = _line;
        }''',
     '''        foreach (var b in _pushButtons)
        {
            b.Foreground = _fg;
            b.Background = _card;
            b.BorderBrush = _line;
        }
        foreach (var b in _bareButtons)
        {
            b.Foreground = _sub;                 // ✕：只有字形，悬停只压暗
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
        }''', 'bare refresh'),
    ('''        foreach (var b in _pushButtons) b.Style = FlatButton(r);''',
     '''        foreach (var b in _pushButtons) b.Style = FlatButton(r);
        foreach (var b in _bareButtons) b.Style = FlatButton(r);''', 'bare style'),
    # ③ 亚克力档不画自绘包边（区域裁剪的边就是边）
    ('''        _edgeOverlay.BorderBrush = new SolidColorBrush(C(material == "classic"
            ? (_dark ? "#4a4a4a" : "#909090")
            : (_dark ? "#3dffffff" : "#38000000")));''',
     '''        // 亚克力档：形状由窗口区域裁出来，再画一条自绘边会和锯齿区域边叠成"两层"；
        // 玻璃档没有区域裁剪，靠这条边定义轮廓
        _edgeOverlay.BorderBrush = material == WindowMaterial.Acrylic
            ? Brushes.Transparent
            : new SolidColorBrush(C(_dark ? "#3dffffff" : "#38000000"));''', 'edge per material'),

    # ② 可拉伸：允许改尺寸 + 边缘命中（无边框窗口要自己回 WM_NCHITTEST）
    ('''        ResizeMode = ResizeMode.NoResize;''',
     '''        ResizeMode = ResizeMode.CanResize;      // 允许拉伸（无边框窗口靠下面的 WM_NCHITTEST 命中边缘）
        MinWidth = 620;
        MinHeight = 420;''', 'resizable'),
    ('''        SourceInitialized += (_, _) => ApplyMaterial();''',
     '''        SourceInitialized += (_, _) =>
        {
            ApplyMaterial();
            // 无边框窗口没有系统边框，边缘拉伸要自己回 WM_NCHITTEST
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
                src.AddHook(WndProc);
        };''', 'hook'),
    ('''    /// <summary>落到岛体下方（放不下由 placer 自己回退）。</summary>''',
     '''    private const int WM_NCHITTEST = 0x0084;
    private const int ResizeBorderDip = 6;

    /// <summary>
    /// 边缘/四角命中 → 交给系统做拉伸。WindowStyle=None 的无边框窗口默认收不到这些命中，
    /// 表现就是"设置页拉不动"。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || ResizeMode != ResizeMode.CanResize) return IntPtr.Zero;
        try
        {
            int x = unchecked((short)(long)lParam);
            int y = unchecked((short)((long)lParam >> 16));
            if (!Native.GetWindowRect(hwnd, out var r)) return IntPtr.Zero;
            int b = (int)Math.Round(ResizeBorderDip * (PresentationSource.FromVisual(this)
                is System.Windows.Interop.HwndSource s ? s.CompositionTarget.TransformToDevice.M11 : 1.0));
            bool left = x < r.Left + b, right = x >= r.Right - b;
            bool top = y < r.Top + b, bottom = y >= r.Bottom - b;
            int hit = left && top ? 13 : right && top ? 14 : left && bottom ? 16 : right && bottom ? 17
                : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
            if (hit == 0) return IntPtr.Zero;
            handled = true;
            return new IntPtr(hit);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>落到岛体下方（放不下由 placer 自己回退）。</summary>''', 'wndproc'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)
