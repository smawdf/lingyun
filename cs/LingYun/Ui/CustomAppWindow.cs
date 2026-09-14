using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LingYun.Config;

namespace LingYun.Ui;

/// <summary>
/// 快捷页「＋ 添加程序」弹出：管理自定义程序（名称 / 路径 / 圆钮颜色，最多 3 个）。
/// 编辑在本地副本上进行，「完成」一次性写回 quick_custom 配置并刷新快捷页。
/// </summary>
public sealed class CustomAppWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly NativeIslandApp _island;
    private readonly Action _save;
    private readonly List<Config.QuickCustomApp> _draft = new();
    private readonly StackPanel _list = new();
    private readonly TextBox _name = new(), _path = new();
    private string _color = "#38bdf8";
    private readonly Brush _sub, _fg, _card;
    private readonly bool _dark;

    private static readonly (string Hex, string Name)[] Colors =
    {
        ("#38bdf8", "蓝"), ("#4ade80", "绿"), ("#fbbf24", "黄"), ("#f87171", "红"), ("#a78bfa", "紫"),
    };

    public CustomAppWindow(AppConfig cfg, NativeIslandApp island, Action save)
    {
        _cfg = cfg;
        _island = island;
        _save = save;
        foreach (var a in cfg.QuickCustoms)
            _draft.Add(new Config.QuickCustomApp { Name = a.Name, Path = a.Path, Color = a.Color });
        _dark = !IslandPalette.ResolveLight(cfg.Theme, IslandPalette.SystemUsesLightTheme());
        var bg = Brush(_dark ? "#000000" : "#f5f6f8");
        _fg = Brush(_dark ? "#ffffff" : "#18181a");
        _sub = Brush(_dark ? "#8c8c8c" : "#5c5c66");
        _card = Brush(_dark ? "#2a2a2a" : "#e8eaee");
        var accent = Brush(_dark ? "#60cdff" : "#0a7af0");

        Title = "自定义快捷程序";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = bg;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 100;

        var root = new StackPanel { Margin = new Thickness(1) };
        var header = new TextBlock
        {
            Text = "自定义快捷程序", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = _fg, Margin = new Thickness(18, 16, 18, 0),
        };
        header.MouseLeftButtonDown += (_, _) => TryDragMove();
        root.Children.Add(header);
        root.Children.Add(new TextBlock
        {
            Text = "自定义程序出现在快捷页安全行，单击启动（最多 3 个）",
            FontSize = 10.5, Foreground = _sub, Margin = new Thickness(18, 4, 18, 0),
        });

        root.Children.Add(new TextBlock { Text = "已有程序", FontSize = 10.5, Foreground = _sub, Margin = new Thickness(18, 14, 18, 2) });
        root.Children.Add(_list);
        RefreshList();

        root.Children.Add(new TextBlock { Text = "添加新程序", FontSize = 10.5, Foreground = _sub, Margin = new Thickness(18, 12, 18, 2) });
        _name = new TextBox
        {
            Margin = new Thickness(18, 2, 18, 6),
            FontSize = 12.5,
            Foreground = _fg,
            Background = _card,
            BorderBrush = Brush(_dark ? "#3a3a3a" : "#c8ccd4"),
        };
        _name.TextChanged += (_, _) => UpdatePlaceholder(_name, "显示名称（如：网易云音乐）");
        UpdatePlaceholder(_name, "显示名称（如：网易云音乐）");
        root.Children.Add(_name);
        _path = new TextBox
        {
            Margin = new Thickness(18, 0, 18, 8),
            FontSize = 12.5,
            Foreground = _fg,
            Background = _card,
            BorderBrush = Brush(_dark ? "#3a3a3a" : "#c8ccd4"),
        };
        _path.TextChanged += (_, _) => UpdatePlaceholder(_path, "程序路径（如 C:\\Program Files\\Netease\\cloudmusic.exe）");
        UpdatePlaceholder(_path, "程序路径（如 C:\\Program Files\\Netease\\cloudmusic.exe）");
        root.Children.Add(_path);

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 18, 0) };
        colorRow.Children.Add(new TextBlock { Text = "颜色", FontSize = 11.5, Foreground = _sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        foreach (var (hex, cname) in Colors)
        {
            var rb = new RadioButton
            {
                GroupName = "cc",
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Content = new System.Windows.Shapes.Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = Brush(hex),
                    ToolTip = cname,
                },
            };
            if (hex == _color) rb.IsChecked = true;
            rb.Checked += (_, _) => _color = hex;
            colorRow.Children.Add(rb);
        }
        root.Children.Add(colorRow);

        var addBtn = new Button
        {
            Content = "＋ 添加到列表", Height = 30, Margin = new Thickness(18, 12, 18, 0),
            Foreground = _fg, Background = _card, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        addBtn.Click += (_, _) => AddCurrent();
        root.Children.Add(addBtn);

        var footer = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = new TextBlock { Text = "右键岛上的自定义卡也能移除（未实装时在此删除）", FontSize = 10, Foreground = _sub, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 0);
        var cancel = new Button
        {
            Content = "取消", Width = 76, Height = 30, Margin = new Thickness(0, 0, 10, 0),
            Foreground = _fg, Background = _card, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        var ok = new Button
        {
            Content = "完成", Width = 76, Height = 30,
            Foreground = _dark ? Brushes.Black : Brushes.White, Background = accent,
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        ok.Click += (_, _) =>
        {
            _cfg.QuickCustoms = _draft
                .Where(a => !string.IsNullOrWhiteSpace(a.Path))
                .Select(a => new Config.QuickCustomApp { Name = a.Name, Path = a.Path, Color = a.Color })
                .ToList();
            _island.ApplyConfig();
            _save();
            Close();
        };
        Grid.SetColumn(ok, 2);
        footer.Children.Add(hint);
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        root.Children.Add(footer);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 60,
            Content = root,
        };
        IslandPopupPlacer.PlaceBelowIsland(this, SystemParameters.WorkArea.Top + 100, _island.ShellRect);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        if (_draft.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "还没有自定义程序", FontSize = 11.5, Foreground = _sub, Margin = new Thickness(18, 2, 18, 4) });
            return;
        }
        for (int i = 0; i < _draft.Count; i++)
        {
            int idx = i;
            var row = new Grid { Margin = new Thickness(18, 3, 18, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Orientation = Orientation.Horizontal };
            info.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brush(_draft[i].Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{_draft[i].Name}  ·  {_draft[i].Path}",
                FontSize = 11.5,
                Foreground = _fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(info);
            var del = new Button
            {
                Content = "删除", FontSize = 11, Width = 46, Height = 22,
                Foreground = Brush(_dark ? "#ff6b6b" : "#cc2b2b"),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            del.Click += (_, _) => { _draft.RemoveAt(idx); RefreshList(); };
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
            _list.Children.Add(row);
        }
    }

    private void AddCurrent()
    {
        string name = ActualText(_name, "显示名称（如：网易云音乐）").Trim();
        string path = ActualText(_path, "程序路径（如 C:\\Program Files\\Netease\\cloudmusic.exe）").Trim();
        if (name.Length == 0 || path.Length == 0 || _draft.Count >= 3) return;
        _draft.Add(new Config.QuickCustomApp { Name = name, Path = path, Color = _color });
        _name.Clear();
        _path.Clear();
        RefreshList();
    }

    /// <summary>占位符：用 Tag 存原文，聚焦清空、失焦回填（避免引入额外依赖）。</summary>
    private static void UpdatePlaceholder(TextBox box, string ph)
    {
        box.Tag = ph;
        box.GotFocus -= PlaceholderFocus;
        box.LostFocus -= PlaceholderFocus;
        box.GotFocus += PlaceholderFocus;
        box.LostFocus += PlaceholderFocus;
        if (box.Text.Length == 0 && !box.IsFocused) box.Text = ph;
    }

    private static void PlaceholderFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string ph) return;
        if (box.Text == ph) box.Text = "";
        else if (box.Text.Length == 0) box.Text = ph;
    }

    private static string ActualText(TextBox box, string ph) => box.Text == ph ? "" : box.Text;

    private void TryDragMove()
    {
        try { DragMove(); } catch { /* 非拖拽态忽略 */ }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
