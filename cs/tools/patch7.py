# -*- coding: utf-8 -*-
"""四条：① 扁平按钮清掉焦点虚线框（那个"正方形"）② 滑杆支持点击直达
③ 完成按钮补框线 ④ 自适应换色更精细（中值平滑 + 连续两次一致才翻）。"""
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
    # ① 扁平按钮 / 药丸 / 开关：清掉默认焦点虚线框（悬停或点击后那圈"正方形"就是它）
    ('''        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    /// <summary>单选"药丸"：圆角边框 + 文字；选中/未选中的颜色由 RefreshStates 统一上。</summary>''',
     '''        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        // 默认按钮还会画一圈"焦点虚线框"（点过之后一直留着），自定义模板后它非常突兀
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        return style;
    }

    /// <summary>单选"药丸"：圆角边框 + 文字；选中/未选中的颜色由 RefreshStates 统一上。</summary>''', 'flat focus'),
    ('''        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    /// <summary>
    /// 开关：轨道 + 圆钮。选中时圆钮右移、轨道染成强调色。''',
     '''        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        return style;
    }

    /// <summary>
    /// 开关：轨道 + 圆钮。选中时圆钮右移、轨道染成强调色。''', 'pill focus'),

    # ② 滑杆：点击轨道直接跳到该位置（WPF 默认是"按 LargeChange 加减固定值"）
    ('''    private static Slider NewSlider(double min, double max) => new()
    {
        Minimum = min, Maximum = max, SmallChange = 1, LargeChange = 10,
    };''',
     '''    private static Slider NewSlider(double min, double max) => new()
    {
        Minimum = min, Maximum = max, SmallChange = 1, LargeChange = 10,
        // 点哪到哪（WPF 默认点轨道是"加减一个固定步长"，用户反馈"点一下直接缩放固定值"）
        IsMoveToPointEnabled = true,
    };''', 'slider move-to-point'),

    # ③ 完成按钮补框线
    ('''        done.FontWeight = FontWeights.SemiBold;
        done.Background = _accent;
        done.Foreground = Brushes.White;
        done.BorderThickness = new Thickness(0);''',
     '''        done.FontWeight = FontWeights.SemiBold;
        done.Background = _accent;
        done.Foreground = Brushes.White;
        done.BorderThickness = new Thickness(1);     // 用户要求：完成按钮要有框线
        done.BorderBrush = _accentEdge;''', 'done border'),
])

patch('cs/LingYun/Ui/NativeIslandApp.cs', [
    # ④ 自适应换色：中值平滑 + 连续两次一致才翻
    ('''        _backdropLum = s.Luminance;
        // 最亮分区代表"最不利的区域"：平均亮度会被大片暗色稀释，只看平均会漏掉半明半暗的壁纸
        bool dark = IslandPalette.PreferDarkGlass(
            new SKColor(s.R, s.G, s.B), _cfg.Opacity, _glassDark, out _glassContrast);
        if (s.BrightestCell > 0.82 && _cfg.Opacity < 70) dark = false;   // 有很亮的区域且玻璃薄：浅材质更稳
        if (dark != _glassDark)
        {
            _glassDark = dark;
            _palCache = null;                                    // 立刻换色，不等缓存过期
        }
    }''',
     '''        _backdropLum = s.Luminance;
        // 精细一点：单帧亮度会抖动（滚动网页、播放视频），先取最近三次的**中值**再判断
        _lumHist[_lumHistIdx % 3] = s.Luminance;
        _lumHistIdx++;
        double smooth = Median3(_lumHist[0], _lumHist[1], _lumHist[2]);
        _backdropLum = smooth;
        var mean = new SKColor(s.R, s.G, s.B);
        bool dark = IslandPalette.PreferDarkGlass(mean, _cfg.Opacity, _glassDark, out _glassContrast);
        if (s.BrightestCell > 0.82 && _cfg.Opacity < 70) dark = false;   // 有很亮的区域且玻璃薄：浅材质更稳

        // 要连续两次得出同一结论才真的换（迟滞之外再加一层驻留，避免临界处闪烁）
        if (dark == _glassDark)
        {
            _glassPendingCount = 0;                      // 已经一致，撤销待定
        }
        else if (dark == _glassPending)
        {
            _glassPendingCount++;
            if (_glassPendingCount >= 2)
            {
                _glassDark = dark;
                _glassPendingCount = 0;
                _palCache = null;                        // 立刻换色，不等缓存过期
            }
        }
        else
        {
            _glassPending = dark;                        // 第一次出现相反结论：先记下
            _glassPendingCount = 1;
        }
    }

    /// <summary>三次采样的中值（去掉单帧尖峰）。纯函数，自测钉住。</summary>
    internal static double Median3(double a, double b, double c)
        => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>
    /// 自适应换色的驻留判定（纯函数，自测用）：结论与当前不同、且连续 need 次都一样才换。
    /// 返回 (立刻切换?, 新的待定值, 新的计数)。
    /// </summary>
    internal static (bool Apply, bool Pending, int Count) GlassFlipStep(
        bool want, bool current, bool pending, int count, int need = 2)
    {
        if (want == current) return (false, pending, 0);
        if (want == pending)
        {
            int next = count + 1;
            return (next >= need, want, next >= need ? 0 : next);
        }
        return (false, want, 1);
    }''', 'adaptive smooth'),
    ('''    private double _backdropLum = double.NaN;''',
     '''    private double _backdropLum = double.NaN;
    private readonly double[] _lumHist = { double.NaN, double.NaN, double.NaN };
    private int _lumHistIdx;
    private bool _glassPending;
    private int _glassPendingCount;''', 'adaptive fields'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)
