using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LingYun.Config;

namespace LingYun.Ui;

/// <summary>
/// 计划页点「执行时刻」卡弹出：小时/分钟步进编辑（分钟按 5 步进）。
/// 样式与岛同语言（同字号/强调色）；保存时写配置 + 实时预览 + 落盘。
/// </summary>
public sealed class PlanTimeWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly NativeIslandApp _island;
    private readonly Action _save;
    private int _hour, _minute;
    private readonly TextBlock _hourV = new(), _minuteV = new();

    public PlanTimeWindow(AppConfig cfg, NativeIslandApp island, Action save)
    {
        _cfg = cfg;
        _island = island;
        _save = save;
        _hour = cfg.Hour;
        _minute = cfg.Minute;
        bool dark = !IslandPalette.ResolveLight(cfg.Theme, IslandPalette.SystemUsesLightTheme());
        var bg = Brush(dark ? "#000000" : "#f5f6f8");
        var fg = Brush(dark ? "#ffffff" : "#18181a");
        var sub = Brush(dark ? "#8c8c8c" : "#5c5c66");
        var card = Brush(dark ? "#2a2a2a" : "#e8eaee");
        var accent = Brush(dark ? "#60cdff" : "#0a7af0");

        Title = "执行时刻";
        Width = 296;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = bg;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 140;

        var root = new StackPanel { Margin = new Thickness(1) };
        var header = new TextBlock
        {
            Text = "执行时刻", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = fg, Margin = new Thickness(18, 16, 18, 0),
        };
        header.MouseLeftButtonDown += (_, _) => TryDragMove();
        root.Children.Add(header);

        var row = new Grid { Margin = new Thickness(18, 12, 18, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var hourCol = MakeStepper("时", fg, sub, card,
            () => _hour, v => _hour = (v % 24 + 24) % 24, 1, _hourV);
        var minCol = MakeStepper("分", fg, sub, card,
            () => _minute, v => _minute = (v / 5 * 5 + 60) % 60, 5, _minuteV);
        row.Children.Add(hourCol);
        Grid.SetColumn(hourCol, 0);
        row.Children.Add(minCol);
        Grid.SetColumn(minCol, 2);
        root.Children.Add(row);
        Sync();

        var footer = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = new TextBlock
        {
            Text = "分钟按 5 分钟步进", FontSize = 10.5, Foreground = sub,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(hint, 0);
        var cancel = new Button
        {
            Content = "取消", Width = 76, Height = 30, Margin = new Thickness(0, 0, 10, 0),
            Foreground = fg, Background = card, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        var ok = new Button
        {
            Content = "保存", Width = 76, Height = 30,
            Foreground = dark ? Brushes.Black : Brushes.White, Background = accent,
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        ok.Click += (_, _) =>
        {
            _cfg.Hour = _hour;
            _cfg.Minute = _minute;
            _island.ApplyConfig();
            _save();
            Close();
        };
        Grid.SetColumn(ok, 2);
        footer.Children.Add(hint);
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        root.Children.Add(footer);

        Content = root;
        IslandPopupPlacer.PlaceBelowIsland(this, SystemParameters.WorkArea.Top + 140, _island.ShellRect);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private StackPanel MakeStepper(string label, Brush fg, Brush sub, Brush card,
        Func<int> get, Action<int> set, int step, TextBlock value)
    {
        var sp = new StackPanel();
        var up = new Button
        {
            Content = "▲", Width = 72, Height = 26, Foreground = fg,
            Background = card, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        up.Click += (_, _) => { set(get() + step); Sync(); };
        value.FontSize = 32;
        value.FontWeight = FontWeights.SemiBold;
        value.Foreground = fg;
        value.TextAlignment = TextAlignment.Center;
        value.Margin = new Thickness(0, 6, 0, 6);
        var down = new Button
        {
            Content = "▼", Width = 72, Height = 26, Foreground = fg,
            Background = card, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        down.Click += (_, _) => { set(get() - step + (label == "分" ? 60 : 24)); Sync(); };
        var lab = new TextBlock
        {
            Text = label, FontSize = 11, Foreground = sub,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        sp.Children.Add(up);
        sp.Children.Add(value);
        sp.Children.Add(down);
        sp.Children.Add(lab);
        return sp;
    }

    private void Sync()
    {
        _hourV.Text = _hour.ToString("00");
        _minuteV.Text = _minute.ToString("00");
    }

    private void TryDragMove()
    {
        try { DragMove(); } catch { /* 非拖拽态忽略 */ }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
