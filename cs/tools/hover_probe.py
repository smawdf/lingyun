"""实测：紧凑态胶囊悬停是否仍然"无反应"（快捷球已移除，功能只在展开后的「快捷」页）。

为什么需要它：离屏渲染帧（--dump-frames）走的是 ForcePage/ForceQuickPress 强制态，
**证明不了运行时**的真实悬停行为。这个脚本在真机上按真实输入路径走一遍。

用法：
    python hover_probe.py --label off --wait 9

    --label  输出文件名后缀：_before_<label>.png / _after_<label>.png
    --wait   开始前先等几秒（等启动时的系统通知收起——通知占位期间会盖住紧凑态，污染结论）

判据：after 与 before 的差异采样点数。快捷球移除后悬停应恒为 0；
若出现显著差异点，说明 compact 又混入了悬停交互，需要回查。

注意：单次 SetCursorPos 跳转**不会**投递 WM_MOUSEMOVE，
分层窗更是只对不透明像素投递鼠标消息，所以必须小步走。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import ImageGrab

u32 = ctypes.windll.user32

CLASS = "LingYunLayeredIsland"
SHELL_W = 640
ISLAND_W, ISLAND_H = 240, 52     # 紧凑态
HOT = 34                         # FanHot：右缘热区宽度
STEPS = 60                       # 小步移动的步数


def find_window():
    """按窗口类名找岛壳窗口，返回 (hwnd, RECT)。"""
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        buf = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(hwnd, buf, 256)
        if buf.value == CLASS:
            found.append(hwnd)
            return False
        return True

    u32.EnumWindows(cb, 0)
    if not found:
        return None, None
    r = wt.RECT()
    u32.GetWindowRect(found[0], ctypes.byref(r))
    return found[0], r


def diff_count(a, b, step=2, thr=30):
    """隔列采样比对，返回变化像素数。"""
    w, h = a.size
    n = 0
    for y in range(h):
        for x in range(0, w, step):
            p, q = a.getpixel((x, y)), b.getpixel((x, y))
            if abs(p[0] - q[0]) + abs(p[1] - q[1]) + abs(p[2] - q[2]) > thr:
                n += 1
    return n, ((w + 1) // 2) * h


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", default="run")
    ap.add_argument("--wait", type=float, default=0.0)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    out = args.out or os.path.dirname(os.path.abspath(__file__))

    if args.wait > 0:
        print(f"先等 {args.wait}s，让启动时的系统通知收起（通知占位期间不画球）…")
        time.sleep(args.wait)

    hwnd, r = find_window()
    if not hwnd:
        print("FAIL 没找到窗口 class=" + CLASS)
        return 1

    sx, sy = r.left, r.top
    # 岛体：壳内水平居中、贴壳顶（Island() 返回的 y 恒为 0）
    ix = sx + (SHELL_W - ISLAND_W) // 2
    iy = sy
    right, midy = ix + ISLAND_W, iy + ISLAND_H // 2
    print(f"shell=({sx},{sy})-({r.right},{r.bottom})  island=({ix},{iy})-({right},{iy + ISLAND_H})")
    print(f"热区 x > {right - HOT}（FanHot={HOT}）  悬停点=({right - 10},{midy})")

    # 截图区：岛体 + 右侧余量 + 下方（长按提示药丸画在壳内 y≤72）
    box = (ix - 20, iy - 6, right + 40, iy + 110)

    pt = wt.POINT()
    u32.GetCursorPos(ctypes.byref(pt))
    saved = (pt.x, pt.y)

    before = os.path.join(out, f"_before_{args.label}.png")
    after = os.path.join(out, f"_after_{args.label}.png")
    for p in (before, after):
        if os.path.exists(p):
            os.remove(p)

    try:
        # 1) 先把鼠标挪远，确保不是悬停态
        u32.SetCursorPos(ix + 10, iy + ISLAND_H + 200)
        time.sleep(0.40)
        a = ImageGrab.grab(bbox=box, all_screens=True)
        a.save(before)

        # 2) 小步走进右缘热区（必须小步）
        startx, endx = ix + 40, right - 10
        for i in range(STEPS + 1):
            u32.SetCursorPos(startx + (endx - startx) * i // STEPS, midy)
            time.sleep(0.012)

        # 3) 停住等 dwell（dwell 由渲染循环驱动，停住不动也会到期）
        time.sleep(1.6)

        b = ImageGrab.grab(bbox=box, all_screens=True)
        b.save(after)

        n, total = diff_count(a, b)
        print(f"before={before}")
        print(f"after ={after}")
        print(f"采样点={total}  差异点={n}")
        print("结论: " + ("悬停无变化（紧凑态无悬停交互，符合预期）" if n == 0
                          else f"悬停出现变化 {n} 点（compact 混入了悬停交互，需回查）"))
    finally:
        u32.SetCursorPos(saved[0], saved[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
