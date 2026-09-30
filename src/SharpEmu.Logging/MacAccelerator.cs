// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.Logging;

/// <summary>
/// Reads the macOS GPU's IOAccelerator registry entry: its model name, core
/// count and the utilization the driver publishes in PerformanceStatistics.
/// None of these need elevated privileges, and the system frameworks are
/// universal, so this works in arm64 processes and under Rosetta 2 alike.
/// </summary>
public static class MacAccelerator
{
    private const string IOKitLibrary = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KIOMainPortDefault = 0;
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const int CFNumberSInt64Type = 4;

    /// <summary>Returns the GPU model (for example "Apple M4 Pro"), or null.</summary>
    public static string? TryGetModel()
    {
        string? model = null;
        ForEachAccelerator(service =>
        {
            model = ReadStringProperty(service, "model");
            return model is null;
        });
        return model;
    }

    /// <summary>Returns the GPU core count Apple Silicon publishes, or null.</summary>
    public static int? TryGetCoreCount()
    {
        long? cores = null;
        ForEachAccelerator(service =>
        {
            cores = ReadNumberProperty(service, "gpu-core-count");
            return cores is null;
        });
        return cores is > 0 and <= int.MaxValue ? (int)cores : null;
    }

    /// <summary>
    /// Returns the busiest accelerator's "Device Utilization %", or NaN when
    /// it cannot be read. The driver reports the whole device, not one process.
    /// </summary>
    public static double ReadDeviceUtilization()
    {
        var percent = double.NaN;
        ForEachAccelerator(service =>
        {
            var statistics = CreateProperty(service, "PerformanceStatistics");
            if (statistics == 0)
            {
                return true;
            }

            try
            {
                if (CFGetTypeID(statistics) == CFDictionaryGetTypeID() &&
                    ReadDictionaryNumber(statistics, "Device Utilization %") is { } value)
                {
                    percent = IncludeAccelerator(percent, value);
                }
            }
            finally
            {
                CFRelease(statistics);
            }

            return true;
        });
        return percent;
    }

    // A Mac can register more than one accelerator (dual-GPU Intel models, an eGPU).
    // The busiest one stands for the host, as the Windows sampler takes the busiest engine.
    internal static double IncludeAccelerator(double current, long value)
    {
        if (value < 0)
        {
            return current;
        }

        return Math.Max(double.IsNaN(current) ? 0 : current, Math.Min(100, value));
    }

    private static void ForEachAccelerator(Func<uint, bool> visit)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            // IOServiceGetMatchingServices consumes the matching dictionary.
            var matching = IOServiceMatching("IOAccelerator");
            if (matching == 0 || IOServiceGetMatchingServices(KIOMainPortDefault, matching, out var iterator) != 0)
            {
                return;
            }

            try
            {
                for (var service = IOIteratorNext(iterator); service != 0; service = IOIteratorNext(iterator))
                {
                    var keepGoing = visit(service);
                    IOObjectRelease(service);
                    if (!keepGoing)
                    {
                        break;
                    }
                }
            }
            finally
            {
                IOObjectRelease(iterator);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static nint CreateProperty(uint service, string key)
    {
        var cfKey = CreateCFString(key);
        if (cfKey == 0)
        {
            return 0;
        }

        try
        {
            return IORegistryEntryCreateCFProperty(service, cfKey, 0, 0);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static string? ReadStringProperty(uint service, string key)
    {
        var value = CreateProperty(service, key);
        if (value == 0)
        {
            return null;
        }

        try
        {
            var typeId = CFGetTypeID(value);
            if (typeId == CFStringGetTypeID())
            {
                return ToManagedString(value);
            }

            // Intel Macs publish the model as a NUL-terminated CFData.
            if (typeId == CFDataGetTypeID())
            {
                var length = (int)Math.Min(CFDataGetLength(value), 256);
                var bytes = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(value), bytes, 0, length);
                var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                return text.Length == 0 ? null : text;
            }

            return null;
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static long? ReadNumberProperty(uint service, string key)
    {
        var value = CreateProperty(service, key);
        if (value == 0)
        {
            return null;
        }

        try
        {
            return ToInt64(value);
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static long? ReadDictionaryNumber(nint dictionary, string key)
    {
        var cfKey = CreateCFString(key);
        if (cfKey == 0)
        {
            return null;
        }

        try
        {
            // Get rule: the value is owned by the dictionary.
            var value = CFDictionaryGetValue(dictionary, cfKey);
            return value == 0 ? null : ToInt64(value);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static long? ToInt64(nint value)
    {
        if (CFGetTypeID(value) != CFNumberGetTypeID())
        {
            return null;
        }

        return CFNumberGetValue(value, CFNumberSInt64Type, out var number) ? number : null;
    }

    private static string? ToManagedString(nint value)
    {
        var buffer = new byte[256];
        if (!CFStringGetCString(value, buffer, buffer.Length, CFStringEncodingUtf8))
        {
            return null;
        }

        var terminator = Array.IndexOf(buffer, (byte)0);
        var text = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator).Trim();
        return text.Length == 0 ? null : text;
    }

    private static nint CreateCFString(string text) =>
        CFStringCreateWithCString(0, text, CFStringEncodingUtf8);

    [DllImport(IOKitLibrary)]
    private static extern nint IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(IOKitLibrary)]
    private static extern int IOServiceGetMatchingServices(uint mainPort, nint matching, out uint iterator);

    [DllImport(IOKitLibrary)]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKitLibrary)]
    private static extern int IOObjectRelease(uint entry);

    [DllImport(IOKitLibrary)]
    private static extern nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFStringCreateWithCString(
        nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundationLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDictionaryGetValue(nint dictionary, nint key);

    [DllImport(CoreFoundationLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFNumberGetValue(nint number, int type, out long value);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDataGetLength(nint data);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFDataGetBytePtr(nint data);

    [DllImport(CoreFoundationLibrary)]
    private static extern nuint CFGetTypeID(nint value);

    [DllImport(CoreFoundationLibrary)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundationLibrary)]
    private static extern nuint CFDataGetTypeID();

    [DllImport(CoreFoundationLibrary)]
    private static extern nuint CFNumberGetTypeID();

    [DllImport(CoreFoundationLibrary)]
    private static extern nuint CFDictionaryGetTypeID();

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFRelease(nint value);
}
