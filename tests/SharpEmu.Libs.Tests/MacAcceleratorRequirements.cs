// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class MacAcceleratorFactAttribute : FactAttribute
{
    public MacAcceleratorFactAttribute()
    {
        // Virtualized macOS hosts such as CI runners may publish no accelerator statistics.
        if (!OperatingSystem.IsMacOS() || !double.IsFinite(MacAccelerator.ReadDeviceUtilization()))
            Skip = "This test requires a macOS GPU that publishes IOAccelerator utilization.";
    }
}
