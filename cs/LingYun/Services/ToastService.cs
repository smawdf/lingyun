using System.Threading.Tasks;

namespace LingYun.Services;

/// <summary>
/// 一条系统通知。Aumid/Id 用于「点击唤醒对应应用」与「从操作中心划掉」。
/// </summary>
public sealed record ToastData(string App, string Title, string Body, string Aumid = "", uint Id = 0)
{
    public string OneLine =>
        (Title.Length > 0 ? Title : Body) is { Length: > 0 } t
            ? (App.Length > 0 && !t.StartsWith(App) ? App + " · " + t : t)
            : App;
}

/// <summary>
/// 系统通知（Toast）监听。
/// 思路与部分实现移植自 Apache-2.0 项目 NotchPeninsula 的 toast.cs（见 THIRD-PARTY.md）：
/// 用 WinRT 的 UserNotificationListener 轮询（该 API 没有推送事件，只能轮询）。
/// 与上游的差异：启动时以「现有通知的最大 Id」作高水位，历史通知不回放；
/// 每条通知带 AUMID 与 Id；支持划掉单条通知。
/// 需要用户在「设置 → 隐私和安全性 → 通知」里允许应用访问通知，否则直接降级为不可用。
/// </summary>
public sealed class ToastService : IDisposable
{
    private readonly System.Timers.Timer _poll;
    private Windows.UI.Notifications.Management.UserNotificationListener? _listener;
    private volatile bool _polling;
    private ToastData? _current;
    private uint _lastId;            // 已见过的最大通知 Id（高水位）：比它新才算新通知
    private DateTime _shownAt;

    /// <summary>有新通知（非 null）或通知已过期（null）。</summary>
    public event Action<ToastData?>? Changed;

    /// <summary>是否已获得通知访问权限。false 时本服务不产生任何事件。</summary>
    public bool Available { get; private set; }

    /// <summary>最后一次请求的结果说明，用于诊断。</summary>
    public string Status { get; private set; } = "未初始化";

    public TimeSpan VisibleFor { get; } = TimeSpan.FromSeconds(6);

    /// <summary>已见过的最大通知 Id（诊断/自测用）。</summary>
    internal uint LastSeenId => _lastId;

    /// <summary>当前正在展示的通知（诊断用）。</summary>
    internal ToastData? Current => _current;

    public ToastService(int pollMs = 2000)
    {
        _poll = new System.Timers.Timer(pollMs) { AutoReset = true };
        _poll.Elapsed += (_, _) => _ = PollAsync();
    }

    /// <summary>初始化结果：是否可用 + 说明。</summary>
    private record InitResult(bool Available, string Status);

    /// <summary>
    /// 真正请求权限并起轮询。放在后台线程跑：不带包标识的桌面应用里，
    /// UserNotificationListener 的激活与 RequestAccessAsync 可能同步阻塞永不返回，
    /// 不能让它把启动流程（或诊断）卡死。
    /// </summary>
    private async Task<InitResult> InitAsync()
    {
        var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
        _listener = listener;
        var status = await listener.RequestAccessAsync();
        if (status != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            return new InitResult(false, "用户未授予通知访问权限（设置 → 系统 → 通知）");
        // 高水位：把启动时就躺在操作中心里的旧通知全部记作「已见过」，否则一开机会弹一条旧消息
        try
        {
            var existing = await listener.GetNotificationsAsync(
                Windows.UI.Notifications.NotificationKinds.Toast);
            var list = existing is null
                ? new List<Windows.UI.Notifications.UserNotification>()
                : System.Linq.Enumerable.ToList(existing);
            if (list.Count > 0) _lastId = list.Max(n => n.Id);
        }
        catch { /* 拿不到就当没有旧通知 */ }
        _poll.Start();
        await PollAsync();
        return new InitResult(true, "已授权");
    }

    public async Task StartAsync()
    {
        Task<InitResult> work;
        try
        {
            work = Task.Run(InitAsync);
        }
        catch (Exception ex)
        {
            Available = false;
            Status = "不可用：" + ex.GetType().Name + " " + ex.Message;
            return;
        }

        // 硬超时：超了就放弃（那条后台线程会自己烂掉，不影响主流程）
        var done = await Task.WhenAny(work, Task.Delay(5000));
        if (!ReferenceEquals(done, work))
        {
            Available = false;
            Status = "初始化超时：未带包标识的桌面应用常拿不到通知权限，已跳过该功能";
            return;
        }
        try
        {
            var r = await work;
            Available = r.Available;
            Status = r.Status;
        }
        catch (Exception ex)
        {
            Available = false;
            Status = "不可用：" + ex.GetType().Name + " " + ex.Message;
        }
    }

    private async Task PollAsync()
    {
        // 上一轮还没回来就跳过，避免在卡住的 WinRT 调用上堆积
        if (!Available || _polling || _listener is null) return;
        _polling = true;
        try
        {
            var listener = _listener;
            // WinRT 的 IVectorView 在此投影下 Count 是方法而不是属性，
            // 所以一律走 LINQ 的枚举接口，不直接依赖 Count / 索引器。
            var raw = await listener.GetNotificationsAsync(
                Windows.UI.Notifications.NotificationKinds.Toast);
            var list = raw is null
                ? new List<Windows.UI.Notifications.UserNotification>()
                : System.Linq.Enumerable.ToList(raw);
            // 最新的一条 = Id 最大的一条（操作中心按时间递增 Id）
            var newest = list.OrderByDescending(n => n.Id).FirstOrDefault();
            if (newest is null || newest.Id <= _lastId)
            {
                // 没有新通知：当前这条超过展示时长就撤下
                if (_current is not null && DateTime.Now - _shownAt > VisibleFor)
                {
                    _current = null;
                    Changed?.Invoke(null);
                }
                return;
            }
            _lastId = newest.Id;
            var data = Extract(newest);
            if (data is null) return;   // 取不到内容（空通知）→ 记下 Id 不再重试
            _shownAt = DateTime.Now;
            _current = data;
            Changed?.Invoke(data);
        }
        catch
        {
            // 轮询失败静默跳过，下一轮再试；通知不是核心功能
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>从 WinRT 通知里取显示名 / 标题 / 正文 / AUMID。全空视为无效。</summary>
    private static ToastData? Extract(Windows.UI.Notifications.UserNotification un)
    {
        string app = "", aumid = "";
        try
        {
            app = un.AppInfo?.DisplayInfo?.DisplayName ?? "";
            aumid = un.AppInfo?.AppUserModelId ?? "";
        }
        catch { /* 部分应用拿不到显示名 */ }

        string title = "", body = "";
        try
        {
            var binding = un.Notification?.Visual?.GetBinding(
                Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
            if (binding is not null)
            {
                int i = 0;
                foreach (var t in System.Linq.Enumerable.ToList(binding.GetTextElements()))
                {
                    if (i == 0) title = t.Text ?? "";
                    else if (i == 1) body = t.Text ?? "";
                    else break;
                    i++;
                }
            }
        }
        catch { /* 拿不到正文就用应用名兜底 */ }

        if (app.Length == 0 && title.Length == 0 && body.Length == 0) return null;
        return new ToastData(app, title, body, aumid, un.Id);
    }

    /// <summary>
    /// 说明：WinRT 的 UserNotificationListener 没有「按 Id 删除单条」的 API（只有清空全部，
    /// 那会误删用户的其他通知），所以点击后不划掉通知——与上游 NotchPeninsula 的行为一致。
    /// 岛侧收起后也不会重复弹出：该 Id 已记入高水位。
    /// </summary>
    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
    }
}
