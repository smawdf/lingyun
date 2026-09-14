using System.Diagnostics;

namespace LingYun.Services;

/// <summary>
/// 紧凑胶囊右缘的快捷球动作。
/// <para>
/// <c>Destructive=true</c> 表示这个动作会丢掉未保存的工作（关机 / 重启 / 睡眠），
/// 因此**单击绝不执行**，必须按住 <see cref="QuickActions.HoldMs"/> 毫秒确认。
/// 详见 <see cref="QuickActions"/> 的顺序契约。
/// </para>
/// </summary>
public sealed record QuickAction(string Key, string Label, string Icon, Action Run, bool Destructive = false);

public static class QuickActions
{
    /// <summary>危险动作需要按住的时长（毫秒）。低于它的松手一律当成误触。</summary>
    public const int HoldMs = 900;

    /// <summary>
    /// 顺序 = 扇出顺序，**安全动作必须排在前面**。
    /// <para>
    /// 历史教训一：这里原本是 关机/重启/睡眠 打头，鼠标一悬停岛右缘扇出来的就是这三个危险项，
    /// 随手一点就跑 <c>shutdown /r /t 0</c> 把用户电脑重启了。现在危险项一律后置，
    /// 并且额外受长按保护；<c>--self-test</c> 会断言第一屏不含危险动作。
    /// </para>
    /// <para>
    /// 历史教训二：资源管理器 / 设置 / 任务管理器 三个曾被用户点名移除
    /// （2026-09-11，原第一屏就是它们）。<c>--self-test</c> 会断言这三个 key 已消失，
    /// 避免日后被顺手加回来。
    /// </para>
    /// <para>
    /// 历史教训三：YouTube 曾被用户点名移除（2026-09-13，快捷页改版），由自定义程序承接；
    /// 自定义程序存配置 <c>quick_custom</c>，不进这张内置表。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<QuickAction> All = new List<QuickAction>
    {
        new("browser", "浏览器", "🌐", () => Shell("start msedge")),
        new("cmd", "命令行", ">_", () => Shell("start cmd")),
        new("sleep", "睡眠", "☾", () => Shell("rundll32.exe powrprof.dll,SetSuspendState 0,1,0"), Destructive: true),
        new("restart", "重启", "↻", () => Shell("shutdown /r /t 0"), Destructive: true),
        new("shutdown", "关机", "⏻", () => PowerActions.Shutdown(), Destructive: true),
    };

    /// <summary>危险动作数量（自检用）。</summary>
    public static int DestructiveCount => All.Count(a => a.Destructive);

    /// <summary>第一个危险动作的下标，没有则 -1。诊断出图靠它摆"危险屏"，避免写死下标。</summary>
    public static int FirstDestructiveIndex
    {
        get
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i].Destructive) return i;
            return -1;
        }
    }

    /// <summary>按 key 找下标，找不到返回 -1。</summary>
    public static int IndexOf(string key)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Key == key) return i;
        return -1;
    }

    /// <summary>
    /// 单击是否应当立刻执行。抽成纯函数是为了能在 <c>--self-test</c> 里断言
    /// 「危险动作单击绝不执行」——那正是把用户电脑重启掉的根因。
    /// </summary>
    public static bool ShouldRunOnClick(QuickAction a) => !a.Destructive;

    /// <summary>长按是否够格执行：必须是危险动作、按够了时长、且松手时指针仍在原球上。</summary>
    public static bool ShouldRunOnHold(QuickAction a, double heldMs, bool stillOnBall)
        => a.Destructive && stillOnBall && heldMs >= HoldMs;

    private static void Shell(string cmd) =>
        Process.Start(new ProcessStartInfo("cmd", $"/c {cmd}") { UseShellExecute = false });
}
