// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The packet a handler executes: header fields plus its position in the cursor.
public readonly record struct PacketContext(uint Header, ulong PacketAddress, uint Offset, uint Remaining, uint Total)
{
    public uint Length => PacketHeader.Length(Header);

    public uint Opcode => PacketHeader.Opcode(Header);

    public uint CustomCode => PacketHeader.CustomCode(Header);
}

public readonly record struct FlipRequest(int Handle, int Index, int FlipMode, long FlipArgument);

// Parses one queue's command stream and keeps that queue's register and packet state.
public sealed partial class GpuCommandInterpreter
{
    public const int ComputeQueueBase = 0x20;
    public const uint ConstantRamDwords = 0x3000;
    public const uint RingChunkBytes = 0x10000;

    private readonly ICommandStreamHost _host;
    private readonly List<uint[]> _payloadScratchByDepth = new();
    private readonly uint[] _constantRam = new uint[ConstantRamDwords];
    private PacketCursorStack? _execution;
    private bool _chainRequested;

    public GpuCommandInterpreter(ICommandStreamHost host, int queueId, int interruptEventId)
    {
        _host = host;
        QueueId = queueId;
        InterruptEventId = interruptEventId;
        TypedRegisters = new RegisterBanks(host.Fatal);
    }

    public int QueueId { get; }

    public int InterruptEventId { get; }

    public bool IsComputeQueue => InterruptEventId >= ComputeQueueBase;

    public CommandRegisterBanks Registers { get; } = new();

    // The typed banks the render executor reads; the dictionaries feed the translation they replace.
    public RegisterBanks TypedRegisters { get; }

    public ulong SubmitId { get; set; }

    public uint IndexTypeAndSize { get; private set; }

    public uint IndexBufferSize { get; private set; }

    public ulong IndexBaseAddress { get; private set; }

    public ulong DrawIndirectArgumentsBase { get; private set; }

    public ulong DispatchIndirectArgumentsBase { get; private set; }

    // Persistent draw state: indirect draws update it for later draws.
    public uint InstanceCount { get; private set; } = 1;

    public uint DrawIndexOffset { get; private set; }

    public uint UserDataMarker { get; private set; }

    public bool PredicateSkip { get; private set; }

    public bool ConditionalWaitEnabled { get; private set; }

    public FlipRequest PendingFlip { get; private set; }

    public uint ConstantEngineCount { get; private set; }

    public uint DrawEngineCount { get; private set; }

    public bool ConstantEngineComplete { get; set; }

    public ulong SyntheticOcclusionCounter { get; private set; }

    public ulong AtomicReturnMeData { get; private set; }

    public bool AtomicReturnMeValid { get; private set; }

    public ulong AtomicReturnPfpData { get; private set; }

    public bool AtomicReturnPfpValid { get; private set; }

    public ReadOnlySpan<uint> ConstantRam => _constantRam;

    public void Reset()
    {
        Registers.Reset();
        TypedRegisters.Reset();
        IndexTypeAndSize = 0;
        IndexBufferSize = 0;
        UserDataMarker = 0;
        DrawIndirectArgumentsBase = 0;
        DispatchIndirectArgumentsBase = 0;
        Array.Clear(_constantRam);
    }

    // The queue-reset packets also drop the index base, the instance count and the wait condition.
    public void ResetForQueueReset()
    {
        Reset();
        IndexBaseAddress = 0;
        InstanceCount = 1;
        DrawIndexOffset = 0;
        ConditionalWaitEnabled = false;
        _host.OnQueueReset(QueueId);
    }

    public void ResetCounters()
    {
        DrawEngineCount = 0;
        ConstantEngineCount = 0;
        ConstantEngineComplete = false;
    }

    public void SetFlip(FlipRequest flip) => PendingFlip = flip;

    public SubmissionProgress Process(PacketCursorStack execution, ulong address, uint dwordCount)
    {
        if (_execution is not null)
        {
            throw _host.Fatal($"The command interpreter is already running a stream: queue={QueueId}.");
        }

        if (execution.IsEmpty && dwordCount != 0)
        {
            execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        }

        execution.Suspended = false;
        execution.MadeProgress = false;
        // A later slice may see ring memory the guest has appended since.
        InvalidatePacketCache();
        _execution = execution;
        try
        {
            RunPackets(execution, 0);
        }
        finally
        {
            _execution = null;
        }

        return execution.IsEmpty ? SubmissionProgress.Complete : SubmissionProgress.Blocked;
    }

    // Runs a nested buffer to completion before the calling packet finishes.
    public void RunIndirectBuffer(ulong address, uint dwordCount)
    {
        var execution = RequireExecution();
        if (dwordCount == 0)
        {
            return;
        }

        if (execution.Depth >= PacketCursorStack.MaxDepth)
        {
            throw _host.Fatal($"The indirect buffer nesting is too deep: depth={execution.Depth} address=0x{address:X16} dwords={dwordCount}.");
        }

        var stopDepth = execution.Depth;
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        RunPackets(execution, stopDepth);
    }

    // Replaces the current buffer with another one at the same depth; the chaining packet ends its buffer.
    private void ChainToBuffer(ulong address, uint dwordCount, bool followedChunkAdvance = false)
    {
        var execution = RequireExecution();
        execution.Pop();
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address, followedChunkAdvance));
        _chainRequested = true;
    }

    public void Suspend() => RequireExecution().Suspended = true;

    // Read-ahead of the packet stream. Each packet cost two synchronised guest reads (header,
    // then payload), about 2,700 per Dead Cells frame on the render thread; one read now serves
    // the headers and payloads of the packets that follow. Waits still read memory directly,
    // and the cache is dropped at every slice, at unwritten ring memory, on command-processor
    // writes into it and after host-side copies.
    private const int PacketCacheDwords = 256;
    private readonly uint[] _packetCache = new uint[PacketCacheDwords];
    private ulong _packetCacheAddress;
    private int _packetCacheCount;

    private void InvalidatePacketCache() => _packetCacheCount = 0;

    private bool TryGetCachedPackets(ulong address, int dwords, out ReadOnlySpan<uint> values)
    {
        values = default;
        if (_packetCacheCount == 0 || address < _packetCacheAddress || ((address - _packetCacheAddress) & 3) != 0)
        {
            return false;
        }

        var index = (address - _packetCacheAddress) / sizeof(uint);
        if (index + (ulong)dwords > (ulong)_packetCacheCount)
        {
            return false;
        }

        values = _packetCache.AsSpan((int)index, dwords);
        return true;
    }

    private uint ReadPacketHeader(ulong packetAddress, uint remainingDwords)
    {
        if (TryGetCachedPackets(packetAddress, 1, out var cached))
        {
            return cached[0];
        }

        var count = (int)Math.Min(remainingDwords, (uint)PacketCacheDwords);
        if (count > 1)
        {
            RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Header, count * sizeof(uint));
            if (_host.TryReadGuest(packetAddress, MemoryMarshal.AsBytes(_packetCache.AsSpan(0, count))))
            {
                _packetCacheAddress = packetAddress;
                _packetCacheCount = count;
                return _packetCache[0];
            }

            InvalidatePacketCache();
        }

        return ReadDword(packetAddress, RenderPhaseProfile.CommandReadKind.Header);
    }

    private PacketCursorStack RequireExecution() =>
        _execution ?? throw _host.Fatal($"No command stream is running: queue={QueueId}.");

    private void RunPackets(PacketCursorStack execution, int stopDepth)
    {
        while (execution.Depth > stopDepth)
        {
            _host.RunPendingCommands();
            var cursorIndex = execution.Depth - 1;
            ref var cursor = ref execution.At(cursorIndex);
            if (cursor.Offset > cursor.DwordCount)
            {
                throw _host.Fatal($"The packet cursor is past its buffer: offset={cursor.Offset} dwords={cursor.DwordCount} address=0x{cursor.Address:X16}.");
            }

            if (cursor.DeferredAdvance != 0)
            {
                if (cursor.DeferredAdvance > cursor.Remaining)
                {
                    throw _host.Fatal($"The deferred advance is past the buffer: advance={cursor.DeferredAdvance} remaining={cursor.Remaining} address=0x{cursor.Address:X16}.");
                }

                cursor.Offset += cursor.DeferredAdvance;
                cursor.DeferredAdvance = 0;
                execution.MadeProgress = true;
                continue;
            }

            if (cursor.Offset == cursor.DwordCount)
            {
                execution.Pop();
                continue;
            }

            var packetAddress = cursor.Address + ((ulong)cursor.Offset * sizeof(uint));
            var header = ReadPacketHeader(packetAddress, cursor.Remaining);
            var total = cursor.DwordCount;
            var remaining = cursor.Remaining;
            var offset = cursor.Offset;

            if (header == PacketHeader.FillerHeader)
            {
                cursor.Offset++;
                execution.MadeProgress = true;
                continue;
            }

            // Ring memory the guest has not written yet; retry after it appends more.
            if (header == 0 && cursor.FollowedChunkAdvance)
            {
                InvalidatePacketCache();
                execution.Suspended = true;
                return;
            }

            if (remaining < 2)
            {
                throw _host.Fatal($"The packet is shorter than two dwords: offset=0x{offset:X5} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            var length = PacketHeader.Length(header);
            if (length > remaining)
            {
                throw _host.Fatal($"The packet is longer than the buffer: offset=0x{offset:X5} header=0x{header:X8} length={length} remaining={remaining} address=0x{packetAddress:X16}.");
            }

            if (PacketHeader.IsPredicated(header) && PredicateSkip)
            {
                cursor.Offset += length;
                execution.MadeProgress = true;
                continue;
            }

            var opcode = PacketHeader.Opcode(header);
            var handler = PacketDispatchTable.Opcodes[opcode];
            if (handler is null)
            {
                DumpUnknownPacket(cursor, offset);
                throw _host.Fatal($"The packet opcode is unknown: offset=0x{offset:X5} header=0x{header:X8} opcode=0x{opcode:X2} address=0x{packetAddress:X16}.");
            }

            var payload = ReadPayload(cursorIndex, packetAddress, length - 1);
            var packet = new PacketContext(header & ~1u, packetAddress, offset, remaining, total);
            var consumed = handler(this, in packet, payload) + 1;
            if (opcode is PacketOpcode.CopyData or PacketOpcode.DmaData ||
                (opcode == PacketOpcode.Nop && ((header >> 2) & 0x3Fu) == PacketCustomCode.DmaData))
            {
                // Host-side copies can land in the packet stream itself.
                InvalidatePacketCache();
            }
            if (consumed > remaining)
            {
                throw _host.Fatal($"The handler consumed more than the buffer holds: consumed={consumed} remaining={remaining} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            if (execution.Suspended)
            {
                if (execution.Depth > cursorIndex + 1 && !_chainRequested)
                {
                    execution.At(cursorIndex).DeferredAdvance = consumed;
                }

                _chainRequested = false;
                return;
            }

            if (_chainRequested)
            {
                _chainRequested = false;
                execution.MadeProgress = true;
                continue;
            }

            if (execution.Depth != cursorIndex + 1)
            {
                throw _host.Fatal($"The cursor stack changed under a packet: depth={execution.Depth} expected={cursorIndex + 1} header=0x{header:X8}.");
            }

            execution.At(cursorIndex).Offset += consumed;
            execution.MadeProgress = true;
        }
    }

    // The payload lives in a per-depth scratch buffer for the handler call only.
    private ReadOnlySpan<uint> ReadPayload(int depth, ulong packetAddress, uint payloadDwords)
    {
        while (_payloadScratchByDepth.Count <= depth)
        {
            _payloadScratchByDepth.Add(new uint[PacketHeader.MaxLength]);
        }

        var scratch = _payloadScratchByDepth[depth].AsSpan(0, (int)payloadDwords);
        if (payloadDwords != 0 && TryGetCachedPackets(packetAddress + sizeof(uint), (int)payloadDwords, out var cached))
        {
            cached.CopyTo(scratch);
            return scratch;
        }

        if (payloadDwords != 0)
        {
            RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Payload, scratch.Length * sizeof(uint));
        }
        if (payloadDwords != 0 && !_host.TryReadGuest(packetAddress + sizeof(uint), MemoryMarshal.AsBytes(scratch)))
        {
            throw _host.Fatal($"The command stream cannot read a packet: address=0x{packetAddress:X16} dwords={payloadDwords + 1}.");
        }

        return scratch;
    }

    private void DumpUnknownPacket(in PacketCursor cursor, uint offset)
    {
        var begin = offset > 8 ? offset - 8 : 0;
        var end = Math.Min(cursor.DwordCount, offset + 16);
        Console.Error.WriteLine($"[GPU][FATAL] The packet stream near the unknown packet: buffer=0x{cursor.Address:X16} dwords={cursor.DwordCount} offset=0x{offset:X5}");
        Span<byte> word = stackalloc byte[sizeof(uint)];
        for (var index = begin; index < end; index++)
        {
            var text = _host.TryReadGuest(cursor.Address + ((ulong)index * sizeof(uint)), word)
                ? BinaryPrimitives.ReadUInt32LittleEndian(word).ToString("X8")
                : "????????";
            Console.Error.WriteLine($"\t{index:X5}{(index == offset ? ":" : " ")} {text}");
        }
    }

    internal uint ReadDword(ulong address, RenderPhaseProfile.CommandReadKind readKind = RenderPhaseProfile.CommandReadKind.Operand32)
    {
        RenderPhaseProfile.RecordCommandRead(readKind, sizeof(uint));
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!_host.TryReadGuest(address, bytes))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=4.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal ulong ReadQword(ulong address)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Operand64, sizeof(ulong));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!_host.TryReadGuest(address, bytes))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=8.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    internal void ReadBytes(ulong address, Span<byte> destination)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Other, destination.Length);
        if (!_host.TryReadGuest(address, destination))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size={destination.Length}.");
        }
    }

    internal void WriteDword(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteBytes(address, bytes);
    }

    internal void WriteQword(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteBytes(address, bytes);
    }

    internal void WriteBytes(ulong address, ReadOnlySpan<byte> source)
    {
        if (_packetCacheCount != 0 && address < _packetCacheAddress + (ulong)_packetCacheCount * sizeof(uint) &&
            address + (ulong)source.Length > _packetCacheAddress)
        {
            InvalidatePacketCache();
        }

        if (!_host.Memory.TryWrite(address, source))
        {
            throw _host.Fatal($"The command stream cannot write guest memory: address=0x{address:X16} size={source.Length}.");
        }
    }

    private static ulong Address(uint low, uint high) => low | ((ulong)high << 32);
}
