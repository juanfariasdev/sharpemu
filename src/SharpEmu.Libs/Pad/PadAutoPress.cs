// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Local benchmarking aid: SHARPEMU_AUTOPRESS_CROSS=start_s,period_ms,hold_ms,stop_s taps Cross
/// on a timer, counted from the first pad read, so a title can be driven past its menus without
/// anyone at the keyboard. It ignores window focus.
/// </summary>
internal static class PadAutoPress
{
    private static readonly (double Start, double Period, double Hold, double Stop)? Config = Parse();
    private static readonly long StartTick = Environment.TickCount64;

    public static uint Buttons()
    {
        if (Config is not { } config)
        {
            return 0;
        }

        var seconds = (Environment.TickCount64 - StartTick) / 1000.0;
        if (seconds < config.Start || seconds > config.Stop)
        {
            return 0;
        }

        var phaseMs = (seconds - config.Start) * 1000.0 % config.Period;
        return phaseMs < config.Hold ? OrbisPadButton.Cross : 0;
    }

    private static (double, double, double, double)? Parse()
    {
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_AUTOPRESS_CROSS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',');
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var hold) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var stop) ||
            period <= 0 || hold <= 0)
        {
            Console.Error.WriteLine($"[LOADER][WARN] SHARPEMU_AUTOPRESS_CROSS is malformed: '{raw}'.");
            return null;
        }

        Console.Error.WriteLine($"[LOADER][INFO] Auto-pressing Cross from {start}s to {stop}s every {period}ms for {hold}ms.");
        return (start, period, hold, stop);
    }
}
