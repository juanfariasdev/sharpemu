// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

// View mappings bypass the anonymous-allocation tables. Keep their query state together.
internal static class PosixViewRegions
{
    internal static readonly object Gate = new();
    private static readonly PosixViewRegionTable Table = new((ulong)Environment.SystemPageSize);

    internal static void Replace(ulong address, ulong size, uint state, uint protection)
    {
        HostMemory.OnMappingChanged();
        Table.Replace(address, size, state, protection);
    }

    // Mapping creates the storage. Fault-time protection updates must not allocate.
    internal static void ChangeProtection(ulong address, ulong size, uint protection)
    {
        HostMemory.OnMappingChanged();
        Table.ChangeProtection(address, size, protection);
    }

    internal static bool TryQuery(ulong address, out HostMemory.BasicInfo info)
    {
        lock (Gate)
        {
            return Table.TryQuery(address, out info);
        }
    }

    internal static uint RawProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => HostMemory.PAGE_NOACCESS,
        HostPageProtection.ReadOnly => HostMemory.PAGE_READONLY,
        HostPageProtection.ReadWrite => HostMemory.PAGE_READWRITE,
        HostPageProtection.Execute => HostMemory.PAGE_EXECUTE,
        HostPageProtection.ReadExecute => HostMemory.PAGE_EXECUTE_READ,
        HostPageProtection.ReadWriteExecute or HostPageProtection.ExecuteWriteCopy => HostMemory.PAGE_EXECUTE_READWRITE,
        _ => throw new ArgumentOutOfRangeException(nameof(protection)),
    };
}

/// <summary>
/// Sorted, disjoint view regions. A game maps and unmaps thousands of views, so every
/// update touches only the regions around the change; rebuilding and re-sorting the whole
/// table per mmap cost about half a millisecond each once it held a few thousand entries.
/// </summary>
internal sealed class PosixViewRegionTable
{
    internal readonly record struct Region(ulong Start, ulong End, uint State, uint Protection,
        uint[]? PageProtections, ulong PageBase);

    private readonly List<Region> _regions = new();
    private readonly ulong _pageSize;

    public PosixViewRegionTable(ulong pageSize) => _pageSize = pageSize;

    internal IReadOnlyList<Region> Regions => _regions;

    public void Replace(ulong address, ulong size, uint state, uint protection)
    {
        var end = RoundPageEnd(address, size);
        var first = FirstEndingAbove(address);
        var last = first;
        while (last < _regions.Count && _regions[last].Start < end)
        {
            last++;
        }

        // The parts of the overlapped regions outside the new range survive, and keep their
        // page protection arrays, which stay indexed from their original page base.
        Region? head = null;
        Region? tail = null;
        if (first < last)
        {
            if (_regions[first].Start < address)
            {
                head = _regions[first] with { End = address };
            }

            if (_regions[last - 1].End > end)
            {
                tail = _regions[last - 1] with { Start = end };
            }
        }

        _regions.RemoveRange(first, last - first);
        var next = first;
        if (head is { } headRegion)
        {
            _regions.Insert(next++, headRegion);
        }

        if (state != HostMemory.MEM_FREE_STATE)
        {
            uint[]? pages = null;
            if (state == HostMemory.MEM_COMMIT)
            {
                pages = new uint[checked((int)((end - address) / _pageSize))];
                Array.Fill(pages, protection);
            }

            _regions.Insert(next++, new Region(address, end, state, protection, pages, address));
        }

        if (tail is { } tailRegion)
        {
            _regions.Insert(next++, tailRegion);
        }

        // The rest of the table is already merged, so only pairs touching the change can join.
        MergeNeighbours(first - 1, next);
    }

    public void ChangeProtection(ulong address, ulong size, uint protection)
    {
        var end = RoundPageEnd(address, size);
        for (var index = FirstEndingAbove(address); index < _regions.Count; index++)
        {
            var region = _regions[index];
            if (region.Start >= end) break;
            if (region.PageProtections is null) continue;
            var startIndex = (int)((Math.Max(address, region.Start) - region.PageBase) / _pageSize);
            var endIndex = (int)((Math.Min(end, region.End) - region.PageBase) / _pageSize);
            Array.Fill(region.PageProtections, protection, startIndex, endIndex - startIndex);
        }
    }

    public bool TryQuery(ulong address, out HostMemory.BasicInfo info)
    {
        var index = FirstEndingAbove(address);
        if (index < _regions.Count && _regions[index].Start <= address)
        {
            var region = _regions[index];
            var start = address - address % _pageSize;
            var end = region.End;
            var protection = region.Protection;
            if (region.PageProtections is { } pages)
            {
                var pageIndex = (int)((start - region.PageBase) / _pageSize);
                protection = pages[pageIndex];
                end = start + _pageSize;
                while (end < region.End && pages[++pageIndex] == protection) end += _pageSize;
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

    // Regions are disjoint and sorted, so their ends are sorted too.
    private int FirstEndingAbove(ulong address)
    {
        var lower = 0;
        var upper = _regions.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (_regions[middle].End <= address)
                lower = middle + 1;
            else
                upper = middle;
        }

        return lower;
    }

    // Same rule the full rebuild applied: a region without page protections joins an
    // adjacent predecessor of the same state and protection.
    private void MergeNeighbours(int from, int to)
    {
        var index = Math.Max(from, 0) + 1;
        while (index <= to && index < _regions.Count)
        {
            var previous = _regions[index - 1];
            var region = _regions[index];
            if (region.PageProtections is null && previous.End == region.Start &&
                previous.State == region.State && previous.Protection == region.Protection)
            {
                _regions[index - 1] = previous with { End = region.End };
                _regions.RemoveAt(index);
                to--;
            }
            else
            {
                index++;
            }
        }
    }

    private ulong RoundPageEnd(ulong address, ulong size)
    {
        var end = checked(address + size);
        var remainder = end % _pageSize;
        return remainder == 0 ? end : checked(end + _pageSize - remainder);
    }
}
