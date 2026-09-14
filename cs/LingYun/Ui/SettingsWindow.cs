using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LingYun.Config;

namespace LingYun.Ui;

/// <summary>
/// 岛设置：外观（主题/不透明度/尺寸）、位置、显示内容（组合模式/通知/自动隐藏）、歌词（卡拉OK/延迟）。
/// 所有改动经 island.ApplyConfig()/ApplyGeometry() 实时预览（岛线程执行，不落盘）；
/// 关闭窗口时写盘一次——避免拖动滑杆期间每秒几十次原子写。
/// 更新检测未做（本机没有发布渠道），这里只显示本地版本号。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly NativeIslandApp _island;
    private readonly Action _save;
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
    private readonly CheckBox _composite = new();
    private readonly CheckBox _compositeClock = new();
    private readonly CheckBox _compositeHardware = new();
    private readonly CheckBox _compositeMedia = new();
    private readonly CheckBox _perfNetwork = new();
    private readonly RadioButton _themeDark = new();
    private readonly RadioButton _themeLight = new();
    private readonly RadioButton _themeSystem = new();
    private readonly RadioButton _themeLiquidGlass = new();
    private readonly CheckBox _glassAdaptive = new();
    private readonly RadioButton _styleA = new();
    private readonly RadioButton _styleB = new();
    private readonly RadioButton _styleC = new();
    private bool _ready;
    private readonly Brush _sub = Brushes.Gray;
    private readonly Brush _fg = Brushes.White;
    private readonly Brush _card = Brushes.DimGray;
    private readonly bool _dark;

    public SettingsWindow(AppConfig cfg, NativeIslandApp island, Action save)
    {
        _cfg = cfg;
        _island = island;
        _save = save;
        _dark = !IslandPalette.ResolveLight(cfg.Theme, IslandPalette.SystemUsesLightTheme());

        var bg = Brush(_dark ? "#000000" : "#f5f6f8");
        var fg = Brush(_dark ? "#ffffff" : "#18181a");
        var sub = Brush(_dark ? "#8c8c8c" : "#5c5c66");
        _sub = sub;
        _fg = fg;
        _card = Brush(_dark ? "#2a2a2a" : "#e8eaee");
        var accent = Brush(_dark ? "#60cdff" : "#0a7af0");

        Title = "岛设置";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = bg;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 60;

        var root = new StackPanel { Margin = new Thickness(1) };

        // 标题行 + 关闭
        var header = new Grid { Margin = new Thickness(18, 14, 14, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "岛设置", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fg };
        Grid.SetColumn(title, 0);
        var close = new Button
        {
            Content = "✕", Width = 28, Height = 24,
            Background = Brushes.Transparent, Foreground = sub, BorderThickness = new Thickness(0),
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);
        IslandPopupPlacer.EnableHeaderDrag(header, this);
        root.Children.Add(header);

        // ---- 外观 ----
        AddSection(root, "外观");
        AddRadioRow(root, "主题", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight),
            ("跟随系统", _themeSystem), ("液态玻璃", _themeLiquidGlass),
        });
        _themeDark.Checked += (_, _) => SetTheme("dark");
        _themeLight.Checked += (_, _) => SetTheme("light");
        _themeSystem.Checked += (_, _) => SetTheme("system");
        _themeLiquidGlass.Checked += (_, _) => SetTheme("liquid-glass");
        AddRadioRow(root, "媒体页", new[] { ("A · 精修", _styleA), ("B · 沉浸", _styleB), ("C · 氛围", _styleC) }, "mediastyle");
        _styleA.Checked += (_, _) => SetMediaStyle("a");
        _styleB.Checked += (_, _) => SetMediaStyle("b");
        _styleC.Checked += (_, _) => SetMediaStyle("c");
        AddCheck(root, _glassAdaptive, "液态玻璃自适应",
            "按岛背后的桌面明暗自动切换浅色玻璃（深字）/ 深色玻璃（白字），每秒采样一次（约 0.5% 单核）",
            v =>
            {
                _cfg.GlassAdaptive = v;
                _island.ApplyConfig();
            });
        AddOpacityPresets(root);

        // ---- 位置与大小 ----
        AddSection(root, "位置与大小");
        AddSlider(root, "胶囊大小", _compact, _compactLabel, v =>
        {
            _cfg.CompactScale = v / 100.0;
            _compactLabel.Text = $"  {v:0}%";
            _island.ApplyGeometry();
        });
        AddSlider(root, "展开大小", _expanded, _expandedLabel, v =>
        {
            _cfg.ExpandedScale = v / 100.0;
            _expandedLabel.Text = $"  {v:0}%";
            _island.ApplyGeometry();
        });
        AddSlider(root, "水平位置", _offsetX, _offsetXLabel, v =>
        {
            _cfg.OffsetX = (int)v;
            _offsetXLabel.Text = v == 0 ? "  居中" : v < 0 ? $"  左移 {-v:0}px" : $"  右移 {v:0}px";
            _island.ApplyGeometry();
        });
        AddSlider(root, "距顶部", _offsetY, _offsetYLabel, v =>
        {
            _cfg.OffsetY = (int)v;
            _offsetYLabel.Text = $"  {v:0}px";
            _island.ApplyGeometry();
        });

        // ---- 显示内容 ----
        AddSection(root, "显示内容");
        AddCheck(root, _composite, "组合模式",
            "胶囊里同时显示：时间 / 硬件 / 媒体，宽度随内容自动伸缩", v =>
            {
                _cfg.Composite = v;
                SyncCompositeEnabled();
                _island.ApplyConfig();
            });
        AddCheck(root, _compositeClock, "  时间", "显示当前时间（有计划时显示倒计时）", v =>
        {
            _cfg.CompositeClock = v;
            _island.ApplyConfig();
        }, indent: true);
        AddCheck(root, _compositeHardware, "  硬件占用", "CPU 与内存占用（每秒采样一次）", v =>
        {
            _cfg.CompositeHardware = v;
            _island.ApplyConfig();
        }, indent: true);
        AddCheck(root, _compositeMedia, "  媒体", "封面 + 歌词/标题 + 频谱（没有媒体会话时不显示）", v =>
        {
            _cfg.CompositeMedia = v;
            _island.ApplyConfig();
        }, indent: true);
        AddCheck(root, _perfNetwork, "性能页网速", "性能页显示实时下载 / 上传速度（每秒采样一次）", v =>
        {
            _cfg.PerfNetwork = v;
            _island.ApplyConfig();
        });
        AddCheck(root, new CheckBox(), "系统通知弹窗",
            "有通知时接管胶囊约 6 秒，点击唤醒对应应用（需在系统设置里允许通知访问）", v =>
            {
                _cfg.Toast = v;
                _island.ToastEnabled = v;
                _save();
            });
        AddCheck(root, new CheckBox(), "闲置自动隐藏",
            "无媒体且鼠标离开 10 秒后收起岛；光标移到屏幕顶部即可恢复", v =>
            {
                _cfg.AutoHide = v;
                _island.ApplyConfig();
            });

        // ---- 歌词 ----
        AddSection(root, "歌词");
        AddCheck(root, new CheckBox(), "显示歌词",
            "展开媒体页显示在线歌词（LRCLIB，需要联网）", v =>
            {
                _cfg.Lyrics = v;
                _island.LyricsEnabled = v;
                _save();
            });
        AddCheck(root, new CheckBox(), "卡拉OK逐字",
            "当前句按播放进度从左到右点亮", v =>
            {
                _cfg.LyricsKaraoke = v;
                _island.ApplyConfig();
            });
        AddSlider(root, "延迟补偿", _lyricDelay, _lyricDelayLabel, v =>
        {
            _cfg.LyricDelayMs = (int)v;
            _lyricDelayLabel.Text = v == 0 ? "  不补偿" : v > 0 ? $"  提前 {v / 1000.0:0.0}s" : $"  推后 {-v / 1000.0:0.0}s";
            _island.ApplyConfig();
        });

        // ---- 底部：版本 + 按钮 ----
        var footer = new Grid { Margin = new Thickness(18, 18, 18, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var version = new TextBlock
        {
            Text = "灵云 v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?") + "（本地）",
            FontSize = 11.5, Foreground = sub, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(version, 0);
        var reset = new Button
        {
            Content = "恢复默认", Width = 84, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Foreground = fg, Background = _card, Cursor = Cursors.Hand,
        };
        reset.Click += (_, _) => ResetAll();
        Grid.SetColumn(reset, 1);
        var done = new Button
        {
            Content = "完成", Width = 84, Height = 32,
            Foreground = _dark ? Brushes.Black : Brushes.White,
            Background = accent, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand,
        };
        done.Click += (_, _) => Close();
        Grid.SetColumn(done, 2);
        footer.Children.Add(version);
        footer.Children.Add(reset);
        footer.Children.Add(done);
        root.Children.Add(footer);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 40,
            Content = new Border
            {
                BorderBrush = Brush(_dark ? "#3a3a3a" : "#c8ccd4"),
                BorderThickness = new Thickness(1),
                Child = root,
            },
        };

        // 落位放到岛壳下方（原固定 Top 60 会和岛体重叠，岛压上来就点不到关闭按钮）；
        // 放不下（小屏）由 placer 回退原位
        IslandPopupPlacer.PlaceBelowIsland(this, SystemParameters.WorkArea.Top + 60, _island.ShellRect);

        // 回填当前值（放最后，Value 变更才会触发预览）
        Backfill(FindCheck("系统通知弹窗"), _cfg.Toast);
        Backfill(FindCheck("闲置自动隐藏"), _cfg.AutoHide);
        Backfill(FindCheck("显示歌词"), _cfg.Lyrics);
        Backfill(FindCheck("卡拉OK逐字"), _cfg.LyricsKaraoke);
        _perfNetwork.IsChecked = _cfg.PerfNetwork;
        _glassAdaptive.IsChecked = _cfg.GlassAdaptive;
        _composite.IsChecked = _cfg.Composite;
        _compositeClock.IsChecked = _cfg.CompositeClock;
        _compositeHardware.IsChecked = _cfg.CompositeHardware;
        _compositeMedia.IsChecked = _cfg.CompositeMedia;
        _themeDark.IsChecked = !IslandPalette.ResolveLight(_cfg.Theme, false) && _cfg.Theme != "system";
        _themeLight.IsChecked = string.Equals(_cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _themeSystem.IsChecked = string.Equals(_cfg.Theme, "system", StringComparison.OrdinalIgnoreCase);
        _themeLiquidGlass.IsChecked = IslandPalette.IsLiquidGlass(_cfg.Theme);
        _styleA.IsChecked = _cfg.MediaStyle != "b" && _cfg.MediaStyle != "c";
        _styleB.IsChecked = _cfg.MediaStyle == "b";
        _styleC.IsChecked = _cfg.MediaStyle == "c";
        SyncCompositeEnabled();
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
        _ready = true;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) =>
        {
            try { _save(); } catch { /* 写盘失败不致命 */ }
        };
    }

    private CheckBox? FindCheck(string label) => _checks.TryGetValue(label, out var c) ? c : null;
    private readonly Dictionary<string, CheckBox> _checks = new();

    private void SetTheme(string theme)
    {
        if (!_ready) return;
        _cfg.Theme = theme;
        _island.ApplyConfig();
    }

    /// <summary>媒体页样式（a 精修 / b 沉浸 / c 氛围）：改配置即实时预览，关闭窗口时随 Save 落盘。</summary>
    private void SetMediaStyle(string style)
    {
        if (!_ready) return;
        _cfg.MediaStyle = style;
        _island.ApplyConfig();
    }

    private void Backfill(CheckBox? box, bool value)
    {
        if (box is not null) box.IsChecked = value;
    }

    /// <summary>组合模式子项随总开关启用/禁用（关了总开关就不该能点模块）。</summary>
    private void SyncCompositeEnabled()
    {
        bool on = _composite.IsChecked == true;
        _compositeClock.IsEnabled = on;
        _compositeHardware.IsEnabled = on;
        _compositeMedia.IsEnabled = on;
    }

    private void ResetAll()
    {
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
        foreach (var (label, box) in _checks)
        {
            box.IsChecked = label is "系统通知弹窗" or "显示歌词" or "卡拉OK逐字" or "性能页网速";
        }
    }

    private void AddOpacityPresets(StackPanel root)
    {
        AddSlider(root, "背景透明度", _opacity, _opacityLabel, v =>
        {
            _cfg.Opacity = (int)v;
            _opacityLabel.Text = $"  {v:0}%";
            _island.ApplyConfig();
        });

        var row = new Grid { Margin = new Thickness(88, 2, 18, 0) };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, value) in new[]
        {
            ("轻透 40%", 40.0), ("半透 70%", 70.0), ("不透明 100%", 100.0),
        })
        {
            var button = new Button
            {
                Content = text,
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(strip.Children.Count == 0 ? 0 : 6, 0, 0, 0),
                Foreground = _fg,
                Background = _card,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = value,
            };
            button.Click += (_, _) => _opacity.Value = (double)button.Tag;
            strip.Children.Add(button);
        }
        row.Children.Add(strip);
        root.Children.Add(row);
        root.Children.Add(new TextBlock
        {
            Text = "液态玻璃也会跟随此设置；滑杆可调 40–100% 任意值",
            FontSize = 10.5,
            Foreground = _sub,
            Margin = new Thickness(88, 1, 18, 0),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void AddSection(StackPanel root, string text)
    {
        root.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11.5, Foreground = _sub,
            Margin = new Thickness(18, 16, 18, 2),
        });
    }

    private void AddRadioRow(StackPanel root, string label, (string Text, RadioButton Btn)[] options, string group = "theme")
    {
        var row = new Grid { Margin = new Thickness(18, 8, 18, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var lab = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = _sub,
            VerticalAlignment = VerticalAlignment.Center, Width = 60,
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
            btn.Margin = new Thickness(first ? 0 : 10, 0, 0, 0);
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
        box.Margin = new Thickness(indent ? 34 : 18, 8, 18, 0);
        box.Cursor = Cursors.Hand;
        box.Checked += (_, _) => { if (_ready) apply(true); };
        box.Unchecked += (_, _) => { if (_ready) apply(false); };
        root.Children.Add(box);
        root.Children.Add(new TextBlock
        {
            Text = hint, FontSize = 10.5, Foreground = _sub,
            Margin = new Thickness(indent ? 54 : 38, 1, 18, 0), TextWrapping = TextWrapping.Wrap,
        });
    }

    private void AddSlider(StackPanel root, string label, Slider slider, TextBlock valueLabel,
        Action<double> apply)
    {
        var row = new Grid { Margin = new Thickness(18, 12, 18, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lab = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = _sub,
            VerticalAlignment = VerticalAlignment.Center, Width = 70,
        };
        valueLabel.FontSize = 12;
        valueLabel.Foreground = _sub;
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueLabel.Width = 78;
        slider.Height = 22;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.IsSnapToTickEnabled = false;
        Grid.SetColumn(lab, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueLabel, 2);
        slider.ValueChanged += (_, e) =>
        {
            if (!_ready) return;
            valueLabel.Text = "";
            apply(e.NewValue);
        };
        row.Children.Add(lab);
        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        root.Children.Add(row);
    }

    private static Slider NewSlider(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        TickFrequency = 1,
        IsMoveToPointEnabled = true,
        Cursor = Cursors.Hand,
    };

    private static SolidColorBrush Brush(string hex) => new(
        (Color)ColorConverter.ConvertFromString(hex));
}
