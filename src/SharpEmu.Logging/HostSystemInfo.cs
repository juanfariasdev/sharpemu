// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SharpEmu.Logging;

/// <summary>Provides best-effort information about the host system.</summary>
public static class HostSystemInfo
{
    private static readonly Lazy<string> CpuNameValue = new(GetCpuName);
    private static readonly Lazy<string> GpuNameValue = new(GetPreferredGpuName);
    private static readonly Lazy<string> MemoryDescriptionValue = new(GetMemoryDescription);

    /// <summary>Host CPU name, or a safe fallback when it cannot be determined.</summary>
    public static string CpuName => CpuNameValue.Value;

    /// <summary>Preferred physical GPU name, or a safe fallback when it cannot be determined.</summary>
    public static string GpuName => GpuNameValue.Value;

    /// <summary>Returns a concise description of the host hardware for diagnostic logs.</summary>
    public static string Summary =>
        $"Host hardware: CPU: {CpuName}; GPU: {GpuName}; RAM: {MemoryDescriptionValue.Value}.";

    private static string GetCpuName()
    {
        if (OperatingSystem.IsMacOS() && ReadSysctlString("machdep.cpu.brand_string") is { } macName)
        {
            return $"{macName} ({Environment.ProcessorCount} logical processors)";
        }

        if (!OperatingSystem.IsWindows())
        {
            return $"{Environment.ProcessorCount} logical processors";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                name = Regex.Replace(
                    name.Trim(),
                    @"\s+\d+-Core Processor$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return Regex.Replace(
                    name,
                    @"\s+with Radeon Graphics$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
        }

        return $"{Environment.ProcessorCount} logical processors";
    }

    private static string GetPreferredGpuName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetMacGpuName();
        }

        if (!OperatingSystem.IsWindows())
        {
            return "unknown";
        }

        try
        {
            var preferredName = "unknown";
            var preferredScore = int.MinValue;
            for (uint index = 0; ; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>(),
                };

                if (!EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                var name = device.DeviceString?.Trim();
                var score = ScoreGpu(name);
                if (score > preferredScore)
                {
                    preferredName = name!;
                    preferredScore = score;
                }
            }

            return preferredScore > 0 ? preferredName : "unknown";
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
            return "unknown";
        }
    }

    private static string GetMacGpuName()
    {
        var model = MacAccelerator.TryGetModel();
        if (model is null)
        {
            return "unknown";
        }

        return MacAccelerator.TryGetCoreCount() is { } cores ? $"{model} ({cores}-core GPU)" : model;
    }

    private static int ScoreGpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("basic display", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("geforce", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("radeon", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private static string GetMemoryDescription()
    {
        if (OperatingSystem.IsMacOS())
        {
            ulong totalBytes = 0;
            nuint size = sizeof(ulong);
            if (sysctlbyname("hw.memsize", ref totalBytes, ref size, 0, 0) == 0 && totalBytes > 0)
            {
                return FormatMemory(totalBytes);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx
                {
                    dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
                };
                if (GlobalMemoryStatusEx(ref status))
                {
                    return FormatMemory(status.ullTotalPhys);
                }
            }
            catch (Exception)
            {
                // Hardware information is diagnostic only.
            }
        }

        return "unknown";
    }

    private static string FormatMemory(ulong totalBytes)
    {
        var megabytes = totalBytes / (1024 * 1024);
        var gigabytes = totalBytes / (1024d * 1024 * 1024);
        return $"{megabytes:N0} MB ({gigabytes:N1} GB)";
    }

    private static string? ReadSysctlString(string name)
    {
        try
        {
            nuint size = 0;
            if (sysctlbyname(name, null, ref size, 0, 0) != 0 || size == 0 || size > 1024)
            {
                return null;
            }

            var buffer = new byte[size];
            if (sysctlbyname(name, buffer, ref size, 0, 0) != 0)
            {
                return null;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint deviceNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("libSystem.B.dylib")]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? value, ref nuint size, nint newValue, nuint newSize);

    [DllImport("libSystem.B.dylib")]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref ulong value, ref nuint size, nint newValue, nuint newSize);
}
