using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LingYun.Services;

public sealed class MediaState
{
    public bool Active { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AppId { get; init; } = "";
    public string Status { get; init; } = "Closed";
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public byte[]? Thumb { get; init; }
    public bool IsPlaying => Active && Status == "Playing";
}

/// <summary>SMTC 会话摘要，供展开面板的「来源」切换器显示。</summary>
public sealed class SessionInfo
{
    public string AppId { get; init; } = "";
    /// <summary>友好名（Edge / Chrome / 网易云…），取不到就用 AppId。</summary>
    public string Label { get; init; } = "";
    public bool IsPlaying { get; init; }
}

/// <summary>Windows SMTC 媒体会话（浏览器 Edge/Chrome 与音乐客户端均可）。</summary>
public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly System.Timers.Timer _timer;
    private bool _seeking;
    // 用户手动选定的来源（AUMID）；空 = 跟随系统当前会话
    private string _lockedAppId = "";
    // 模拟进度：SMTC 的 Position 是快照（多数播放器只在播放/暂停/拖动时更新），
    // 播放期间本地按墙钟推进，SMTC 跳变或换曲才重新对齐
    private long _simPosMs;
    private DateTime? _lastRefreshAt;
    private string _simKey = "";

    public event Action<MediaState>? Updated;
    public MediaState State { get; private set; } = new();

    /// <summary>当前所有 SMTC 会话（多个播放器同时出声时的「来源」列表）。</summary>
    public IReadOnlyList<SessionInfo> Sessions { get; private set; } = Array.Empty<SessionInfo>();

    /// <summary>正在被控制的会话在 <see cref="Sessions"/> 里的下标；-1 = 无会话。</summary>
    public int SelectedIndex { get; private set; } = -1;

    public MediaSessionService()
    {
        _timer = new System.Timers.Timer(500) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await RefreshSafe();
    }

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _timer.Start();
            await Refresh();
        }
        catch
        {
            State = new MediaState { Active = false };
            Updated?.Invoke(State);
        }
    }

    public void Stop() => _timer.Stop();

    /// <summary>离屏渲染用：注入假媒体状态（诊断出帧时让胶囊有媒体可画）。</summary>
    internal void InjectState(MediaState state) => State = state;

    /// <summary>离屏渲染用：注入假会话列表，供「来源」切换器出图。</summary>
    internal void InjectSessions(IReadOnlyList<SessionInfo> sessions, int selectedIndex)
    {
        Sessions = sessions;
        SelectedIndex = sessions.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, sessions.Count - 1);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    public void BeginSeek() => _seeking = true;

    public async Task SeekAsync(long ms)
    {
        if (_session is null) return;
        try
        {
            // CsWinRT 投影：TryChangePlaybackPositionAsync(long ticks)
            await _session.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(ms).Ticks);
        }
        catch { /* ignore */ }
        _seeking = false;
        await Refresh();
    }

    public async Task PlayPauseAsync()
    {
        if (_session is null) return;
        try { await _session.TryTogglePlayPauseAsync(); } catch { /* ignore */ }
        _ = Refresh();
    }

    /// <summary>真暂停（幂等）。用于「暂停媒体」动作，替代会反向播放的媒体键 toggle。</summary>
    public async Task PauseAsync()
    {
        if (_session is null) return;
        try { await _session.TryPauseAsync(); } catch { /* ignore */ }
        _ = Refresh();
    }

    public async Task NextAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipNextAsync(); } catch { /* ignore */ }
        _ = Refresh();
    }

    public async Task PrevAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipPreviousAsync(); } catch { /* ignore */ }
        _ = Refresh();
    }

    /// <summary>
    /// 切到下一个/上一个来源（展开面板「来源 ‹ n/m ›」）。
    /// 选定后锁定该会话，直到它消失；之后自动回到跟随系统当前会话。
    /// </summary>
    public void CycleSession(int delta)
    {
        if (Sessions.Count == 0) return;
        int cur = SelectedIndex < 0 ? 0 : SelectedIndex;
        int next = ((cur + delta) % Sessions.Count + Sessions.Count) % Sessions.Count;
        _lockedAppId = Sessions[next].AppId;
        _ = Refresh();
    }

    /// <summary>
    /// 选会话：优先用户锁定（按 AUMID），其次系统当前会话，再次任一正在播放的，最后第一个。
    /// 抽成纯函数是为了 --self-test 能断言回落顺序，不依赖真实 SMTC 会话。
    /// </summary>
    internal static int PickSession(
        IReadOnlyList<(string AppId, bool IsPlaying)> sessions, string? lockedAppId, int systemCurrentIndex)
    {
        if (sessions.Count == 0) return -1;
        if (!string.IsNullOrEmpty(lockedAppId))
        {
            for (int i = 0; i < sessions.Count; i++)
                if (string.Equals(sessions[i].AppId, lockedAppId, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        if (systemCurrentIndex >= 0 && systemCurrentIndex < sessions.Count) return systemCurrentIndex;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].IsPlaying) return i;
        return 0;
    }

    /// <summary>
    /// 浏览器标题清理（思路来自 NotchPeninsula MediaController.CleanBrowserTitle，Apache-2.0）：
    /// 「正在播放：A - B」拆出标题与艺人，并去掉 B 站/优酷等视频站的长后缀。
    /// </summary>
    internal static string CleanTitle(string title, out string artist)
    {
        artist = "";
        var trimmed = title.TrimEnd();
        const string prefix = "正在播放";
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.Length > prefix.Length
            && (trimmed[prefix.Length] == ':' || trimmed[prefix.Length] == '：'))
        {
            trimmed = trimmed[(prefix.Length + 1)..].Trim();
            // 「标题 - 艺人」：分隔符后还有内容才算艺人，避免把「A - 」当成艺人
            int dash = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash + 3 < trimmed.Length)
            {
                artist = trimmed[(dash + 3)..].Trim();
                trimmed = trimmed[..dash].Trim();
            }
        }
        foreach (var suffix in BrowserVideoSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length].TrimEnd();
        }
        return trimmed;
    }

    /// <summary>视频站标题的常见尾巴（去掉了标题里最吵的部分）。</summary>
    private static readonly string[] BrowserVideoSuffixes =
    {
        "_哔哩哔哩_bilibili",
        "-电视剧-高清完整正版视频在线观看-优酷",
        "-电影-高清完整正版视频在线观看-优酷",
        "-综艺-高清完整正版视频在线观看-优酷",
        "-动漫-高清完整正版视频在线观看-优酷",
        "-少儿-高清完整正版视频在线观看-优酷",
        "-纪录片-高清完整正版视频在线观看-优酷",
        "-体育-高清完整正版视频在线观看-优酷",
        "-文化-高清完整正版视频在线观看-优酷",
        "-游戏-高清完整正版视频在线观看-优酷",
        "-音乐-高清完整正版视频在线观看-优酷",
        "-最新热门短剧大全-免费短剧在线观看",
        "_腾讯视频",
        "_爱奇艺",
    };

    /// <summary>
    /// SMTC 会话黑名单：这些客户端上报会话但不响应任何控制命令
    /// （TryTogglePlayPause / TryChangePlaybackPosition 全部无效，2026-09-13 实测抖音），
    /// 展示出来只会让暂停和进度条变成摆设——整表忽略：不进来源列表、不占媒体胶囊。
    /// </summary>
    private static readonly string[] IgnoredApps = { "douyin" };

    /// <summary>该会话是否应被忽略（纯函数，自测用）。按 AppId 子串匹配。</summary>
    internal static bool IsIgnoredApp(string appId)
    {
        var a = (appId ?? "").ToLowerInvariant();
        return IgnoredApps.Any(a.Contains);
    }

    private async Task RefreshSafe()
    {
        try { await Refresh(); } catch { /* ignore */ }
    }

    private async Task Refresh()
    {
        if (_mgr is null || _seeking) return;

        // 枚举全部会话：黑名单应用先剔除，多个播放器同时出声时再让用户手动选来源
        var raw = _mgr.GetSessions()
            .Where(s => !IsIgnoredApp(s.SourceAppUserModelId ?? ""))
            .ToList();
        var probes = new List<(string AppId, bool IsPlaying)>(raw.Count);
        var systemCurrent = _mgr.GetCurrentSession();
        int sysIdx = -1;
        for (int i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            bool playing = false;
            try { playing = s.GetPlaybackInfo()?.PlaybackStatus.ToString() == "Playing"; } catch { /* 个别会话拒绝 */ }
            string id = s.SourceAppUserModelId ?? "";
            probes.Add((id, playing));
            if (systemCurrent is not null && id == (systemCurrent.SourceAppUserModelId ?? ""))
                sysIdx = i;
        }
        Sessions = probes
            .Select(p => new SessionInfo { AppId = p.AppId, Label = AppIcons.FriendlyName(p.AppId), IsPlaying = p.IsPlaying })
            .ToList();

        int idx = PickSession(probes, _lockedAppId, sysIdx);
        SelectedIndex = idx;
        // 锁定项已经消失：释放锁定，回到跟随系统当前会话
        if (_lockedAppId.Length > 0 && idx >= 0
            && !string.Equals(probes[idx].AppId, _lockedAppId, StringComparison.OrdinalIgnoreCase))
            _lockedAppId = "";

        var session = idx >= 0 && idx < raw.Count ? raw[idx] : null;
        _session = session;
        if (session is null)
        {
            State = new MediaState { Active = false };
            Updated?.Invoke(State);
            return;
        }

        string title = "", artist = "", album = "", appId = session.SourceAppUserModelId ?? "";
        byte[]? thumb = null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            title = props.Title ?? "";
            artist = props.Artist ?? "";
            album = props.AlbumTitle ?? "";
            if (props.Thumbnail is not null)
                thumb = await ReadThumbAsync(props.Thumbnail);
        }
        catch { /* some sessions reject props */ }

        // 浏览器会话的标题常带「正在播放：」「_哔哩哔哩_bilibili」这类噪音，显示前清掉
        title = CleanTitle(title, out var artistFromTitle);
        if (string.IsNullOrWhiteSpace(artist)) artist = artistFromTitle;

        var play = session.GetPlaybackInfo();
        var tl = session.GetTimelineProperties();
        bool isPlaying = play.PlaybackStatus.ToString() == "Playing";
        var now = DateTime.UtcNow;
        double elapsedMs = _lastRefreshAt is { } last ? (now - last).TotalMilliseconds : 0;
        _lastRefreshAt = now;
        bool trackChanged = _simKey != $"{appId}|{title}";
        _simKey = $"{appId}|{title}";
        long posMs = AdvancePosition(
            _simPosMs, (long)tl.Position.TotalMilliseconds,
            (long)tl.EndTime.TotalMilliseconds, isPlaying, elapsedMs, trackChanged);
        _simPosMs = posMs;

        State = new MediaState
        {
            Active = true,
            Title = title,
            Artist = artist,
            Album = album,
            AppId = appId,
            Status = play.PlaybackStatus.ToString(),
            PositionMs = posMs,
            DurationMs = (long)tl.EndTime.TotalMilliseconds,
            Thumb = thumb,
        };
        Updated?.Invoke(State);
    }

    /// <summary>
    /// 进度推进规则（纯函数，自测钉边界）：
    /// 播放中本地按 elapsed 推进；SMTC 与预期偏差 &gt;1.5s（拖动/播放器自报）或换曲 → 以 SMTC 为准；
    /// 暂停 → 停在 SMTC 报告的位置；结果钳在 [0, duration]。
    /// </summary>
    internal static long AdvancePosition(long simPosMs, long smtcPosMs, long durationMs,
        bool playing, double elapsedMs, bool trackChanged)
    {
        long expected = simPosMs + (playing ? (long)Math.Max(0, elapsedMs) : 0);
        long pos = trackChanged || Math.Abs(smtcPosMs - expected) > 1500
            ? smtcPosMs
            : playing ? expected : smtcPosMs;
        if (durationMs > 0) pos = Math.Min(pos, durationMs);
        return Math.Max(0, pos);
    }

    private static async Task<byte[]?> ReadThumbAsync(IRandomAccessStreamReference thumbRef)
    {
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 2_000_000) return null;
            var buf = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)stream.Size);
            if (loaded == 0) return null;
            reader.ReadBytes(buf);
            return buf;
        }
        catch
        {
            return null;
        }
    }
}
