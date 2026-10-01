// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	// POSIX bridge for the Windows vectored-exception-handler logic. A
	// sigaction(SIGSEGV/SIGBUS/SIGILL) handler rebuilds the EXCEPTION_POINTERS
	// view the shared handlers expect (Win64 CONTEXT register offsets) from
	// the signal's mcontext, runs the same recovery chain the VEH path uses
	// (unresolved-import trap sentinels, demand-paging of lazily-committed
	// guest pages, fault diagnostics), and writes register changes back into
	// the mcontext so sigreturn resumes the repaired guest. Unrecovered
	// faults are forwarded to the previously installed handler so the .NET
	// runtime keeps turning its own faults into managed exceptions.

	private const int PosixSigIll = 4;
	private const int PosixSigTrap = 5;
	private const int PosixSigAbort = 6;
	private const int PosixSigSegv = 11;
	private static readonly int PosixSigBus = OperatingSystem.IsMacOS() ? 10 : 7;

	// struct sigaction: the handler pointer leads on both platforms; Darwin
	// packs { handler(8), mask(4), flags(4) }, Linux glibc/musl packs
	// { handler(8), mask(128), flags(4), restorer(8) }.
	private static readonly int PosixSigactionSize = OperatingSystem.IsMacOS() ? 16 : 152;
	private static readonly int PosixSigactionFlagsOffset = OperatingSystem.IsMacOS() ? 12 : 136;

	private static readonly int PosixSaSigInfo = OperatingSystem.IsMacOS() ? 0x0040 : 0x0004;
	private static readonly int PosixSaNoDefer = OperatingSystem.IsMacOS() ? 0x0010 : 0x40000000;

	// siginfo_t.si_addr: Darwin { signo, errno, code, pid, uid, status, addr },
	// Linux { signo, errno, code, pad32, addr }.
	private static readonly int PosixSigInfoAddressOffset = OperatingSystem.IsMacOS() ? 24 : 16;

	// Darwin ucontext_t stores a pointer to __darwin_mcontext64 at +48; the
	// general registers live in its __ss thread state after the 16-byte
	// exception state. Linux glibc embeds mcontext_t inline at +40 with the
	// registers in gregs[23]. Rosetta 2 delivers the regular x86-64 layout
	// to translated processes.
	private const int DarwinUcontextMcontextOffset = 48;
	private const int DarwinUserContextMachineContextSizeOffset = 40;
	// Darwin stores vector registers at the same offset in both signal frame formats.
	// The system headers i386/_mcontext.h and mach/i386/_structs.h define this offset.
	private const int DarwinMachineContextVectorRegistersOffset = 352;
	private const int DarwinMcontextErrOffset = 4;
	private const int DarwinMcontextFaultAddressOffset = 8;
	private const int LinuxUcontextGregsOffset = 40;
	private const int LinuxGregsErrOffset = 19 * 8;

	// The kernel's x86-64 sigcontext places the FXSAVE-image pointer right
	// after the general registers it hands to the handler: err(152)
	// trapno(160) oldmask(168) cr2(176) fpstate(184), all relative to
	// GetPosixRegisterBase. glibc and musl both overlay this kernel layout
	// verbatim (glibc's mcontext_t.fpregs is the same slot), so the offset
	// is libc-independent. Inside the FXSAVE image the XMM registers start
	// at +160 (32-byte header + 8 legacy x87/MMX slots x 16 bytes) - the
	// same relative position they occupy in the Win64 CONTEXT's FltSave
	// area (Win64ContextXmm0Offset = 256 + 160).
	private const int LinuxGregsFpstateOffset = 184;
	private const int FxsaveXmmOffset = 160;
	private const int XmmBlockSize = 16 * 16;

	// Byte offsets of the general registers relative to GetPosixRegisterBase,
	// ordered to match the contiguous Win64 CONTEXT block CTX_RAX..CTX_RIP
	// (rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8..r15, rip). Verified
	// against the x86-64 platform headers.
	private static readonly int[] PosixRegisterOffsets = OperatingSystem.IsMacOS()
		? new[] { 16, 32, 40, 24, 72, 64, 56, 48, 80, 88, 96, 104, 112, 120, 128, 136, 144 }
		: new[] { 104, 112, 96, 88, 120, 80, 72, 64, 0, 8, 16, 24, 32, 40, 48, 56, 128 };

	private static DirectExecutionBackend? _posixSignalBackend;
	private static bool _posixSignalHandlersInstalled;
	private static bool _posixRawRecoveryEnabled;
	private static bool _posixSignalWarmup;
	private static readonly nint[] _posixPreviousActions = new nint[32];
	// The sigaction entry: HandlePosixSignal itself, or on macOS a trampoline that first
	// moves off a guest stack (see CreatePosixSignalTrampoline).
	private static nint _posixSignalEntry;
	private static int _posixSignalTraceCount;
	private static long _perfSignalCount;
	private static readonly bool _perfSignalCounter =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PERF_MEM"), "1", StringComparison.Ordinal);
	// Read once: every guest write to a tracked page lands in the handler.
	private static readonly bool _logEveryPosixSignal =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POSIX_SIGNALS"), "1", StringComparison.Ordinal);

	[ThreadStatic]
	private static int _posixSignalHandlerDepth;

	// True when the signal context contains the current vector register values.
	// Copy changes back to these registers before the guest continues.
	[ThreadStatic]
	private static bool _posixXmmContextBridged;

	private void SetupPosixExceptionHandler()
	{
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_POSIX_SIGNALS"), "1", StringComparison.Ordinal))
		{
			Console.Error.WriteLine("[LOADER][WARN] POSIX signal exception bridge disabled by SHARPEMU_DISABLE_POSIX_SIGNALS=1; guest faults will not be recovered.");
			return;
		}

		_posixSignalBackend = this;
		if (_posixSignalHandlersInstalled)
		{
			return;
		}

		_posixRawRecoveryEnabled = !string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal);
		if (!_posixRawRecoveryEnabled)
		{
			Console.Error.WriteLine("[LOADER][INFO] Raw sentinel recovery disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
		}

		WarmUpPosixSignalPath();
		SharpEmu.HLE.GuestImageWriteTracker.WarmUp();
		_posixSignalEntry = CreatePosixSignalTrampoline(_hostRspSlotTlsIndex);
		WarmUpPosixSignalTrampoline();

		if (!InstallPosixSignalHandler(PosixSigSegv) ||
			!InstallPosixSignalHandler(PosixSigBus) ||
			!InstallPosixSignalHandler(PosixSigIll) ||
			!InstallPosixSignalHandler(PosixSigTrap) ||
			!InstallPosixSignalHandler(PosixSigAbort))
		{
			throw new InvalidOperationException("Failed to install POSIX fault signal handlers");
		}

		_posixSignalHandlersInstalled = true;
		Console.Error.WriteLine("[LOADER][INFO] POSIX signal exception bridge installed (SIGSEGV/SIGBUS/SIGILL)");
	}

	/// <summary>
	/// Runs the signal-recovery path once with fabricated inputs before the
	/// handlers are installed. The first entry into the handler must not
	/// require JIT compilation (a fault can interrupt arbitrary runtime
	/// states), and under Rosetta 2 the signal trampoline cannot enter x86
	/// code that has never been executed (and therefore never translated): a
	/// cold handler is silently never invoked and the faulting instruction
	/// retries forever.
	/// </summary>
	private void WarmUpPosixSignalPath()
	{
		byte* testUserContext = stackalloc byte[512];
		new Span<byte>(testUserContext, 512).Clear();
		byte* testMachineContext = stackalloc byte[1024];
		new Span<byte>(testMachineContext, 1024).Clear();
		if (OperatingSystem.IsMacOS())
		{
			*(byte**)(testUserContext + DarwinUcontextMcontextOffset) = testMachineContext;
			*(ulong*)(testUserContext + DarwinUserContextMachineContextSizeOffset) = 1024;
			// Load the Mach memory functions before the first signal uses them.
			byte readProbe = 0;
			_ = TryReadMacOsMemory((ulong)testMachineContext, &readProbe, 1);
		}

		_posixSignalWarmup = true;
		try
		{
			((delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal)(PosixSigSegv, 0, (nint)testUserContext);

			// Warm the branches the fabricated fault above skips without
			// spamming diagnostics: the benign-exception path through
			// VectoredHandler, the lazy-commit probe (fault address 0 bails
			// out immediately), and the chain helper (signal 0 has no saved
			// action and sigaction(0, ...) fails with EINVAL).
			EXCEPTION_RECORD record = default;
			record.ExceptionCode = DBG_PRINTEXCEPTION_C;
			byte* contextRecord = stackalloc byte[Win64ContextSize];
			new Span<byte>(contextRecord, Win64ContextSize).Clear();
			EXCEPTION_POINTERS pointers;
			pointers.ExceptionRecord = &record;
			pointers.ContextRecord = contextRecord;
			_ = VectoredHandler(&pointers);

			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			// 0x70000 is never guest-owned, so this walks the vmem region
			// scan and the PRT range check, then bails out silently.
			record.ExceptionInformation[1] = 0x70000;
			_ = TryHandleLazyCommittedPage(&record, 0, 0);
			ChainPreviousPosixAction(0, 0, 0);
		}
		finally
		{
			_posixSignalWarmup = false;
		}
	}

	// A guest fault is delivered on the guest stack, so HandlePosixSignal ran there: managed
	// frames on a stack the runtime does not know, and above the host stack in address. A GC
	// that suspended the thread inside the handler walked its frames out of order and skipped
	// the host frames below the guest entry (RunGuestThread and its callers), leaving their
	// locals pointing at objects the GC had moved. Import calls already switch to the host
	// stack for the same reason; this does the same for signals on macOS.
	//
	// The entry stub of the innermost guest entry saved the host RSP in the storage the
	// _hostRspSlotTlsIndex slot points to. The host stack below it is unused while guest code
	// runs. The trampoline switches there when RSP is more than 64 MiB away from that saved
	// RSP (a guest stack), and otherwise (a fault on the host stack, or no guest entry on this
	// thread) jumps straight to the managed handler. The ucontext and siginfo pointers stay
	// valid: they point into the signal frame the kernel left on the original stack.
	private static nint CreatePosixSignalTrampoline(uint hostRspSlotKey)
	{
		var handler = (nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal;
		// gs:[key * 8] is the pthread TSD slot on macOS x64 (the native stubs read theirs the same way).
		if (!OperatingSystem.IsMacOS() || hostRspSlotKey >= 512)
		{
			return 0;
		}

		var page = (byte*)HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
			HostMemory.PAGE_EXECUTE_READWRITE);
		if (page == null)
		{
			return 0;
		}

		var code = new System.Collections.Generic.List<byte>(96);
		var directFixups = new System.Collections.Generic.List<int>();
		void Jcc(byte opcode)
		{
			code.Add(opcode);
			code.Add(0);
			directFixups.Add(code.Count - 1);
		}

		// mov rax, gs:[key * 8]: this thread's host-RSP storage, or 0
		code.AddRange([0x65, 0x48, 0x8B, 0x04, 0x25]);
		code.AddRange(BitConverter.GetBytes(hostRspSlotKey * 8));
		code.AddRange([0x48, 0x85, 0xC0]); Jcc(0x74);          // test rax, rax / jz direct
		code.AddRange([0x4C, 0x8B, 0x10]);                     // mov r10, [rax]: saved host RSP
		code.AddRange([0x4D, 0x85, 0xD2]); Jcc(0x74);          // test r10, r10 / jz direct
		code.AddRange([0x4D, 0x89, 0xD3]);                     // mov r11, r10
		code.AddRange([0x49, 0x29, 0xE3]);                     // sub r11, rsp
		code.AddRange([0x49, 0x81, 0xC3, 0x00, 0x00, 0x00, 0x04]); // add r11, 64 MiB
		code.AddRange([0x49, 0x81, 0xFB, 0x00, 0x00, 0x00, 0x08]); // cmp r11, 128 MiB
		Jcc(0x72);                                             // jb direct: already on the host stack
		code.AddRange([0x49, 0x89, 0xE3]);                     // mov r11, rsp
		code.AddRange([0x49, 0x8D, 0xA2]);                     // lea rsp, [r10 - 0x2000]
		code.AddRange(BitConverter.GetBytes(-0x2000));
		code.AddRange([0x48, 0x83, 0xE4, 0xF0]);               // and rsp, -16
		code.AddRange([0x41, 0x53]);                           // push r11
		code.AddRange([0x48, 0x83, 0xEC, 0x08]);               // sub rsp, 8
		code.AddRange([0x48, 0xB8]);                           // mov rax, handler
		code.AddRange(BitConverter.GetBytes((long)handler));
		code.AddRange([0xFF, 0xD0]);                           // call rax
		code.AddRange([0x48, 0x83, 0xC4, 0x08]);               // add rsp, 8
		code.Add(0x5C);                                        // pop rsp: back to the signal frame's stack
		code.Add(0xC3);                                        // ret
		foreach (var fixup in directFixups)
		{
			code[fixup] = checked((byte)(code.Count - (fixup + 1)));
		}
		code.AddRange([0x48, 0xB8]);                           // direct: mov rax, handler
		code.AddRange(BitConverter.GetBytes((long)handler));
		code.AddRange([0xFF, 0xE0]);                           // jmp rax

		for (var index = 0; index < code.Count; index++)
		{
			page[index] = code[index];
		}

		if (!HostMemory.Protect(page, 4096, HostMemory.PAGE_EXECUTE_READ, out _))
		{
			return 0;
		}

		HostMemory.FlushInstructionCache(page, (nuint)code.Count);
		return (nint)page;
	}

	// Rosetta 2 cannot enter x86 code from a signal that it has never run, so both paths of the
	// trampoline run once here, in warm-up mode, before sigaction points at it.
	private void WarmUpPosixSignalTrampoline()
	{
		if (_posixSignalEntry == 0)
		{
			return;
		}

		byte* testUserContext = stackalloc byte[512];
		new Span<byte>(testUserContext, 512).Clear();
		byte* testMachineContext = stackalloc byte[1024];
		new Span<byte>(testMachineContext, 1024).Clear();
		*(byte**)(testUserContext + DarwinUcontextMcontextOffset) = testMachineContext;
		*(ulong*)(testUserContext + DarwinUserContextMachineContextSizeOffset) = 1024;

		const int switchStackBytes = 512 * 1024;
		var switchStack = (byte*)NativeMemory.Alloc(switchStackBytes);
		var previousSlot = TlsGetValue(_hostRspSlotTlsIndex);
		ulong* storage = stackalloc ulong[4];
		var entry = (delegate* unmanaged<int, nint, nint, void>)_posixSignalEntry;
		_posixSignalWarmup = true;
		try
		{
			storage[0] = 0;
			TlsSetValue(_hostRspSlotTlsIndex, (nint)storage);
			entry(PosixSigSegv, 0, (nint)testUserContext);
			if (switchStack != null)
			{
				storage[0] = (ulong)(switchStack + switchStackBytes);
				entry(PosixSigSegv, 0, (nint)testUserContext);
			}
		}
		finally
		{
			_posixSignalWarmup = false;
			TlsSetValue(_hostRspSlotTlsIndex, previousSlot);
			NativeMemory.Free(switchStack);
		}
	}

	private static bool InstallPosixSignalHandler(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		*(nint*)action = _posixSignalEntry != 0
			? _posixSignalEntry
			: (nint)(delegate* unmanaged<int, nint, nint, void>)&HandlePosixSignal;
		// No SA_ONSTACK: the runtime's alternate stacks are far too small for
		// the recovery/diagnostic path (JIT compilation of cold handler code
		// can run inside the signal frame). Guest faults deliver onto the 2MB
		// guest stack, host faults onto the regular thread stack — the same
		// stacks Windows dispatches exceptions on.
		*(int*)(action + PosixSigactionFlagsOffset) = PosixSaSigInfo | PosixSaNoDefer;

		var previous = (byte*)NativeMemory.AllocZeroed((nuint)PosixSigactionSize);
		if (sigaction(signal, action, previous) != 0)
		{
			NativeMemory.Free(previous);
			Console.Error.WriteLine($"[LOADER][ERROR] sigaction({signal}) failed: errno={Marshal.GetLastPInvokeError()}");
			return false;
		}

		_posixPreviousActions[signal] = (nint)previous;
		return true;
	}

	[UnmanagedCallersOnly]
	private static void HandlePosixSignal(int signal, nint siginfo, nint ucontext)
	{
		if (_posixSignalHandlerDepth > 0)
		{
			// A fault inside our own fault handler (diagnostics touched an
			// unmapped address): restore the default action and return so the
			// re-executed instruction terminates the process.
			RestoreDefaultPosixAction(signal);
			return;
		}

		_posixSignalHandlerDepth++;
		if (_perfSignalCounter)
		{
			var n = Interlocked.Increment(ref _perfSignalCount);
			if (n % 100000 == 0)
			{
				Console.Error.WriteLine($"[PERF][MEM] posix_faults={n}");
			}
		}
		try
		{
			// Guest-image write tracking runs first: it only needs the fault
			// address (safe for host and guest threads alike) and must resume
			// the faulting write immediately after restoring write access.
			if (signal != PosixSigIll &&
				siginfo != 0 &&
				SharpEmu.HLE.GuestImageWriteTracker.TryHandleWriteFault(
					*(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset)))
			{
				return;
			}

			if (TryHandlePosixFault(signal, siginfo, ucontext))
			{
				return;
			}
		}
		catch
		{
			// A managed exception must never unwind out of a signal frame.
		}
		finally
		{
			_posixSignalHandlerDepth--;
		}

		ChainPreviousPosixAction(signal, siginfo, ucontext);
	}

	private static bool TryHandlePosixFault(int signal, nint siginfo, nint ucontext)
	{
		byte* registers = GetPosixRegisterBase(ucontext);
		if (registers == null)
		{
			return false;
		}

		byte* contextRecord = stackalloc byte[Win64ContextSize];
		new Span<byte>(contextRecord, Win64ContextSize).Clear();
		int[] offsets = PosixRegisterOffsets;
		for (int i = 0; i < offsets.Length; i++)
		{
			WriteCtxU64(contextRecord, CTX_RAX + i * 8, *(ulong*)(registers + offsets[i]));
		}

		// Copy the vector registers that instruction recovery can change.
		// Keep all other floating-point state unchanged.
		byte* vectorRegisters = GetSignalVectorRegisterAddress(ucontext, registers);
		if (vectorRegisters != null)
		{
			Buffer.MemoryCopy(
				vectorRegisters,
				contextRecord + Win64ContextXmm0Offset,
				XmmBlockSize,
				XmmBlockSize);
		}
		_posixXmmContextBridged = vectorRegisters != null;

		EXCEPTION_RECORD record = default;
		record.ExceptionAddress = (void*)ReadCtxU64(contextRecord, CTX_RIP);
		if (signal == PosixSigIll)
		{
			record.ExceptionCode = 3221225501u;
		}
		else if (signal == PosixSigTrap)
		{
			record.ExceptionCode = 2147483651u;
		}
		else if (signal == PosixSigAbort)
		{
			record.ExceptionCode = 1073741845u;
		}
		else
		{
			ulong faultAddress = GetPosixFaultAddress(siginfo, registers);
			record.ExceptionCode = 3221225477u;
			record.NumberParameters = 2;
			record.ExceptionInformation[0] = GetPosixAccessType(registers, faultAddress, ReadCtxU64(contextRecord, CTX_RIP));
			record.ExceptionInformation[1] = faultAddress;
		}

		EXCEPTION_POINTERS pointers;
		pointers.ExceptionRecord = &record;
		pointers.ContextRecord = contextRecord;

		int traceIndex = _posixSignalWarmup ? 0 : Interlocked.Increment(ref _posixSignalTraceCount);
		bool traceSignal = traceIndex > 0 && (traceIndex <= 16 || traceIndex % 1024 == 0 || _logEveryPosixSignal);
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: sig={signal} rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16} " +
				$"fault=0x{record.ExceptionInformation[1]:X16} access={record.ExceptionInformation[0]} rsp=0x{ReadCtxU64(contextRecord, CTX_RSP):X16}");
			Console.Error.Flush();
		}

		// Sentinel recovery runs first: on Windows both vectored handlers see
		// every fault anyway, and recovering here avoids dumping the full
		// VectoredHandler diagnostics for each recoverable trap.
		int disposition = 0;
		if (_posixRawRecoveryEnabled)
		{
			disposition = TryRecoverUnresolvedSentinel(&pointers);
		}
		if (disposition != -1 && !_posixSignalWarmup && _posixSignalBackend is { } backend)
		{
			disposition = backend.VectoredHandler(&pointers);
		}
		if (traceSignal)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] posix-signal#{traceIndex}: recovered={disposition == -1} new_rip=0x{ReadCtxU64(contextRecord, CTX_RIP):X16}");
			Console.Error.Flush();
		}
		if (disposition != -1 && !_posixSignalWarmup)
		{
			return false;
		}

		for (int i = 0; i < offsets.Length; i++)
		{
			*(ulong*)(registers + offsets[i]) = ReadCtxU64(contextRecord, CTX_RAX + i * 8);
		}
		if (vectorRegisters != null)
		{
			Buffer.MemoryCopy(
				contextRecord + Win64ContextXmm0Offset,
				vectorRegisters,
				XmmBlockSize,
				XmmBlockSize);
		}
		return true;
	}

	private static byte* GetSignalVectorRegisterAddress(nint userContextAddress, byte* machineContext)
	{
		if (OperatingSystem.IsMacOS())
		{
			var machineContextSize = *(ulong*)((byte*)userContextAddress + DarwinUserContextMachineContextSizeOffset);
			return machineContextSize >= DarwinMachineContextVectorRegistersOffset + XmmBlockSize
				? machineContext + DarwinMachineContextVectorRegistersOffset
				: null;
		}

		if (OperatingSystem.IsLinux())
		{
			byte* floatingPointState = *(byte**)(machineContext + LinuxGregsFpstateOffset);
			return floatingPointState != null ? floatingPointState + FxsaveXmmOffset : null;
		}

		return null;
	}

	private static byte* GetPosixRegisterBase(nint ucontext)
	{
		if (ucontext == 0)
		{
			return null;
		}

		if (OperatingSystem.IsMacOS())
		{
			return *(byte**)((byte*)ucontext + DarwinUcontextMcontextOffset);
		}

		return (byte*)ucontext + LinuxUcontextGregsOffset;
	}

	private static ulong GetPosixFaultAddress(nint siginfo, byte* registers)
	{
		ulong address = siginfo != 0 ? *(ulong*)((byte*)siginfo + PosixSigInfoAddressOffset) : 0;
		if (address == 0 && OperatingSystem.IsMacOS())
		{
			address = *(ulong*)(registers + DarwinMcontextFaultAddressOffset);
		}

		return address;
	}

	private static ulong GetPosixAccessType(byte* registers, ulong faultAddress, ulong rip)
	{
		// x86 page-fault error code: bit 1 = write access, bit 4 = instruction
		// fetch. Fall back to comparing the fault address against RIP when
		// the error code is not populated (e.g. under Rosetta 2 translation).
		ulong error = OperatingSystem.IsMacOS()
			? *(uint*)(registers + DarwinMcontextErrOffset)
			: *(ulong*)(registers + LinuxGregsErrOffset);
		if ((error & 0x10) != 0)
		{
			return 8;
		}
		if ((error & 0x2) != 0)
		{
			return 1;
		}

		return faultAddress != 0 && faultAddress == rip ? 8u : 0u;
	}

	private static void RestoreDefaultPosixAction(int signal)
	{
		byte* action = stackalloc byte[PosixSigactionSize];
		new Span<byte>(action, PosixSigactionSize).Clear();
		_ = sigaction(signal, action, null);
	}

	private static void ChainPreviousPosixAction(int signal, nint siginfo, nint ucontext)
	{
		byte* previous = (uint)signal < (uint)_posixPreviousActions.Length
			? (byte*)_posixPreviousActions[signal]
			: null;
		nint handler = previous != null ? *(nint*)previous : 0;
		if (handler == 0)
		{
			// SIG_DFL (or nothing saved): reinstate the default action and
			// return, so re-executing the faulting instruction terminates the
			// process with the original fault context intact.
			RestoreDefaultPosixAction(signal);
			return;
		}
		if (handler == 1)
		{
			// SIG_IGN
			return;
		}

		int flags = *(int*)(previous + PosixSigactionFlagsOffset);
		if ((flags & PosixSaSigInfo) != 0)
		{
			((delegate* unmanaged<int, nint, nint, void>)handler)(signal, siginfo, ucontext);
		}
		else
		{
			((delegate* unmanaged<int, void>)handler)(signal);
		}
	}

	[DllImport("libc", SetLastError = true)]
	private static extern int sigaction(int signum, void* act, void* oldact);
}
