// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;

namespace SharpEmu.Libs.VideoOut;

/// <summary>A host GPU utilization sampler for the performance overlay.</summary>
internal interface IHostGpuUsage : IDisposable
{
    /// <summary>Latest utilization in percent, or NaN when unavailable.</summary>
    double Percent { get; }

    /// <summary>Starts a sample off the calling thread; no-op while one is pending.</summary>
    void RequestSample();
}

internal static class HostGpuUsage
{
    public static IHostGpuUsage? Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsGpuUsage();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacGpuUsage();
        }

        return null;
    }
}

/// <summary>
/// Samples the IOAccelerator's "Device Utilization %". macOS publishes no
/// public per-process GPU counter, so this is the whole device's usage.
/// </summary>
internal sealed class MacGpuUsage : IHostGpuUsage
{
    private int _samplePending;
    private int _disposed;
    private double _percent = double.NaN;

    public double Percent => Volatile.Read(ref _percent);

    public void RequestSample()
    {
        if (!OperatingSystem.IsMacOS() ||
            Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _samplePending, 1, 0) != 0)
        {
            return;
        }

        // Registry reads are cheap but still syscalls; keep them off the presentation thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Volatile.Write(ref _percent, MacAccelerator.ReadDeviceUtilization());
                }
            }
            finally
            {
                Volatile.Write(ref _samplePending, 0);
            }
        });
    }

    public void Dispose() => Volatile.Write(ref _disposed, 1);
}
