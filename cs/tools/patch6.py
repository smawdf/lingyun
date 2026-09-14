# -*- coding: utf-8 -*-
"""① 自动关闭时长改名更好找；② 液态玻璃档下禁用「深浅」；③ 窗口玻璃色调与岛的玻璃主体对齐(214)。"""
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


patch('cs/LingYun/Platform/WindowMaterial.cs', [
    ('''            Glass => dark ? unchecked((int)0xA6121418) : unchecked((int)0xA6FFFFFF),''',
     '''            // 与岛的液态玻璃主体**同 alpha**（214 = 0xD6）：同一个主题下岛和窗口才是同一块材质
            Glass => dark ? unchecked((int)0xD6121418) : unchecked((int)0xD6FFFFFF),''', 'glass tint = island'),
])

patch('cs/LingYun/Ui/SettingsWindow.cs', [
    # ① 改名成用户的说法，并写清它管什么
    ('''        AddSlider(card, "自动回缩", _autoCollapse, _autoCollapseLabel, v =>
        {
            _cfg.AutoCollapseMs = (int)v;
            _autoCollapseLabel.Text = v <= 0 ? "  不自动回缩" : $"  离开 {v / 1000.0:0.0}s 后";
            _island.ApplyConfig();
        });
        AddHint(root, "展开面板在鼠标离开岛后多久自动收起；调到最左（0）就永不自动收起，点空白处仍然可以手动收起。");''',
     '''        AddSlider(card, "展开自动关闭", _autoCollapse, _autoCollapseLabel, v =>
        {
            _cfg.AutoCollapseMs = (int)v;
            _autoCollapseLabel.Text = v <= 0 ? "  不自动关闭" : $"  离开 {v / 1000.0:0.0}s 后";
            _island.ApplyConfig();
        });
        AddHint(root, "岛的展开面板在鼠标离开后多久自动关闭；拉到最左（0）就永不自动关闭，"
                      + "那时只能点空白处关闭。");''', 'rename slider'),
    ('''        _autoCollapseLabel.Text = _cfg.AutoCollapseMs <= 0
            ? "  不自动回缩" : $"  离开 {_cfg.AutoCollapseMs / 1000.0:0.0}s 后";''',
     '''        _autoCollapseLabel.Text = _cfg.AutoCollapseMs <= 0
            ? "  不自动关闭" : $"  离开 {_cfg.AutoCollapseMs / 1000.0:0.0}s 后";''', 'labels'),

    # ② 液态玻璃档下「深浅」无意义（玻璃自带浅色 + 自适应），置灰并说明
    ('''        AddRadioRow(card, "深浅", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight), ("跟随系统", _themeSystem),
        });''',
     '''        AddRadioRow(card, "深浅", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight), ("跟随系统", _themeSystem),
        });
        _depthRow = card;   // 液态玻璃档下置灰（玻璃恒浅色 + 自适应，深浅不适用）''', 'depth row ref'),
    ('''    private readonly RadioButton _topAlways = new();''',
     '''    private StackPanel? _depthRow;
    private readonly RadioButton _topAlways = new();''', 'depth field'),
    ('''        AddGroupLabel(root, "媒体页");''',
     '''        AddGroupLabel(root, "媒体页");''', 'noop'),
    ('''        ApplyControlStyles();
        RefreshStates();
    }''',
     '''        // 液态玻璃档下「深浅」不适用：玻璃恒浅色（深色壁纸时自适应切深色玻璃）
        if (_depthRow is not null)
        {
            bool glassNow = IslandPalette.IsLiquidGlass(_cfg.Theme);
            foreach (UIElement child in _depthRow.Children)
                child.IsEnabled = !glassNow;
            _themeDark.IsEnabled = _themeLight.IsEnabled = _themeSystem.IsEnabled = !glassNow;
        }
        ApplyControlStyles();
        RefreshStates();
    }''', 'depth enable'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)
