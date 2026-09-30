// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host.Posix;
using Xunit;
using Region = SharpEmu.HLE.Host.Posix.PosixViewRegionTable.Region;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class PosixViewRegionTableTests
{
    private static readonly uint[] States = [HostMemory.MEM_COMMIT, HostMemory.MEM_RESERVE, HostMemory.MEM_FREE_STATE];
    private static readonly uint[] Protections = [HostMemory.PAGE_NOACCESS, HostMemory.PAGE_READONLY, HostMemory.PAGE_READWRITE];

    [Theory]
    [InlineData(4096UL, 1)]
    [InlineData(4096UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(16384UL, 4)]
    public void IncrementalUpdatesMatchTheFullRebuild(ulong pageSize, int seed)
    {
        var random = new System.Random(seed);
        var table = new PosixViewRegionTable(pageSize);
        var reference = new List<Region>();
        const int pages = 96;

        for (var step = 0; step < 4000; step++)
        {
            var address = (ulong)random.Next(pages) * pageSize;
            // Sizes are not always page multiples, as with the real callers' byte lengths.
            var size = (ulong)random.Next(1, 24) * pageSize - (random.Next(4) == 0 ? pageSize / 2 : 0);
            if (random.Next(4) == 0)
            {
                var protection = Protections[random.Next(Protections.Length)];
                table.ChangeProtection(address, size, protection);
                ReferenceChangeProtection(reference, pageSize, address, size, protection);
            }
            else
            {
                var state = States[random.Next(States.Length)];
                var protection = Protections[random.Next(Protections.Length)];
                table.Replace(address, size, state, protection);
                ReferenceReplace(reference, pageSize, address, size, state, protection);
            }

            AssertSameRegions(reference, table.Regions, step);
            var probe = (ulong)random.Next(pages + 8) * pageSize + (ulong)random.Next((int)pageSize);
            Assert.Equal(ReferenceTryQuery(reference, pageSize, probe, out var expected), table.TryQuery(probe, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ReplaceSplitsAnEnclosingRegionAndMergesUncommittedNeighbours()
    {
        const ulong page = 4096;
        var table = new PosixViewRegionTable(page);
        table.Replace(0, 8 * page, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
        table.Replace(2 * page, 2 * page, HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
        Assert.Equal(3, table.Regions.Count);

        table.Replace(2 * page, 2 * page, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
        var region = Assert.Single(table.Regions);
        Assert.Equal((0UL, 8 * page, HostMemory.MEM_RESERVE), (region.Start, region.End, region.State));
    }

    private static void AssertSameRegions(List<Region> expected, IReadOnlyList<Region> actual, int step)
    {
        Assert.True(expected.Count == actual.Count, $"step {step}: {expected.Count} regions expected, {actual.Count} found");
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            Assert.True(
                (left.Start, left.End, left.State, left.Protection, left.PageBase) ==
                (right.Start, right.End, right.State, right.Protection, right.PageBase),
                $"step {step}, region {index}: {left} != {right}");
            Assert.Equal(left.PageProtections, right.PageProtections);
        }
    }

    // The previous implementation, which rebuilt and re-sorted the whole table per update.
    private static void ReferenceReplace(List<Region> regions, ulong pageSize, ulong address, ulong size, uint state, uint protection)
    {
        var end = RoundPageEnd(pageSize, address, size);
        var replacement = new List<Region>(regions.Count + 2);
        foreach (var region in regions)
        {
            if (region.End <= address || region.Start >= end)
            {
                replacement.Add(region);
                continue;
            }
            if (region.Start < address) replacement.Add(region with { End = address });
            if (region.End > end) replacement.Add(region with { Start = end });
        }
        if (state != HostMemory.MEM_FREE_STATE)
        {
            uint[]? pages = null;
            if (state == HostMemory.MEM_COMMIT)
            {
                pages = new uint[checked((int)((end - address) / pageSize))];
                Array.Fill(pages, protection);
            }
            replacement.Add(new Region(address, end, state, protection, pages, address));
        }
        replacement.Sort((left, right) => left.Start.CompareTo(right.Start));
        regions.Clear();
        foreach (var region in replacement)
        {
            if (region.PageProtections is null && regions.Count > 0 && regions[^1].End == region.Start &&
                regions[^1].State == region.State && regions[^1].Protection == region.Protection)
                regions[^1] = regions[^1] with { End = region.End };
            else
                regions.Add(region);
        }
    }

    private static void ReferenceChangeProtection(List<Region> regions, ulong pageSize, ulong address, ulong size, uint protection)
    {
        var end = RoundPageEnd(pageSize, address, size);
        foreach (var region in regions)
        {
            if (region.Start >= end) break;
            if (region.End <= address || region.PageProtections is null) continue;
            var startIndex = (int)((Math.Max(address, region.Start) - region.PageBase) / pageSize);
            var endIndex = (int)((Math.Min(end, region.End) - region.PageBase) / pageSize);
            Array.Fill(region.PageProtections, protection, startIndex, endIndex - startIndex);
        }
    }

    private static bool ReferenceTryQuery(List<Region> regions, ulong pageSize, ulong address, out HostMemory.BasicInfo info)
    {
        foreach (var region in regions)
        {
            if (region.Start > address) break;
            if (address >= region.End) continue;
            var start = address - address % pageSize;
            var end = region.End;
            var protection = region.Protection;
            if (region.PageProtections is { } pages)
            {
                var index = (int)((start - region.PageBase) / pageSize);
                protection = pages[index];
                end = start + pageSize;
                while (end < region.End && pages[++index] == protection) end += pageSize;
            }
            info = new HostMemory.BasicInfo
            {
                BaseAddress = start,
                AllocationBase = region.Start,
                RegionSize = end - start,
                State = region.State,
                Protect = protection,
                AllocationProtect = region.Protection,
            };
            return true;
        }
        info = default;
        return false;
    }

    private static ulong RoundPageEnd(ulong pageSize, ulong address, ulong size)
    {
        var end = checked(address + size);
        var remainder = end % pageSize;
        return remainder == 0 ? end : checked(end + pageSize - remainder);
    }
}
