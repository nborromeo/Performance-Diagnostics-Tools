// Low-level method detour for Unity Editor (Mono runtime, x64).
// Writes a 14-byte JMP [RIP+0] at the start of the original method so it
// redirects to the replacement.  No bytes are copied from the original, so
// there is no RIP-relative offset fixup problem.
// Supported: Windows x64, macOS x64/arm64, Linux x64 (Editor only).

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CanvasInvalidationTracker
{
    internal static unsafe class MinimalPatcher
    {
        // ── Platform imports ─────────────────────────────────────────────────
#if UNITY_EDITOR_WIN
        [DllImport("kernel32", SetLastError = true)]
        static extern bool VirtualProtect(IntPtr addr, UIntPtr size,
                                          uint newProtect, out uint oldProtect);
        const uint PAGE_EXECUTE_READWRITE = 0x40;

#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        [DllImport("libc")]
        static extern int mprotect(IntPtr addr, IntPtr len, int prot);
        const int PROT_RWX = 7;          // PROT_READ | PROT_WRITE | PROT_EXEC
        const long k_PageSize = 4096;
#endif

        // ── JMP pattern ──────────────────────────────────────────────────────
        // FF 25 00 00 00 00        JMP QWORD PTR [RIP+0]
        // xx xx xx xx xx xx xx xx  8-byte absolute target  (total = 14 bytes)
        const int k_PatchBytes = 14;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Overwrites the first <c>k_PatchBytes</c> bytes of <paramref name="original"/>
        /// with a JMP that redirects to <paramref name="replacement"/>.
        /// The replacement must have an identical managed signature.
        /// </summary>
        internal static bool TryPatch(MethodBase original, MethodBase replacement)
        {
            if (original == null || replacement == null) return false;

#if !UNITY_EDITOR_WIN && !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX
            Debug.LogWarning("[CanvasInvalidationTracker] Method detouring is not " +
                             "supported on this platform — stack traces unavailable.");
            return false;
#else
            try
            {
                // Force the JIT to compile both methods before we touch their bodies.
                RuntimeHelpers.PrepareMethod(original.MethodHandle);
                RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

                IntPtr origPtr = original.MethodHandle.GetFunctionPointer();
                IntPtr replPtr = replacement.MethodHandle.GetFunctionPointer();

                MakeWritable(origPtr, k_PatchBytes);
                WriteJmp((byte*)origPtr, replPtr);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[CanvasInvalidationTracker] Patch failed on '{original.Name}': {ex}");
                return false;
            }
#endif
        }

        // ── Internals ────────────────────────────────────────────────────────

        static void WriteJmp(byte* dst, IntPtr target)
        {
            // JMP [RIP+0]
            dst[0] = 0xFF; dst[1] = 0x25;
            dst[2] = 0x00; dst[3] = 0x00; dst[4] = 0x00; dst[5] = 0x00;
            // 64-bit target address sits immediately after the 6-byte instruction
            *((ulong*)(dst + 6)) = (ulong)(long)target;
        }

        static void MakeWritable(IntPtr ptr, int size)
        {
#if UNITY_EDITOR_WIN
            VirtualProtect(ptr, new UIntPtr((uint)size),
                           PAGE_EXECUTE_READWRITE, out _);
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            long start = (long)ptr & ~(k_PageSize - 1);
            long end   = ((long)ptr + size + k_PageSize - 1) & ~(k_PageSize - 1);
            mprotect((IntPtr)start, (IntPtr)(end - start), PROT_RWX);
#endif
        }
    }
}
