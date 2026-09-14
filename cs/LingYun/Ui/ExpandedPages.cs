using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LingYun.Config;
using LingYun.Services;

namespace LingYun.Ui;

/// <summary>展开态功能页：计划 / 性能 / 天气 / 事项 / 月份 + 顶栏导航。</summary>
public sealed class ExpandedPages : StackPanel
{
    public static readonly string[] Order = { "计划", "性能", "天气", "事项", "月份" };

    private readonly Dictionary<string, UIElement> _pages = new();
    private readonly StackPanel _nav = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 0) };
    private readonly Dictionary<string, Button> _navBtns = new();
    private string _active = "计划";

    private readonly AppConfig _cfg;
    private readonly Action _save;
    private readonly PerfSampler _perf;
    private readonly WeatherService _weather;

    private readonly TextBlock _perfText;
    private readonly TextBlock _weatherText;
    private readonly TextBlock _weatherErr;
    private readonly StackPanel _taskList;
    private readonly TextBlock _monthText;
    private readonly Grid _calGrid;
    private readonly HashSet<string> _selectedDates = new();

    public ExpandedPages(AppConfig cfg, Action save, PerfSampler perf, WeatherService weather)
    {
        _cfg = cfg;
        _save = save;
        _perf = perf;
        _weather = weather;
        Margin = new Thickness(16, 8, 16, 8);

        foreach (var name in Order)
        {
            var b = new Button
            {
                Content = name,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Tag = name,
            };
            b.Click += (_, _) => Show(name);
            _navBtns[name] = b;
            _nav.Children.Add(b);
        }
        Children.Add(_nav);

        // 计划
        Children.Add(BuildPlanPage());

        // 性能
        _perfText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(8, 12, 8, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = "采样中…",
        };
        Children.Add(Wrap("性能", _perfText));

        // 天气
        _weatherText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 8, 8, 4),
            Text = "获取中…",
        };
        _weatherErr = new TextBlock
        {
            Foreground = Brushes.Orange,
            FontSize = 11,
            Margin = new Thickness(8, 0, 8, 4),
        };
        var cityBox = new TextBox { Width = 160, Margin = new Thickness(8, 4, 4, 8) };
        var cityBtn = new Button { Content = "更新城市", Margin = new Thickness(0, 4, 8, 8) };
        cityBtn.Click += (_, _) =>
        {
            _cfg.Location = cityBox.Text.Trim();
            _save();
            _ = _weather.RefreshAsync();
        };
        var cityRow = new StackPanel { Orientation = Orientation.Horizontal };
        cityRow.Children.Add(cityBox);
        cityRow.Children.Add(cityBtn);
        var weatherCol = new StackPanel();
        weatherCol.Children.Add(_weatherText);
        weatherCol.Children.Add(_weatherErr);
        weatherCol.Children.Add(cityRow);
        Children.Add(Wrap("天气", weatherCol));

        // 事项
        _taskList = new StackPanel { Margin = new Thickness(8, 8, 8, 8) };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var nameBox = new TextBox { Width = 140, Margin = new Thickness(0, 0, 4, 0) };
        var addBtn = new Button { Content = "添加" };
        addBtn.Click += (_, _) =>
        {
            var n = nameBox.Text.Trim();
            if (n.Length == 0 || _cfg.Tasks.Count >= 12) return;
            _cfg.Tasks.Add(new TaskItem { Name = n, Category = "Other", Color = "#00A0FF", Time = DateTime.Now.ToString("HH:mm") });
            nameBox.Text = "";
            _save();
            RefreshTasks();
        };
        addRow.Children.Add(nameBox);
        addRow.Children.Add(addBtn);
        var taskCol = new StackPanel();
        taskCol.Children.Add(_taskList);
        taskCol.Children.Add(addRow);
        Children.Add(Wrap("事项", taskCol));
        RefreshTasks();

        // 月份 + 日历
        _monthText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(8, 8, 8, 4),
        };
        _calGrid = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        for (int i = 0; i < 7; i++) _calGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++) _calGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        BuildCalendar();
        var monthCol = new StackPanel();
        monthCol.Children.Add(_monthText);
        monthCol.Children.Add(_calGrid);
        Children.Add(Wrap("月份", monthCol));
        RefreshMonth();

        _perf.Metrics += m => Application.Current?.Dispatcher.Invoke(() =>
        {
            _perfText.Text = $"CPU  {m.Cpu:0.0}%\n内存 {m.MemPct:0.0}%  ({m.MemUsedGb:0.0}/{m.MemTotalGb:0.0} GB)\n下载 {m.NetKbps:0} KB/s\n上传 {m.UploadKbps:0} KB/s\n开机 {NativeIslandApp.FmtUptime(m.UptimeSeconds)}";
        });
        _weather.Updated += w => Application.Current?.Dispatcher.Invoke(() =>
        {
            _weatherText.Text = w.Ok ? $"{w.City}  {w.TempC:0.#}°  {w.Desc}" : "天气不可用";
            _weatherErr.Text = w.Error ?? "";
        });

        Show("计划");
    }

    public string ActivePage => _active;

    public void Show(string name)
    {
        _active = name;
        int i = 0;
        foreach (var pageName in Order)
        {
            var panel = _pages[pageName];
            panel.Visibility = pageName == name ? Visibility.Visible : Visibility.Collapsed;
            _navBtns[pageName].Foreground = pageName == name
                ? new SolidColorBrush(Color.FromRgb(0x60, 0xcd, 0xff))
                : Brushes.Gray;
            i++;
        }
        if (name == "月份") { BuildCalendar(); RefreshMonth(); }
    }

    private UIElement Wrap(string title, UIElement body)
    {
        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xcd, 0xff)),
            Margin = new Thickness(8, 4, 8, 0),
        });
        var host = new ContentControl { Content = body };
        col.Children.Add(host);
        _pages[title] = col;
        return col;
    }

    private UIElement BuildPlanPage()
    {
        var col = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        col.Children.Add(new TextBlock
        {
            Text = "计划",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xcd, 0xff)),
        });
        var en = new CheckBox { Content = "启用自动执行", Foreground = Brushes.White, IsChecked = _cfg.Enabled, Margin = new Thickness(0, 6, 0, 0) };
        en.Checked += (_, _) => { _cfg.Enabled = true; _save(); };
        en.Unchecked += (_, _) => { _cfg.Enabled = false; _save(); };
        col.Children.Add(en);

        var actRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actRow.Children.Add(new TextBlock { Text = "动作", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var act = new ComboBox { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var a in PowerActions.All.Values)
            act.Items.Add(new ComboBoxItem { Content = $"{a.Icon} {a.Label}", Tag = a.Key });
        act.SelectedIndex = Math.Max(0, PowerActions.All.Keys.ToList().IndexOf(_cfg.Action));
        act.SelectionChanged += (_, _) =>
        {
            if (act.SelectedItem is ComboBoxItem { Tag: string k })
            {
                _cfg.Action = k;
                _save();
            }
        };
        actRow.Children.Add(act);
        col.Children.Add(actRow);

        var hour = new Spinner(_cfg.Hour, 0, 23);
        var minute = new Spinner(_cfg.Minute, 0, 59);
        hour.Changed += v => { _cfg.Hour = v; _save(); };
        minute.Changed += v => { _cfg.Minute = v; _save(); };
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        timeRow.Children.Add(new TextBlock { Text = "时间", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        timeRow.Children.Add(hour);
        timeRow.Children.Add(new TextBlock { Text = ":", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        timeRow.Children.Add(minute);
        col.Children.Add(timeRow);

        col.Children.Add(new TextBlock { Text = "星期", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 2) });
        var week = new StackPanel { Orientation = Orientation.Horizontal };
        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        for (int d = 1; d <= 7; d++)
        {
            int day = d;
            var cb = new CheckBox { Content = names[d - 1], Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0), IsChecked = _cfg.Weekdays.Contains(day) };
            cb.Checked += (_, _) =>
            {
                if (!_cfg.Weekdays.Contains(day)) _cfg.Weekdays.Add(day);
                _cfg.Weekdays.Sort();
                _cfg.Enabled = true;
                en.IsChecked = true;
                _save();
            };
            cb.Unchecked += (_, _) => { _cfg.Weekdays.Remove(day); _save(); };
            week.Children.Add(cb);
        }
        col.Children.Add(week);

        var also = new CheckBox { Content = "到点同时暂停媒体", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0), IsChecked = _cfg.AlsoPauseMedia };
        also.Checked += (_, _) => { _cfg.AlsoPauseMedia = true; _save(); };
        also.Unchecked += (_, _) => { _cfg.AlsoPauseMedia = false; _save(); };
        col.Children.Add(also);

        col.Children.Add(new TextBlock
        {
            Text = "具体日期：在「月份」页点选日历（再点取消）",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
        });

        _pages["计划"] = col;
        return col;
    }

    private void RefreshTasks()
    {
        _taskList.Children.Clear();
        if (_cfg.Tasks.Count == 0)
        {
            _taskList.Children.Add(new TextBlock { Text = "暂无事项", Foreground = Brushes.Gray, FontSize = 12 });
            return;
        }
        for (int i = 0; i < _cfg.Tasks.Count; i++)
        {
            int idx = i;
            var t = _cfg.Tasks[i];
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var del = new Button { Content = "×", Width = 22, Height = 22, Background = Brushes.Transparent, Foreground = Brushes.OrangeRed, BorderThickness = new Thickness(0) };
            del.Click += (_, _) => { _cfg.Tasks.RemoveAt(idx); _save(); RefreshTasks(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(new TextBlock
            {
                Text = $"{t.Time}  {t.Name}",
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _taskList.Children.Add(row);
        }
    }

    private void RefreshMonth()
    {
        var now = DateTime.Now;
        int days = DateTime.DaysInMonth(now.Year, now.Month);
        int past = now.Day - 1;
        _monthText.Text = $"{now:yyyy年MM月}  已过 {past}/{days} 天 ({past * 100.0 / days:0}%)";
    }

    private void BuildCalendar()
    {
        _calGrid.Children.Clear();
        var now = DateTime.Now;
        var first = new DateTime(now.Year, now.Month, 1);
        int startCol = ((int)first.DayOfWeek + 6) % 7; // Mon=0
        int days = DateTime.DaysInMonth(now.Year, now.Month);
        string[] heads = { "一", "二", "三", "四", "五", "六", "日" };
        for (int c = 0; c < 7; c++)
        {
            var h = new TextBlock { Text = heads[c], Foreground = Brushes.Gray, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(h, 0);
            Grid.SetColumn(h, c);
            _calGrid.Children.Add(h);
        }
        for (int d = 1; d <= days; d++)
        {
            var date = first.AddDays(d - 1);
            string key = date.ToString("yyyy-MM-dd");
            int pos = startCol + d - 1;
            int row = pos / 7 + 1;
            int col = pos % 7;
            bool sel = _cfg.Dates.Contains(key);
            var btn = new Button
            {
                Content = d.ToString(),
                FontSize = 10,
                Height = 22,
                Margin = new Thickness(1),
                Background = sel ? new SolidColorBrush(Color.FromRgb(0x60, 0xcd, 0xff)) : Brushes.Transparent,
                Foreground = sel ? Brushes.Black : Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = key,
            };
            btn.Click += (_, _) =>
            {
                if (_cfg.Dates.Contains(key)) _cfg.Dates.Remove(key);
                else
                {
                    _cfg.Dates.Add(key);
                    _cfg.Dates.Sort();
                    _cfg.Enabled = true;
                }
                _save();
                BuildCalendar();
            };
            Grid.SetRow(btn, row);
            Grid.SetColumn(btn, col);
            _calGrid.Children.Add(btn);
        }
    }

    private sealed class Spinner : StackPanel
    {
        public event Action<int>? Changed;
        private int _value;
        private readonly TextBlock _label;

        public Spinner(int value, int min, int max)
        {
            Orientation = Orientation.Horizontal;
            _value = Math.Clamp(value, min, max);
            var down = new Button { Content = "−", Width = 26, Height = 26, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            _label = new TextBlock
            {
                Text = _value.ToString("00"),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
            };
            var up = new Button { Content = "+", Width = 26, Height = 26, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            down.Click += (_, _) => { _value = _value <= min ? max : _value - 1; _label.Text = _value.ToString("00"); Changed?.Invoke(_value); };
            up.Click += (_, _) => { _value = _value >= max ? min : _value + 1; _label.Text = _value.ToString("00"); Changed?.Invoke(_value); };
            Children.Add(down);
            Children.Add(_label);
            Children.Add(up);
        }
    }
}
