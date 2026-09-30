// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// The process's physical footprint on macOS, the figure Activity Monitor's Memory
/// column and footprint(1) report. Unlike the managed heap size it includes the
/// guest's mapped memory and the graphics driver's allocations.
/// </summary>
internal static class HostProcessMemory
{
    private const int RusageInfoV0 = 0;

    // rusage_info_v0 is a 16-byte UUID followed by ten 64-bit fields;
    // ri_phys_footprint is the eighth of them.
    private const int RusageInfoV0Size = 16 + 10 * sizeof(ulong);
    private const int PhysFootprintOffset = 16 + 7 * sizeof(ulong);

    public static unsafe bool TryGetFootprintBytes(out ulong bytes)
    {
        bytes = 0;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var info = stackalloc byte[RusageInfoV0Size];
            if (proc_pid_rusage(Environment.ProcessId, RusageInfoV0, info) != 0)
            {
                return false;
            }

            bytes = *(ulong*)(info + PhysFootprintOffset);
            return bytes != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libSystem.B.dylib")]
    private static extern unsafe int proc_pid_rusage(int pid, int flavor, byte* buffer);
}
