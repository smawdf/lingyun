using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LingYun.Services;

namespace LingYun.Ui;

/// <summary>紧凑态右缘快捷球：默认 3 枚扇出，点击执行并轮换。</summary>
public sealed class QuickFan : Canvas
{
    private readonly List<Button> _balls = new();
    private int _index;
    private bool _open;

    public QuickFan()
    {
        for (int i = 0; i < 3; i++)
        {
            var b = new Button
            {
                Width = 36,
                Height = 36,
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = true,
            };
            int slot = i;
            b.Click += (_, _) => OnBallClick(slot);
            _balls.Add(b);
            Children.Add(b);
        }
        MouseLeave += (_, _) => SetOpen(false);
    }

    public void SetOpen(bool open)
    {
        _open = open;
        Layout();
    }

    public void Place(double x, double y)
    {
        // x,y = 岛右缘内侧锚点
        Margin = new Thickness(0);
        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
        Width = 120;
        Height = 80;
        Layout();
    }

    private void Layout()
    {
        var acts = QuickActions.All;
        for (int i = 0; i < _balls.Count; i++)
        {
            var b = _balls[i];
            if (!_open)
            {
                b.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(b, i == 0 ? 40 : 0);
                Canvas.SetTop(b, 12);
            }
            else
            {
                b.Visibility = Visibility.Visible;
                // 扇出：向右下
                Canvas.SetLeft(b, 40 + i * 18);
                Canvas.SetTop(b, 8 + i * 16);
            }
            int idx = (_index + i) % acts.Count;
            b.Content = acts[idx].Icon;
            b.ToolTip = acts[idx].Label;
            b.Tag = idx;
        }
    }

    private void OnBallClick(int slot)
    {
        var acts = QuickActions.All;
        int idx = _open ? (_index + slot) % acts.Count : _index;
        acts[idx].Run();
        _index = (_index + 1) % acts.Count;
        Layout();
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetOpen(true);
    }
}
