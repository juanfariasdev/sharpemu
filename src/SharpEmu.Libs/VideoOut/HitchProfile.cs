// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// SHARPEMU_HITCH_MS=n: for every guest flip interval of at least n ms, writes what the
/// process did during that interval - GC pauses, shader and pipeline compilation, swapchain
/// recreation and (with SHARPEMU_PERF_HLE=1) the HLE exports that took the time.
/// </summary>
internal static class HitchProfile
{
    public static readonly double ThresholdMs =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_HITCH_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    public static bool Enabled => ThresholdMs > 0;

    /// <summary>Set by the CPU backend: resets its baseline and, when asked, describes the HLE cost since the previous call.</summary>
    public static Func<bool, string>? HleProbe;

    private static readonly object Gate = new();
    private static long _shaderCompiles;
    private static long _shaderTicks;
    private static long _pipelineCreations;
    private static long _pipelineTicks;
    private static long _swapchainRecreations;

    private static TimeSpan _lastPause = GC.GetTotalPauseDuration();
    private static int _lastGen0 = GC.CollectionCount(0);
    private static int _lastGen1 = GC.CollectionCount(1);
    private static int _lastGen2 = GC.CollectionCount(2);
    private static long _lastAllocated = GC.GetTotalAllocatedBytes(false);
    private static long _lastShaderCompiles;
    private static long _lastShaderTicks;
    private static long _lastPipelineCreations;
    private static long _lastPipelineTicks;
    private static long _lastSwapchainRecreations;

    public static void CountShaderCompile(long ticks)
    {
        Interlocked.Increment(ref _shaderCompiles);
        Interlocked.Add(ref _shaderTicks, ticks);
    }

    public static void CountPipelineCreation(long ticks)
    {
        Interlocked.Increment(ref _pipelineCreations);
        Interlocked.Add(ref _pipelineTicks, ticks);
    }

    public static void CountSwapchainRecreation() => Interlocked.Increment(ref _swapchainRecreations);

    public static void OnFlip(double intervalMs)
    {
        lock (Gate)
        {
            var hitch = intervalMs >= ThresholdMs;
            var pause = GC.GetTotalPauseDuration();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(false);
            var shaderCompiles = Interlocked.Read(ref _shaderCompiles);
            var shaderTicks = Interlocked.Read(ref _shaderTicks);
            var pipelineCreations = Interlocked.Read(ref _pipelineCreations);
            var pipelineTicks = Interlocked.Read(ref _pipelineTicks);
            var swapchainRecreations = Interlocked.Read(ref _swapchainRecreations);
            var hle = HleProbe?.Invoke(hitch);

            if (hitch)
            {
                Console.Error.WriteLine(
                    $"[HITCH] frame_ms={intervalMs:F0} " +
                    $"gc_pause_ms={(pause - _lastPause).TotalMilliseconds:F1} gc={gen0 - _lastGen0}/{gen1 - _lastGen1}/{gen2 - _lastGen2} " +
                    $"alloc_mb={(allocated - _lastAllocated) / (1024.0 * 1024.0):F1} " +
                    $"shaders={shaderCompiles - _lastShaderCompiles} shader_ms={ToMs(shaderTicks - _lastShaderTicks):F1} " +
                    $"pipelines={pipelineCreations - _lastPipelineCreations} pipeline_ms={ToMs(pipelineTicks - _lastPipelineTicks):F1} " +
                    $"swapchain={swapchainRecreations - _lastSwapchainRecreations}" +
                    (string.IsNullOrEmpty(hle) ? string.Empty : $" hle: {hle}"));
            }

            _lastPause = pause;
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;
            _lastAllocated = allocated;
            _lastShaderCompiles = shaderCompiles;
            _lastShaderTicks = shaderTicks;
            _lastPipelineCreations = pipelineCreations;
            _lastPipelineTicks = pipelineTicks;
            _lastSwapchainRecreations = swapchainRecreations;
        }
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
