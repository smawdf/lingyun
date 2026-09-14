# -*- coding: utf-8 -*-
"""七条修改：色调去重 / 圆角策略 / 去 classic / 材质与主题合并 / 设置窗✕去掉悬停框 /
岛展开✕删除 / 顶部空白恢复收起。"""
import io

BASE = 'D:/kaifa/灵云/'
log = []


def patch(path, pairs):
    full = BASE + path
    s = io.open(full, encoding='utf-8').read()
    for old, new, why in pairs:
        hit = old in s
        log.append((path, why, hit))
        if hit:
            s = s.replace(old, new, 1)
    io.open(full, 'w', encoding='utf-8', newline='\n').write(s)


# ---------------- 1) 配置：去 ui_material，加 base_theme ----------------
patch('cs/LingYun/Config/AppConfig.cs', [
    ('''    /// <summary>
    /// 设置窗口材质：acrylic 亚克力（系统真模糊）/ glass 液态玻璃（近似，WPF 做不了折射）/
    /// classic 原生 Windows（纯色 + 系统控件）。岛的材质在 <see cref="Theme"/> 里选。
    /// </summary>
    public string UiMaterial { get; set; } = "acrylic";''',
     '''    /// <summary>
    /// 「外观」= 材质 + 深浅，两者联动（不再有独立的设置窗口材质）：
    /// <see cref="Theme"/> 是最终生效值（dark/light/system/liquid-glass），
    /// <see cref="BaseTheme"/> 记住"亚克力"档下的深浅，从液态玻璃切回来时用。
    /// 设置窗口材质由 theme 推导：液态玻璃 → 同款半透明；其余 → 系统亚克力。
    /// </summary>
    public string BaseTheme { get; set; } = "dark";''', 'base_theme'),
    ('        if (cfg.UiMaterial is not ("acrylic" or "glass" or "classic")) cfg.UiMaterial = "acrylic";',
     '        if (cfg.BaseTheme is not ("dark" or "light" or "system")) cfg.BaseTheme = "dark";', 'normalize'),
])

# ---------------- 2) 材质：去 classic ----------------
patch('cs/LingYun/Platform/WindowMaterial.cs', [
    ('''    public const string Acrylic = "acrylic";
    public const string Glass = "glass";
    public const string Classic = "classic";''',
     '''    public const string Acrylic = "acrylic";
    public const string Glass = "glass";''', 'consts'),
    ('''        if (material == Classic) return "solid";
        // 液态玻璃这一档刻意**不做**系统模糊：它要和岛的材质一模一样（清晰透明），
        // 模糊只有亚克力才用。两者是"两种观感"，不是同一套东西的浓淡。
        if (material == Glass) return "translucent";''',
     '''        // 液态玻璃这一档刻意**不做**系统模糊：它要和岛的材质一模一样（清晰透明），
        // 模糊只有亚克力才用。两者是"两种观感"，不是同一套东西的浓淡。
        if (material == Glass) return "translucent";''', 'resolve'),
    ('''            Glass => dark ? unchecked((int)0xA6121418) : unchecked((int)0xA6FFFFFF),
            Classic => dark ? unchecked((int)0xFF202020) : unchecked((int)0xFFF0F0F0),
            _ => dark ? unchecked((int)0x861A1B20) : unchecked((int)0x7AF2F4F8),   // acrylic''',
     '''            Glass => dark ? unchecked((int)0xA6121418) : unchecked((int)0xA6FFFFFF),
            _ => dark ? unchecked((int)0x861A1B20) : unchecked((int)0x7AF2F4F8),   // acrylic''', 'tints'),
    ('''    /// <summary>面板圆角（DIP）。</summary>
    internal static double Radius(string material)
        => material switch { Classic => 0, Glass => 20, _ => 10 };

    /// <summary>是否画落影（经典档不要）。</summary>
    internal static bool HasShadow(string material) => material != Classic;''',
     '''    /// <summary>面板圆角（DIP）：玻璃大圆角（圆角由我们自己画），亚克力跟 DWM 的观感走。</summary>
    internal static double Radius(string material) => material == Glass ? 20 : 10;

    /// <summary>是否需要裁窗口区域：只有亚克力要（系统模糊会铺满整个矩形，不裁会露出方角）。</summary>
    internal static bool NeedsRegion(string material) => material != Glass;''', 'radius+region'),
])

io.open(BASE + 'cs/tools/patch_log.txt', 'w', encoding='utf-8').write(
    '\n'.join(f'{p}\t{w}\t{"OK" if h else "MISS"}' for p, w, h in log))
for p, w, h in log:
    print(('OK  ' if h else 'MISS'), w)
