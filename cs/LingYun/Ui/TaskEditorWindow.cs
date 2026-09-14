using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LingYun.Config;

namespace LingYun.Ui;

/// <summary>
/// 日程编辑器：从岛上的「日程」页点行/点「＋」弹出（WPF 窗口，可获键盘焦点——
/// 岛本身是 NOACTIVATE 分层窗，无法接收打字）。保存经 ApplyTask 排进岛线程并写盘。
/// </summary>
public sealed class TaskEditorWindow : Window
{
    private static readonly string[] Categories = { "Work", "Health", "Personal", "Study", "Other" };
    private static readonly string[] Swatches =
    {
        "#FF5A5A", "#FFB020", "#35C759", "#00A0FF", "#7A5CFF", "#FF6BD6", "#00C8D7", "#8C8C8C",
    };

    private readonly NativeIslandApp _island;
    private readonly TaskItem? _original;
    private readonly TextBox _name = new();
    private readonly TextBox _time = new();
    private readonly Button _category = new();
    private readonly TextBlock _error = new();
    private readonly Button[] _swatches = new Button[Swatches.Length];
    private Brush _sub = Brushes.Gray;
    private int _categoryIndex;
    private string _color = Swatches[3];

    public TaskEditorWindow(AppConfig cfg, NativeIslandApp island, TaskItem? original)
    {
        _island = island;
        _original = original;
        bool dark = !string.Equals(cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);

        var bg = Brush(dark ? "#000000" : "#f5f6f8");
        var fg = Brush(dark ? "#ffffff" : "#18181a");
        var sub = Brush(dark ? "#8c8c8c" : "#5c5c66");
        _sub = sub;
        var card = Brush(dark ? "#2a2a2a" : "#e8eaee");
        var accent = Brush(dark ? "#60cdff" : "#0a7af0");

        Title = original is null ? "新建日程" : "编辑日程";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = bg;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        // 岛在屏幕顶部居中：编辑器放在岛正下方，避免盖住别的东西
        Left = SystemParameters.WorkArea.Left
               + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 96;

        var root = new StackPanel { Margin = new Thickness(1) };

        // 标题行 + 关闭
        var header = new Grid { Margin = new Thickness(18, 14, 14, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
        };
        Grid.SetColumn(title, 0);
        var close = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 24,
            Background = Brushes.Transparent,
            Foreground = sub,
            BorderThickness = new Thickness(0),
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);
        IslandPopupPlacer.EnableHeaderDrag(header, this);
        root.Children.Add(header);

        root.Children.Add(Field("名称", _name, fg, card, root));
        _name.MaxLength = 40;
        root.Children.Add(Field("时间", _time, fg, card, root));

        // 分类（点击轮换）
        _categoryIndex = Math.Max(0, Array.IndexOf(Categories, original?.Category ?? "Other"));
        _category.Content = "分类：" + Categories[_categoryIndex] + "（点击切换）";
        _category.Height = 30;
        _category.Cursor = Cursors.Hand;
        StyleButton(_category, fg, card);
        _category.Click += (_, _) =>
        {
            _categoryIndex = (_categoryIndex + 1) % Categories.Length;
            _category.Content = "分类：" + Categories[_categoryIndex] + "（点击切换）";
        };
        WithLabel(_category, root);

        // 颜色
        root.Children.Add(new TextBlock
        {
            Text = "颜色",
            FontSize = 12,
            Foreground = sub,
            Margin = new Thickness(18, 10, 0, 4),
        });
        var swatchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(18, 0, 0, 0),
        };
        for (int i = 0; i < Swatches.Length; i++)
        {
            var b = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(Swatches[i]),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = i,
            };
            b.Click += (_, _) => SelectColor(i);
            _swatches[i] = b;
            swatchRow.Children.Add(b);
        }
        root.Children.Add(swatchRow);

        // 错误提示
        _error.Foreground = Brush(dark ? "#ff6b6b" : "#cc2b2b");
        _error.FontSize = 12;
        _error.Margin = new Thickness(18, 8, 0, 0);
        root.Children.Add(_error);

        // 按钮行
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 18, 16),
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 84,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = fg,
            Background = card,
            Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => Close();
        var save = new Button
        {
            Content = "保存",
            Width = 84,
            Height = 32,
            Foreground = dark ? Brushes.Black : Brushes.White,
            Background = accent,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
        };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = new Border
        {
            BorderBrush = Brush(dark ? "#3a3a3a" : "#c8ccd4"),
            BorderThickness = new Thickness(1),
            Child = root,
        };
        IslandPopupPlacer.PlaceBelowIsland(this, SystemParameters.WorkArea.Top + 96, _island.ShellRect);

        // 回填既有值（放最后，控件都已建好）
        if (original is not null)
        {
            _name.Text = original.Name;
            _time.Text = original.Time == "N/A" ? "" : original.Time;
            SelectColor(Math.Max(0, Array.IndexOf(Swatches, NormalizeColor(original.Color))));
        }
        else
        {
            SelectColor(3);
        }
        _name.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Enter) Save();
        };
    }

    private void SelectColor(int index)
    {
        if (index < 0 || index >= Swatches.Length) index = 3;
        _color = Swatches[index];
        for (int i = 0; i < _swatches.Length; i++)
            _swatches[i].BorderBrush = i == index ? Brushes.White : Brushes.Transparent;
    }

    private void Save()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _error.Text = "名称不能为空";
            return;
        }
        var item = new TaskItem
        {
            Name = name,
            Category = Categories[_categoryIndex],
            Color = _color,
            Time = string.IsNullOrWhiteSpace(_time.Text) ? "N/A" : _time.Text.Trim(),
        };
        _island.ApplyTask(_original, item);
        Close();
    }

    private static string NormalizeColor(string hex)
    {
        hex = hex.Trim();
        if (hex.Length == 4 && hex[0] == '#')
            return $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
        return hex.Length == 7 && hex[0] == '#' ? hex.ToUpperInvariant() : Swatches[3];
    }

    private UIElement Field(string label, TextBox box, Brush fg, Brush card, StackPanel root)
    {
        box.Height = 30;
        box.Margin = new Thickness(0, 2, 0, 0);
        box.Background = card;
        box.Foreground = fg;
        box.CaretBrush = fg;
        box.BorderBrush = Brushes.Transparent;
        box.Padding = new Thickness(6, 2, 6, 0);
        return WithLabel(box, root, label);
    }

    private UIElement WithLabel(FrameworkElement control, StackPanel root, string? label = null)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 12, 18, 0) };
        if (label is not null)
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = _sub });
        panel.Children.Add(control);
        root.Children.Add(panel);
        return panel;
    }

    private static void StyleButton(Button b, Brush fg, Brush card)
    {
        b.Background = card;
        b.Foreground = fg;
        b.BorderThickness = new Thickness(0);
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        b.Padding = new Thickness(8, 0, 0, 0);
    }

    private static SolidColorBrush Brush(string hex) => new(
        (Color)ColorConverter.ConvertFromString(hex));
}
