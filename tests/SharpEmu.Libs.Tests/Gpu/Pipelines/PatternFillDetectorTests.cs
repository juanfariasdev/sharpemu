// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class PatternFillDetectorTests
{
    private static PatternFill? Detect(List<Gen5ShaderInstruction> instructions) =>
        PatternFillDetector.Detect(new Gen5ShaderProgram(0x8_0074_E400, instructions));

    private static int IndexAt(List<Gen5ShaderInstruction> instructions, uint pc) =>
        instructions.FindIndex(instruction => instruction.Pc == pc);

    [Fact]
    public void TheFillProgramIsRecognizedWithItsRegisters()
    {
        Assert.Equal(new PatternFill(0, 4, 8, 9, 10), Detect(PatternFillDetector.ExpectedInstructions()));
    }

    [Fact]
    public void TheFillProgramIsRecognizedAtAnyCodeOffset()
    {
        var shifted = PatternFillDetector.ExpectedInstructions().Select(instruction => instruction with { Pc = instruction.Pc + 0x100 }).ToList();
        Assert.NotNull(Detect(shifted));
    }

    [Fact]
    public void AnotherPatternRegisterIsRefused()
    {
        // The residue-1 store takes s5; s4 there would make it a different pattern.
        var instructions = PatternFillDetector.ExpectedInstructions();
        var move = IndexAt(instructions, 0x00EC);
        instructions[move] = instructions[move] with { Sources = [Gen5Operand.Scalar(4)] };
        Assert.Null(Detect(instructions));
    }

    [Fact]
    public void AnotherBranchTargetIsRefused()
    {
        var instructions = PatternFillDetector.ExpectedInstructions();
        var branch = IndexAt(instructions, 0x00A4);
        instructions[branch] = instructions[branch] with { Words = [instructions[branch].Words[0] + 1] };
        Assert.Null(Detect(instructions));
    }

    [Fact]
    public void AnotherStoreControlIsRefused()
    {
        var instructions = PatternFillDetector.ExpectedInstructions();
        var store = IndexAt(instructions, 0x0108);
        instructions[store] = instructions[store] with { Control = (Gen5BufferMemoryControl)instructions[store].Control! with { OffsetBytes = 4 } };
        Assert.Null(Detect(instructions));
    }

    [Fact]
    public void AnExtraInstructionIsRefused()
    {
        var instructions = PatternFillDetector.ExpectedInstructions();
        instructions.Insert(instructions.Count - 1, new Gen5ShaderInstruction(0x0110, Gen5ShaderEncoding.Sopp, "SNop", [0u], [], [], null));
        instructions[^1] = instructions[^1] with { Pc = 0x0114 };
        Assert.Null(Detect(instructions));
    }
}
