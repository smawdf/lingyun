using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LingYun.Config;
using LingYun.Platform;

namespace LingYun.Ui;

/// <summary>
/// 设置「主页」：左侧分区导航 + 右侧内容，替代原来一路往下堆的长页面。
///
/// 分区：外观 / 位置与大小 / 显示内容 / 歌词 / 关于 —— 每区内的控件沿用原有实现
/// （滑杆实时预览走 island.ApplyConfig()/ApplyGeometry()，关闭窗口时写盘一次）。
///
/// 界面材质三档（设置窗口自身，岛的材质在「主题」里选）：
///   acrylic 亚克力：DWM 系统模糊（Win11 背景材质 / Win10 合成属性），半透明面板
///   glass   液态玻璃：亚克力 + 玻璃色调 + 大圆角。WPF 没有 backdrop-filter，
///                     网页里那种边缘折射搬不过来——这档是近似，不是真折射
///   classic 原生 Windows：纯色 + 方角 + 系统控件长相
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly NativeIslandApp _island;
    private readonly Action _save;

    // ---- 控件（沿用原有字段，避免功能倒退）----
    private readonly Slider _compact = NewSlider(80, 150);
    private readonly Slider _expanded = NewSlider(95, 125);
    private readonly Slider _offsetX = NewSlider(-280, 280);
    private readonly Slider _offsetY = NewSlider(0, 200);
    private readonly Slider _opacity = NewSlider(40, 100);
    private readonly Slider _lyricDelay = NewSlider(-2000, 2000);
    private readonly TextBlock _compactLabel = new();
    private readonly TextBlock _expandedLabel = new();
    private readonly TextBlock _offsetXLabel = new();
    private readonly TextBlock _offsetYLabel = new();
    private readonly TextBlock _opacityLabel = new();
    private readonly TextBlock _lyricDelayLabel = new();
    private readonly TextBlock _monitorLabel = new();
    private readonly CheckBox _composite = new();
    private readonly CheckBox _compositeClock = new();
    private readonly CheckBox _compositeHardware = new();
    private readonly CheckBox _compositeMedia = new();
    private readonly CheckBox _perfNetwork = new();
    private readonly CheckBox _glassAdaptive = new();
    private readonly CheckBox _autoStart = new();
    private readonly RadioButton _themeDark = new();
    private readonly RadioButton _themeLight = new();
    private readonly RadioButton _themeSystem = new();
    private readonly RadioButton _themeLiquidGlass = new();
    private readonly RadioButton _styleA = new();
    private readonly RadioButton _styleB = new();
    private readonly RadioButton _styleC = new();
    private readonly RadioButton _matAcrylic = new();
    private readonly RadioButton _matGlass = new();
    private readonly RadioButton _matClassic = new();
    private readonly TextBlock _materialHint = new();
    private readonly Dictionary<string, CheckBox> _checks = new();

    // ---- 材质化画刷：整个窗口共用这几个实例，换材质只改 Color，控件自动跟着变 ----
    private readonly SolidColorBrush _fg = new();
    private readonly SolidColorBrush _sub = new();
    private readonly SolidColorBrush _dim = new();
    private readonly SolidColorBrush _card = new();
    private readonly SolidColorBrush _line = new();
    private readonly SolidColorBrush _accent = new();
    private readonly SolidColorBrush _hover = new();
    private readonly Border _shell = new();
    private readonly StackPanel _nav = new();
    private readonly Grid _panes = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private bool _ready;
    private bool _dark;

    private const double WinW = 820, WinH = 580;
    private const double NavW = 196;

    public SettingsWindow(AppConfig cfg, NativeIslandApp island, Action save)
    {
        _cfg = cfg;
        _island = island;
        _save = save;

        Title = "灵云设置";
        Width = WinW;
        Height = WinH;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // 关键：窗口自身必须透明，DWM 的系统背景材质才有地方透出来
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 40;

        _shell.Margin = new Thickness(0);
        _shell.BorderThickness = new Thickness(1);
        _shell.Child = BuildLayout();
        Content = _shell;

        ApplyMaterial();     // 先定配色/圆角，控件再按它取色
        Backfill();
        _ready = true;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { try { _save(); } catch { /* 写盘失败不致命 */ } };
        // DWM 材质必须有 HWND 才能设，SourceInitialized 之后再应用一次
        SourceInitialized += (_, _) => ApplyMaterial();
        SizeChanged += (_, _) => PlaceBelowIsland();
        Loaded += (_, _) => PlaceBelowIsland();
    }

    /// <summary>落到岛体下方（放不下由 placer 自己回退）。</summary>
    private void PlaceBelowIsland()
    {
        try { IslandPopupPlacer.PlaceBelowIsland(this, SystemParameters.WorkArea.Top + 60, _island.ShellRect); }
        catch { /* 定位失败不影响使用 */ }
    }

    // ==================================================================
    // 布局
    // ==================================================================
    private FrameworkElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        root.RowDefinitions.Add(new RowDefinition());                              // 主体
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部

        root.Children.Add(BuildTitleBar());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavW) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _nav.Margin = new Thickness(10, 4, 10, 10);
        Grid.SetColumn(_nav, 0);
        body.Children.Add(_nav);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
        _panes.Margin = new Thickness(22, 6, 22, 22);
        scroll.Content = _panes;
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        AddSection("look", "◐", "外观", BuildLookPane());
        AddSection("layout", "▭", "位置与大小", BuildLayoutPane());
        AddSection("content", "☰", "显示内容", BuildContentPane());
        AddSection("lyrics", "♪", "歌词", BuildLyricsPane());
        AddSection("about", "ⓘ", "关于", BuildAboutPane());
        SelectSection("look");

        var footer = new Grid { Margin = new Thickness(22, 0, 22, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var version = new TextBlock
        {
            Text = "灵云 v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?"),
            FontSize = 11.5, Foreground = _dim, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(version, 0);
        var reset = NavStyleButton("恢复默认", 84);
        reset.Margin = new Thickness(0, 0, 10, 0);
        reset.Click += (_, _) => ResetAll();
        Grid.SetColumn(reset, 1);
        var done = NavStyleButton("完成", 84);
        done.FontWeight = FontWeights.SemiBold;
        done.Background = _accent;
        done.Foreground = Brushes.White;
        done.BorderThickness = new Thickness(0);
        done.Click += (_, _) => Close();
        Grid.SetColumn(done, 2);
        footer.Children.Add(version);
        footer.Children.Add(reset);
        footer.Children.Add(done);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private FrameworkElement BuildTitleBar()
    {
        var header = new Grid { Margin = new Thickness(22, 16, 16, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x60, 0xcd, 0xff), Color.FromRgb(0x0a, 0x7a, 0xf0), 45),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(logo);
        var title = new TextBlock
        {
            Text = "灵云设置", FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            Foreground = _fg, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var close = new Button
        {
            Content = "✕", Width = 30, Height = 26, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = _sub, Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        IslandPopupPlacer.EnableHeaderDrag(header, this);
        return header;
    }

    /// <summary>加一个分区：导航项 + 内容面板（只显示一个）。</summary>
    private void AddSection(string key, string icon, string label, FrameworkElement pane)
    {
        var nav = new Button
        {
            Content = $"{icon}   {label}",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 2),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = _sub,
            FontSize = 12.5,
            Cursor = Cursors.Hand,
            Tag = key,
        };
        nav.Click += (_, _) => SelectSection(key);
        _navButtons[key] = nav;
        _nav.Children.Add(nav);

        pane.Visibility = Visibility.Collapsed;
        _panes.Children.Add(pane);
    }

    private void SelectSection(string key)
    {
        foreach (var (k, btn) in _navButtons)
        {
            bool on = k == key;
            btn.Foreground = on ? _fg : _sub;
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Background = on ? _hover : Brushes.Transparent;
        }
        foreach (UIElement child in _panes.Children)
            child.Visibility = child is FrameworkElement fe && fe.Tag as string == key
                ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 各分区内容（设置项与原来一一对应）
    // ==================================================================
    private FrameworkElement BuildLookPane()
    {
        var root = NewPane("look", "外观", "决定设置窗口与岛的材质、配色和透明度。");

        AddGroupLabel(root, "设置窗口材质");
        var card = NewCard(root);
        AddRadioRow(card, "材质", new[]
        {
            ("亚克力", _matAcrylic), ("液态玻璃", _matGlass), ("原生 Windows", _matClassic),
        }, "uimaterial");
        _matAcrylic.Checked += (_, _) => SetMaterial("acrylic");
        _matGlass.Checked += (_, _) => SetMaterial("glass");
        _matClassic.Checked += (_, _) => SetMaterial("classic");
        _materialHint.FontSize = 11;
        _materialHint.Foreground = _dim;
        _materialHint.TextWrapping = TextWrapping.Wrap;
        _materialHint.Margin = new Thickness(88, 6, 14, 8);
        root.Children.Add(_materialHint);

        AddGroupLabel(root, "岛的主题");
        card = NewCard(root);
        AddRadioRow(card, "主题", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight),
            ("跟随系统", _themeSystem), ("液态玻璃", _themeLiquidGlass),
        });
        _themeDark.Checked += (_, _) => SetTheme("dark");
        _themeLight.Checked += (_, _) => SetTheme("light");
        _themeSystem.Checked += (_, _) => SetTheme("system");
        _themeLiquidGlass.Checked += (_, _) => SetTheme("liquid-glass");
        AddRadioRow(card, "媒体页", new[] { ("A · 精修", _styleA), ("B · 沉浸", _styleB), ("C · 氛围", _styleC) },
            "mediastyle");
        _styleA.Checked += (_, _) => SetMediaStyle("a");
        _styleB.Checked += (_, _) => SetMediaStyle("b");
        _styleC.Checked += (_, _) => SetMediaStyle("c");
        AddCheck(card, _glassAdaptive, "液态玻璃自适应",
            "按岛背后桌面明暗自动切浅色玻璃（深字）/ 深色玻璃（白字），每秒采样一次（约 0.5% 单核）",
            v => { _cfg.GlassAdaptive = v; _island.ApplyConfig(); });

        AddGroupLabel(root, "背景透明度");
        card = NewCard(root);
        AddSlider(card, "透明度", _opacity, _opacityLabel, v =>
        {
            _cfg.Opacity = (int)v;
            _opacityLabel.Text = $"  {v:0}%";
            _island.ApplyConfig();
        });
        AddOpacityPresets(card);
        AddHint(root, "只压背景与材质，文字和强调色不变；三档材质与液态玻璃都跟随。");
        return root;
    }

    private FrameworkElement BuildLayoutPane()
    {
        var root = NewPane("layout", "位置与大小", "改动即时生效，不需要重启。");
        var card = NewCard(root);
        AddSlider(card, "胶囊大小", _compact, _compactLabel, v =>
        {
            _cfg.CompactScale = v / 100.0;
            _compactLabel.Text = $"  {v:0}%";
            _island.ApplyGeometry();
        });
        AddSlider(card, "展开大小", _expanded, _expandedLabel, v =>
        {
            _cfg.ExpandedScale = v / 100.0;
            _expandedLabel.Text = $"  {v:0}%";
            _island.ApplyGeometry();
        });
        AddSlider(card, "水平位置", _offsetX, _offsetXLabel, v =>
        {
            _cfg.OffsetX = (int)v;
            _offsetXLabel.Text = v == 0 ? "  居中" : v < 0 ? $"  左移 {-v:0}px" : $"  右移 {v:0}px";
            _island.ApplyGeometry();
        });
        AddSlider(card, "距顶部", _offsetY, _offsetYLabel, v =>
        {
            _cfg.OffsetY = (int)v;
            _offsetYLabel.Text = $"  {v:0}px";
            _island.ApplyGeometry();
        });

        AddGroupLabel(root, "显示器");
        card = NewCard(root);
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _monitorLabel.FontSize = 12;
        _monitorLabel.Foreground = _sub;
        _monitorLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_monitorLabel, 0);
        var cycle = NavStyleButton("切换到下一块", 108);
        cycle.Click += (_, _) => { _island.CycleMonitor(); UpdateMonitorLabel(); };
        Grid.SetColumn(cycle, 1);
        row.Children.Add(_monitorLabel);
        row.Children.Add(cycle);
        card.Children.Add(row);
        AddHint(root, "换屏 / 改分辨率 / 任务栏变化后岛会自动重新落位。");
        return root;
    }

    private FrameworkElement BuildContentPane()
    {
        var root = NewPane("content", "显示内容", "紧凑胶囊里放什么、哪些页面出现。");
        var card = NewCard(root);
        AddCheck(card, _composite, "组合模式",
            "胶囊里同屏显示时间 / 硬件 / 媒体，宽度按内容自动伸缩；定宽槽保证数字跳动时不抖",
            v => { _cfg.Composite = v; SyncCompositeEnabled(); _island.ApplyConfig(); });
        AddCheck(card, _compositeClock, "　时间", "显示当前时间（有计划时显示倒计时）",
            v => { _cfg.CompositeClock = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeHardware, "　硬件占用", "CPU 与内存占用（每秒采样一次）",
            v => { _cfg.CompositeHardware = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeMedia, "　媒体", "封面 + 歌词/标题 + 频谱（没有媒体会话时不显示）",
            v => { _cfg.CompositeMedia = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _perfNetwork, "性能页网速", "性能页显示实时下载 / 上传速度（每秒采样一次）",
            v => { _cfg.PerfNetwork = v; _island.ApplyConfig(); });
        AddCheck(card, new CheckBox(), "系统通知弹窗",
            "有通知时接管胶囊约 6 秒，点击唤醒对应应用（需在系统设置里允许通知访问）",
            v => { _cfg.Toast = v; _island.ToastEnabled = v; _save(); });
        AddCheck(card, new CheckBox(), "闲置自动隐藏",
            "无媒体且鼠标离开 10 秒后收起岛；光标移到屏幕顶部即可恢复",
            v => { _cfg.AutoHide = v; _island.ApplyConfig(); });
        return root;
    }

    private FrameworkElement BuildLyricsPane()
    {
        var root = NewPane("lyrics", "歌词", "展开媒体页的在线歌词（来源 LRCLIB，免费无需鉴权）。");
        var card = NewCard(root);
        AddCheck(card, new CheckBox(), "显示歌词",
            "查不到 / 离线 / 接口异常都静默留空，不影响其他功能",
            v => { _cfg.Lyrics = v; _island.LyricsEnabled = v; _save(); });
        AddCheck(card, new CheckBox(), "卡拉OK逐字",
            "当前句按播放进度从左到右点亮；关掉则整句一个颜色",
            v => { _cfg.LyricsKaraoke = v; _island.ApplyConfig(); });
        AddSlider(card, "延迟补偿", _lyricDelay, _lyricDelayLabel, v =>
        {
            _cfg.LyricDelayMs = (int)v;
            _lyricDelayLabel.Text = v == 0 ? "  不补偿" : v > 0 ? $"  提前 {v / 1000.0:0.0}s" : $"  推后 {-v / 1000.0:0.0}s";
            _island.ApplyConfig();
        });
        return root;
    }

    private FrameworkElement BuildAboutPane()
    {
        var root = NewPane("about", "关于", "灵云 —— Windows 顶部灵动岛。");
        var card = NewCard(root);
        AddKeyValue(card, "版本", "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?"));
        AddKeyValue(card, "配置文件", "灵云配置.json（与 exe 同目录）");
        AddCheck(card, _autoStart, "开机自启", "写入 HKCU Run，登录后自动运行", v =>
        {
            try { AutoStart.Set(v); } catch { /* 注册表异常不致命 */ }
        });

        AddGroupLabel(root, "诊断");
        card = NewCard(root);
        AddKeyValue(card, "自测", "lingyun.exe --self-test（166 条契约断言）");
        AddKeyValue(card, "出图", "lingyun.exe --dump-frames（写 灵云-diag/*.png）");
        AddKeyValue(card, "自适应探针", "lingyun.exe --backdrop-probe（采的是背景还是岛自己）");
        AddHint(root, "恢复默认会把材质、主题、透明度、大小、位置、组合模式、通知、歌词全部还原（自定义快捷程序与日程保留）。");
        return root;
    }

    // ==================================================================
    // 材质与配色
    // ==================================================================
    private void SetMaterial(string material)
    {
        if (!_ready) return;
        _cfg.UiMaterial = material;
        ApplyMaterial();
    }

    /// <summary>
    /// 应用界面材质：改画刷颜色、圆角、以及窗口的系统背景材质。
    /// 画刷是共用实例，改 Color 就会即时反映到所有控件上（不必重建控件树）。
    /// </summary>
    private void ApplyMaterial()
    {
        string material = _cfg.UiMaterial;
        _dark = !IslandPalette.ResolveLight(_cfg.Theme, IslandPalette.SystemUsesLightTheme());

        switch (material)
        {
            case "classic":
                _fg.Color = C(_dark ? "#f2f4f8" : "#0a0a0a");
                _sub.Color = C(_dark ? "#c8c8c8" : "#3c3c3c");
                _dim.Color = C(_dark ? "#9a9a9a" : "#6a6a6a");
                _card.Color = C(_dark ? "#2b2b2b" : "#ffffff");
                _line.Color = C(_dark ? "#4a4a4a" : "#a0a0a0");
                _accent.Color = C(_dark ? "#4cc2ff" : "#0078d4");
                _hover.Color = C(_dark ? "#3a3a3a" : "#e5e5e5");
                _shell.CornerRadius = new CornerRadius(0);
                _shell.Background = new SolidColorBrush(C(_dark ? "#202020" : "#f0f0f0"));
                _shell.BorderBrush = new SolidColorBrush(C(_dark ? "#4a4a4a" : "#909090"));
                break;
            case "glass":
                _fg.Color = C(_dark ? "#f6f8fc" : "#101317");
                _sub.Color = C(_dark ? "#c9cfd8" : "#3f444c");
                _dim.Color = C(_dark ? "#98a0ab" : "#5b6069");
                _card.Color = C(_dark ? "#1affffff" : "#2effffff");
                _line.Color = C(_dark ? "#2effffff" : "#3a000000");
                _accent.Color = C(_dark ? "#60cdff" : "#0a72dc");
                _hover.Color = C(_dark ? "#26ffffff" : "#1a0a72dc");
                _shell.CornerRadius = new CornerRadius(20);
                _shell.Background = _dark
                    ? new LinearGradientBrush(C("#b812141a"), C("#cc0a0b0e"), 90)
                    : new LinearGradientBrush(C("#c7ffffff"), C("#aef4f7fc"), 90);
                _shell.BorderBrush = new SolidColorBrush(C(_dark ? "#2effffff" : "#29ffffff"));
                break;
            default:   // acrylic
                _fg.Color = C(_dark ? "#f2f4f8" : "#16181c");
                _sub.Color = C(_dark ? "#b6bcc6" : "#4c5058");
                _dim.Color = C(_dark ? "#8b919b" : "#6a6f78");
                _card.Color = C(_dark ? "#1fffffff" : "#3dffffff");
                _line.Color = C(_dark ? "#26ffffff" : "#22000000");
                _accent.Color = C(_dark ? "#60cdff" : "#0a7af0");
                _hover.Color = C(_dark ? "#1fffffff" : "#14000000");
                _shell.CornerRadius = new CornerRadius(10);
                // 面板要够厚：系统模糊只让 15% 背景透上来，否则背后是深色窗口时
                // 面板会变灰、深色文字对比度不稳（和岛那边"浅色材质必须够厚"是同一条结论）
                _shell.Background = new SolidColorBrush(C(_dark ? "#d91b1c20" : "#d9f2f4f8"));
                _shell.BorderBrush = new SolidColorBrush(C(_dark ? "#21ffffff" : "#24000000"));
                break;
        }

        if (_ready || IsInitialized)
        {
            string effective = WindowMaterial.Apply(this, material, _dark);
            _materialHint.Text = material switch
            {
                "classic" => "纯色面板 + 方角 + 系统控件长相；最清晰、最省资源。",
                "glass" => "亚克力 + 玻璃色调 + 大圆角。"
                           + (effective == "solid" ? "（当前系统不支持系统模糊，退化为纯色）" : "系统模糊已生效。")
                           + "注意：WPF 窗口做不了边缘折射，这一档是近似；真折射只在岛那边。",
                _ => "Win11 式系统模糊 + 半透明面板。"
                     + (effective == "solid" ? "（当前系统不支持，退化为纯色）" : "系统模糊已生效。"),
            };
        }
        ApplyNavSelectionLook();
    }

    /// <summary>换材质后刷新导航选中态配色（选中态用的是 _hover/_fg 实例颜色）。</summary>
    private void ApplyNavSelectionLook()
    {
        foreach (var (key, btn) in _navButtons)
        {
            bool on = btn.FontWeight == FontWeights.SemiBold;
            btn.Foreground = on ? _fg : _sub;
            btn.Background = on ? _hover : Brushes.Transparent;
            if (btn.Tag is string _) { }
        }
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // ==================================================================
    // 回填 / 交互
    // ==================================================================
    private void Backfill()
    {
        _matAcrylic.IsChecked = _cfg.UiMaterial == "acrylic";
        _matGlass.IsChecked = _cfg.UiMaterial == "glass";
        _matClassic.IsChecked = _cfg.UiMaterial == "classic";

        _themeDark.IsChecked = !IslandPalette.ResolveLight(_cfg.Theme, false) && _cfg.Theme != "system";
        _themeLight.IsChecked = string.Equals(_cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _themeSystem.IsChecked = string.Equals(_cfg.Theme, "system", StringComparison.OrdinalIgnoreCase);
        _themeLiquidGlass.IsChecked = IslandPalette.IsLiquidGlass(_cfg.Theme);
        _styleA.IsChecked = _cfg.MediaStyle != "b" && _cfg.MediaStyle != "c";
        _styleB.IsChecked = _cfg.MediaStyle == "b";
        _styleC.IsChecked = _cfg.MediaStyle == "c";
        _glassAdaptive.IsChecked = _cfg.GlassAdaptive;

        _opacity.Value = _cfg.Opacity;
        _opacityLabel.Text = $"  {_cfg.Opacity}%";
        _lyricDelay.Value = _cfg.LyricDelayMs;
        _lyricDelayLabel.Text = _cfg.LyricDelayMs == 0 ? "  不补偿"
            : _cfg.LyricDelayMs > 0 ? $"  提前 {_cfg.LyricDelayMs / 1000.0:0.0}s"
            : $"  推后 {-_cfg.LyricDelayMs / 1000.0:0.0}s";
        _compact.Value = (int)Math.Round(_cfg.CompactScale * 100);
        _expanded.Value = (int)Math.Round(_cfg.ExpandedScale * 100);
        _offsetX.Value = _cfg.OffsetX;
        _offsetY.Value = _cfg.OffsetY;

        _perfNetwork.IsChecked = _cfg.PerfNetwork;
        _composite.IsChecked = _cfg.Composite;
        _compositeClock.IsChecked = _cfg.CompositeClock;
        _compositeHardware.IsChecked = _cfg.CompositeHardware;
        _compositeMedia.IsChecked = _cfg.CompositeMedia;
        Backfill(FindCheck("系统通知弹窗"), _cfg.Toast);
        Backfill(FindCheck("闲置自动隐藏"), _cfg.AutoHide);
        Backfill(FindCheck("显示歌词"), _cfg.Lyrics);
        Backfill(FindCheck("卡拉OK逐字"), _cfg.LyricsKaraoke);
        _autoStart.IsChecked = AutoStart.IsEnabled();
        SyncCompositeEnabled();
        UpdateMonitorLabel();
    }

    private void UpdateMonitorLabel()
    {
        int count = _island.MonitorCount;
        int current = _cfg.MonitorIndex < 0 ? 0 : _cfg.MonitorIndex;
        _monitorLabel.Text = count <= 1
            ? "只有一块显示器"
            : $"当前：第 {current + 1} 块 / 共 {count} 块";
    }

    private void SetTheme(string theme)
    {
        if (!_ready) return;
        _cfg.Theme = theme;
        _island.ApplyConfig();
        ApplyMaterial();   // 深/浅变了，设置窗口自己的配色也跟着走
    }

    private void SetMediaStyle(string style)
    {
        if (!_ready) return;
        _cfg.MediaStyle = style;
        _island.ApplyConfig();
    }

    private CheckBox? FindCheck(string label) => _checks.TryGetValue(label, out var c) ? c : null;
    private void Backfill(CheckBox? box, bool value) { if (box is not null) box.IsChecked = value; }

    private void SyncCompositeEnabled()
    {
        bool on = _composite.IsChecked == true;
        _compositeClock.IsEnabled = on;
        _compositeHardware.IsEnabled = on;
        _compositeMedia.IsEnabled = on;
    }

    private void ResetAll()
    {
        _matAcrylic.IsChecked = true;
        _themeDark.IsChecked = true;
        _styleA.IsChecked = true;
        _compact.Value = 100;
        _expanded.Value = 100;
        _offsetX.Value = 0;
        _offsetY.Value = 8;
        _opacity.Value = 100;
        _lyricDelay.Value = 0;
        _composite.IsChecked = false;
        _compositeClock.IsChecked = true;
        _compositeHardware.IsChecked = true;
        _compositeMedia.IsChecked = true;
        _perfNetwork.IsChecked = true;
        _glassAdaptive.IsChecked = true;
        foreach (var (label, box) in _checks)
            box.IsChecked = label is "系统通知弹窗" or "显示歌词" or "卡拉OK逐字" or "性能页网速";
    }

    // ==================================================================
    // 小控件
    // ==================================================================
    private StackPanel NewPane(string key, string title, string subtitle)
    {
        var root = new StackPanel { Tag = key };
        root.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = _fg,
        });
        root.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 11, Foreground = _dim, Margin = new Thickness(0, 3, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        });
        return root;
    }

    private void AddGroupLabel(StackPanel root, string text)
        => root.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, Foreground = _dim, Margin = new Thickness(0, 6, 0, 6),
        });

    private void AddHint(StackPanel root, string text)
        => root.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, Foreground = _dim, Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

    /// <summary>一个圆角卡片：设置项都往它里面加。</summary>
    private StackPanel NewCard(StackPanel root)
    {
        var inner = new StackPanel();
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 2, 14, 2),
            Margin = new Thickness(0, 0, 0, 10),
            Background = _card,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Child = inner,
        };
        root.Children.Add(border);
        return inner;
    }

    private void AddKeyValue(StackPanel root, string key, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = key, FontSize = 12, Foreground = _fg });
        var v = new TextBlock
        {
            Text = value, FontSize = 11.5, Foreground = _dim,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        root.Children.Add(grid);
    }

    private Button NavStyleButton(string text, double width) => new()
    {
        Content = text, Width = width, Height = 32,
        FontSize = 12.5, Cursor = Cursors.Hand,
        Foreground = _fg, Background = _card, BorderBrush = _line, BorderThickness = new Thickness(1),
    };

    private void AddOpacityPresets(StackPanel root)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var (text, value) in new[]
        {
            ("轻透 40%", 40.0), ("半透 70%", 70.0), ("不透明 100%", 100.0),
        })
        {
            var button = NavStyleButton(text, double.NaN);
            button.Padding = new Thickness(10, 0, 10, 0);
            button.MinWidth = 0;
            button.Margin = new Thickness(strip.Children.Count == 0 ? 0 : 6, 0, 0, 0);
            button.Tag = value;
            button.Click += (_, _) => _opacity.Value = (double)button.Tag;
            strip.Children.Add(button);
        }
        root.Children.Add(strip);
    }

    private void AddRadioRow(StackPanel root, string label, (string Text, RadioButton Btn)[] options,
        string group = "theme")
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var lab = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = _sub,
            VerticalAlignment = VerticalAlignment.Center, Width = 74,
        };
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        bool first = true;
        foreach (var (text, btn) in options)
        {
            btn.Content = text;
            btn.GroupName = group;
            btn.Foreground = _fg;
            btn.FontSize = 12;
            btn.Margin = new Thickness(first ? 0 : 12, 0, 0, 0);
            btn.Cursor = Cursors.Hand;
            strip.Children.Add(btn);
            first = false;
        }
        Grid.SetColumn(strip, 1);
        row.Children.Add(strip);
        root.Children.Add(row);
    }

    private void AddCheck(StackPanel root, CheckBox box, string label, string hint,
        Action<bool> apply, bool indent = false)
    {
        _checks[label.Trim()] = box;
        box.Content = label;
        box.Foreground = _fg;
        box.FontSize = 12.5;
        box.Margin = new Thickness(indent ? 18 : 0, 8, 0, 0);
        box.Cursor = Cursors.Hand;
        box.Checked += (_, _) => { if (_ready) apply(true); };
        box.Unchecked += (_, _) => { if (_ready) apply(false); };
        root.Children.Add(box);
        var h = new TextBlock
        {
            Text = hint, FontSize = 11, Foreground = _dim, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent ? 18 : 0, 2, 0, 8),
        };
        root.Children.Add(h);
    }

    private void AddSlider(StackPanel root, string label, Slider slider, TextBlock valueLabel,
        Action<double> apply)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lab = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = _sub, Width = 74,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lab, 0);
        grid.Children.Add(lab);
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        valueLabel.FontSize = 11.5;
        valueLabel.Foreground = _sub;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.MinWidth = 60;
        valueLabel.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(valueLabel, 2);
        grid.Children.Add(valueLabel);
        slider.ValueChanged += (_, e) => { if (_ready) apply(e.NewValue); };
        root.Children.Add(grid);
    }

    private static Slider NewSlider(double min, double max) => new()
    {
        Minimum = min, Maximum = max, SmallChange = 1, LargeChange = 10,
    };
}
