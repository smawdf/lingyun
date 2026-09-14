// Derived from NotchPeninsula (https://github.com/GEORGEWWWU/NotchPeninsula) AudioAnalyzer.cs,
// Apache-2.0. Modifications for LingYun:
//  - IDisposable lifecycle (stops WASAPI capture instead of running for process lifetime)
//  - Available flag for silent degradation (no device / exclusive mode → UI falls back to fake bars)
//  - DSP block math extracted into a pure internal function so --self-test can feed synthetic tones
//  - No dependency on NotchPeninsula's Logger
using NAudio.Wave;

namespace LingYun.Services;

/// <summary>
/// 系统混音实时频谱：WASAPI 环回捕获 → 降采样 ~8kHz → 5 频段 Goertzel → AGC。
/// 输出 5 根 0..1 的柱子，渲染线程每帧直接读 <see cref="Bands"/>（无锁双缓冲）。
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    /// <summary>5 个目标频段（Hz）：底鼓 / 军鼓 / 人声 / 乐器高频 / 镲片。</summary>
    internal static readonly float[] TargetFreqs = { 80f, 250f, 600f, 1500f, 3500f };
    /// <summary>高频能量补偿倍率：频率越高现实能量越小，越要放大视觉效果。</summary>
    internal static readonly float[] Weights = { 1.0f, 1.8f, 2.8f, 4.5f, 6.5f };
    /// <summary>每多少个降采样样本出一帧能量（8kHz 下 ≈ 32ms）。</summary>
    internal const int BlockSize = 256;

    private WasapiLoopbackCapture? _capture;
    private volatile float[] _front = new float[5];
    private float[] _back = new float[5];
    private readonly float[] _temp = new float[5];

    // Goertzel 递推状态：coeff 固定，q1/q2 每样本推进、每块清零
    private readonly (float coeff, float q1, float q2, float weight)[] _state =
        new (float, float, float, float)[5];
    private int _sampleCount;
    private int _channels = 2;
    private int _decimation = 1;
    private float _peak = 0.1f; // AGC 包络：瞬时攻击 / 每块 ×0.98 释放

    /// <summary>WASAPI 捕获是否成功启动；false 时 UI 应回退假动画。</summary>
    public bool Available => _capture is not null;
    /// <summary>当前 5 根柱子（0..1）。返回内部缓冲，只读约定。</summary>
    public float[] Bands => _front;

    /// <summary>诊断用：捕获格式描述，说明采样率/声道/降采样比。</summary>
    internal string FormatInfo => _capture is null
        ? "不可用（无播放设备 / 独占模式 / 权限不足）"
        : $"{_capture.WaveFormat.SampleRate}Hz {_capture.WaveFormat.Channels}ch"
          + $" → 降采样 1/{_decimation} ≈ {_capture.WaveFormat.SampleRate / _decimation}Hz";

    public AudioSpectrumService()
    {
        try
        {
            _capture = new WasapiLoopbackCapture(); // 默认渲染设备环回
            _channels = _capture.WaveFormat.Channels;
            // 极速降采样：48000Hz → 每 6 个样本取 1 个 ≈ 8000Hz
            _decimation = Math.Max(1, _capture.WaveFormat.SampleRate / 8000);
            int actualRate = _capture.WaveFormat.SampleRate / _decimation;

            for (int i = 0; i < 5; i++)
            {
                float freq = Math.Min(TargetFreqs[i], actualRate / 2.2f);
                float k = MathF.Round(freq * 256f / actualRate);
                float coeff = 2f * MathF.Cos(2f * MathF.PI * k / 256f);
                _state[i] = (coeff, 0, 0, Weights[i]);
            }

            _capture.DataAvailable += OnAudioData;
            _capture.RecordingStopped += (_, _) =>
            {
                // 设备拔出/停止：清零并发布
                Array.Clear(_back, 0, 5);
                _front = Interlocked.Exchange(ref _back, _front);
            };
            _capture.StartRecording();
        }
        catch
        {
            // 无设备 / 独占模式 / 权限失败：静默降级，Bands 恒 0
            _capture = null;
        }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        var buffer = new WaveBuffer(e.Buffer);
        int floatCount = e.BytesRecorded / 4;
        int stride = _channels * _decimation;

        for (int i = 0; i < floatCount; i += stride)
        {
            float sample = buffer.FloatBuffer[i]; // 左声道
            for (int j = 0; j < 5; j++)
            {
                ref var s = ref _state[j];
                float q0 = sample + s.coeff * s.q1 - s.q2;
                s.q2 = s.q1;
                s.q1 = q0;
            }

            if (++_sampleCount >= BlockSize)
            {
                _sampleCount = 0;
                FinishBlock(_state, _temp, ref _peak);
                // 无锁双缓冲原子交换
                Array.Copy(_temp, _back, 5);
                _front = Interlocked.Exchange(ref _back, _front);
            }
        }
    }

    /// <summary>
    /// 一个 256 样本块的收尾：能量 → 加权 → AGC。抽成纯函数供自测喂合成信号。
    /// state 的 q1/q2 会被清零（Goertzel 每块重置）；peak 为 AGC 包络（ref 保持跨块记忆）。
    /// </summary>
    internal static void FinishBlock(
        (float coeff, float q1, float q2, float weight)[] state,
        float[] bars, ref float peak)
    {
        float maxVal = 0f;
        for (int j = 0; j < state.Length; j++)
        {
            ref var s = ref state[j];
            float power = s.q1 * s.q1 + s.q2 * s.q2 - s.coeff * s.q1 * s.q2;
            float val = (MathF.Sqrt(Math.Max(0, power)) / BlockSize) * 20f * s.weight;
            bars[j] = val;
            if (val > maxVal) maxVal = val;
            s.q1 = 0;
            s.q2 = 0;
        }

        // AGC：包络快升慢降 → 底噪红线 0.02 → 目标 0.9、增益钳 1..30 → 最终钳 0..1
        if (maxVal > peak) peak = maxVal;
        else peak *= 0.98f;
        float safePeak = Math.Max(peak, 0.02f);
        float gain = Math.Clamp(0.9f / safePeak, 1f, 30f);
        for (int j = 0; j < bars.Length; j++)
            bars[j] = Math.Clamp(bars[j] * gain, 0f, 1f);
    }

    public void Dispose()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnAudioData;
            capture.StopRecording();
        }
        catch { /* ignore */ }
        try { capture.Dispose(); } catch { /* ignore */ }
        Array.Clear(_front, 0, 5);
    }
}
