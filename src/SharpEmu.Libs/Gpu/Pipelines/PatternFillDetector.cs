// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A compute program that fills a buffer with a repeating pattern of up to four dwords.
// Record i = group * 64 + thread, for every i below s8, gets [s4, s5, s6, s7][i mod s9]
// through buffer_store_format_x with the descriptor in s[0:3]; s10 is the group id, and
// residues past 3 also store s7. Hades clears its full-screen render targets with it
// several times a frame. Run as written, each fill is a full dispatch that leaves the
// image buffer-modified, so the image's next use detiles all of it again.
public sealed record PatternFill(
    uint DestinationScalarResource,
    uint PatternScalarRegister,
    uint CountScalarRegister,
    uint PeriodScalarRegister,
    uint GroupScalarRegister)
{
    public const uint PatternLength = 4;
}

public static class PatternFillDetector
{
    private sealed record Step(
        uint Pc,
        string Opcode,
        Gen5Operand[] Destinations,
        Gen5Operand[] Sources,
        Gen5InstructionControl? Control,
        uint? BranchTarget = null);

    private static readonly Gen5BufferMemoryControl Store = new(
        DwordCount: 1, VectorAddress: 2, VectorData: 0, ScalarResource: 0, OffsetBytes: 0,
        IndexEnabled: true, OffsetEnabled: false, Glc: false, Slc: false);

    // The whole program, registers and branch targets included, so a match has exactly
    // the data flow described above. Program counters are relative to the first word.
    private static readonly Step[] ExpectedProgram =
    [
        new(0x0000, "SInstPrefetch", [], [], null),
        new(0x0004, "VLshlAddU32", [V(2)], [S(10), C(134), V(0)], Vop3()),
        new(0x000C, "VCmpxGtU32", [], [S(8), V(2)], null),
        Branch(0x0010, "SCbranchExecz", target: 0x0110),
        new(0x0014, "VCvtF32U32", [V(0)], [S(9)], null),
        new(0x0018, "SCmpLgU32", [], [C(128), S(9)], null),
        new(0x001C, "SCselectB64", [S(10)], [S(126), C(128)], null),
        new(0x0020, "VRcpIflagF32", [V(0)], [V(0)], null),
        new(0x0024, "VMulF32", [V(0)], [L(0x4F800000), V(0)], null),
        new(0x002C, "VCvtU32F32", [V(3)], [V(0)], null),
        new(0x0030, "VMadU64U32", [V(0)], [S(9), V(3), C(128)], Vop3(106)),
        new(0x0038, "VCmpNeU32", [], [C(128), V(1)], null),
        new(0x003C, "VSubI32", [V(1)], [C(128), V(0)], null),
        new(0x0040, "VCndmaskB32", [V(0)], [V(1), V(0)], null),
        new(0x0044, "VMulHiU32", [V(1)], [V(0), V(3), S(0)], Vop3()),
        new(0x004C, "VSubI32", [V(0)], [V(3), V(1)], null),
        new(0x0050, "VAddI32", [V(1)], [V(3), V(1)], null),
        new(0x0054, "VCndmaskB32", [V(0)], [V(1), V(0)], null),
        new(0x0058, "VMulHiU32", [V(0)], [V(0), V(2), S(0)], Vop3()),
        new(0x0060, "VMulLoU32", [V(1)], [S(9), V(0), S(0)], Vop3()),
        new(0x0068, "VSubI32", [V(3)], [V(2), V(1)], null),
        new(0x006C, "VCmpGeU32", [S(12)], [V(2), V(1)], Sdwa(12)),
        new(0x0074, "VCmpLeU32", [], [S(9), V(3)], null),
        new(0x0078, "SAndB64", [S(106)], [S(12), S(106)], null),
        new(0x007C, "VAddcU32", [V(0)], [C(128), V(0)], null),
        new(0x0080, "VAddCoCiU32", [V(0)], [C(193), V(0), S(12)], Vop3(106)),
        new(0x0088, "VCndmaskB32", [V(0)], [C(193), V(0), S(10)], Vop3()),
        new(0x0090, "VMulLoU32", [V(0)], [S(9), V(0), S(0)], Vop3()),
        new(0x0098, "VSubI32", [V(0)], [V(2), V(0)], null),
        new(0x009C, "VCmpNeI32", [], [C(128), V(0)], null),
        new(0x00A0, "SAndSaveexecB64", [S(8)], [S(106)], null),
        Branch(0x00A4, "SCbranchExecz", target: 0x00FC),
        new(0x00A8, "VCmpNeI32", [], [C(129), V(0)], null),
        new(0x00AC, "SAndSaveexecB64", [S(10)], [S(106)], null),
        Branch(0x00B0, "SCbranchExecz", target: 0x00E4),
        new(0x00B4, "VCmpNeI32", [], [C(130), V(0)], null),
        new(0x00B8, "SAndSaveexecB64", [S(106)], [S(106)], null),
        Branch(0x00BC, "SCbranchExecz", target: 0x00CC),
        new(0x00C0, "VMovB32", [V(0)], [S(7)], null),
        new(0x00C4, "BufferStoreFormatX", [V(0)], [V(2), S(0), C(128)], Store),
        new(0x00CC, "SAndn2B64", [S(126)], [S(106), S(126)], null),
        Branch(0x00D0, "SCbranchExecz", target: 0x00E0),
        new(0x00D4, "VMovB32", [V(0)], [S(6)], null),
        new(0x00D8, "BufferStoreFormatX", [V(0)], [V(2), S(0), C(128)], Store),
        new(0x00E0, "SMovB64", [S(126)], [S(106)], null),
        new(0x00E4, "SAndn2B64", [S(126)], [S(10), S(126)], null),
        Branch(0x00E8, "SCbranchExecz", target: 0x00F8),
        new(0x00EC, "VMovB32", [V(0)], [S(5)], null),
        new(0x00F0, "BufferStoreFormatX", [V(0)], [V(2), S(0), C(128)], Store),
        new(0x00F8, "SMovB64", [S(126)], [S(10)], null),
        new(0x00FC, "SAndn2B64", [S(126)], [S(8), S(126)], null),
        Branch(0x0100, "SCbranchExecz", target: 0x0110),
        new(0x0104, "VMovB32", [V(0)], [S(4)], null),
        new(0x0108, "BufferStoreFormatX", [V(0)], [V(2), S(0), C(128)], Store),
        new(0x0110, "SEndpgm", [], [], null),
    ];

    public static PatternFill? Detect(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions;
        if (instructions.Count != ExpectedProgram.Length)
        {
            return null;
        }

        var start = instructions[0].Pc;
        for (var index = 0; index < ExpectedProgram.Length; index++)
        {
            var instruction = instructions[index];
            var step = ExpectedProgram[index];
            if (instruction.Pc - start != step.Pc || instruction.Opcode != step.Opcode ||
                !instruction.Destinations.SequenceEqual(step.Destinations) || !instruction.Sources.SequenceEqual(step.Sources) ||
                !Equals(instruction.Control, step.Control))
            {
                return null;
            }

            if (step.BranchTarget is { } target && (instruction.Words.Count == 0 || BranchTarget(instruction) - start != target))
            {
                return null;
            }
        }

        return new PatternFill(
            DestinationScalarResource: 0, PatternScalarRegister: 4, CountScalarRegister: 8, PeriodScalarRegister: 9, GroupScalarRegister: 10);
    }

    // Test seam: the expected program as decoded instructions, so tests can change one part of it.
    internal static List<Gen5ShaderInstruction> ExpectedInstructions() =>
        ExpectedProgram.Select(step => new Gen5ShaderInstruction(
            step.Pc,
            Gen5ShaderEncoding.Sopp,
            step.Opcode,
            step.BranchTarget is { } target ? [0xBF880000u | (ushort)(((int)target - (int)step.Pc - 4) / 4)] : [0u],
            step.Sources,
            step.Destinations,
            step.Control)).ToList();

    // SOPP branches carry a signed dword offset from the next instruction.
    private static uint BranchTarget(Gen5ShaderInstruction branch) =>
        (uint)(branch.Pc + 4 + (short)(branch.Words[0] & 0xFFFF) * 4);

    private static Gen5Operand V(uint register) => Gen5Operand.Vector(register);

    private static Gen5Operand S(uint register) => Gen5Operand.Scalar(register);

    private static Gen5Operand C(uint encoded) => new(Gen5OperandKind.EncodedConstant, encoded);

    private static Gen5Operand L(uint literal) => new(Gen5OperandKind.LiteralConstant, literal);

    private static Gen5Vop3Control Vop3(uint? scalarDestination = null) => new(0, 0, 0, false, 0, scalarDestination);

    private static Gen5SdwaControl Sdwa(uint scalarDestination) => new(6, 0, 6, 6, false, false, 0, 0, 0, false, scalarDestination);

    private static Step Branch(uint pc, string opcode, uint target) => new(pc, opcode, [], [], null, target);
}
