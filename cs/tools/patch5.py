# -*- coding: utf-8 -*-
"""岛：删掉展开态 ✕（绘制+命中），撤回"页签条吞点击"（点顶部空白恢复为收起）。"""
import io

P = r'D:\kaifa\灵云\cs\LingYun\Ui\NativeIslandApp.cs'
s = io.open(P, encoding='utf-8').read()
log = []


def rep(old, new, why):
    global s
    hit = old in s
    log.append((why, hit))
    if hit:
        s = s.replace(old, new, 1)


# 1) 撤回"页签条吞点击"：点页签条空白重新走"点空白收起"
rep('''                    // 页签条上的空白也算导航区：吃掉落点，别穿透到"点空白收起"
                    TraceClick("tab band but no tab hit (swallowed)");
                    return;
                }''',
    '''                    // 点页签条上的空白 = 点空白处：按统一规则收起（用户要的行为：
                    // ✕ 已移除，展开后点顶部/其他地方都收起）
                    TraceClick("tab band but no tab hit -> collapse");
                    CollapseOnBlank();
                    return;
                }''', 'tab blank collapses')

# 2) 删掉 ✕ 的命中区
rep('''            // ✕
            if (x >= ix + iw - 40 && x <= ix + iw - 8 && y >= iy + 6 && y <= iy + 34)
            {
                SetMode("compact");
                return;
            }
''', '', 'drop close hit')

# 3) 删掉 ✕ 的绘制
rep('''        // 关闭钮（三种媒体样式共用右上角位置）
        DrawText(canvas, "✕", r.Right - 28 * s, r.Top + 24 * s, 14 * s, Pal.Sub);
''',
    '''        // 展开态没有关闭钮（用户要求移除）：收起靠点空白处 / 顶部空白 / 自动回缩
''', 'drop close draw')

io.open(P, 'w', encoding='utf-8', newline='\n').write(s)
for why, hit in log:
    print(('OK  ' if hit else 'MISS'), why)
