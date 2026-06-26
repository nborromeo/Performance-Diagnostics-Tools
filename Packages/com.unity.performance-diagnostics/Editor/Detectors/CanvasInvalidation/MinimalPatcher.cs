using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PerformanceDiagnostics.Detectors
{
    internal static unsafe class MinimalPatcher
    {
#if UNITY_EDITOR_WIN
        [DllImport("kernel32", SetLastError = true)]
        static extern bool VirtualProtect(IntPtr addr, UIntPtr size,
                                          uint newProtect, out uint oldProtect);
        const uint PAGE_EXECUTE_READWRITE = 0x40;

#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        [DllImport("libc")]
        static extern int mprotect(IntPtr addr, IntPtr len, int prot);
        const int PROT_RWX = 7;
        const long k_PageSize = 4096;
#endif

        const int k_PatchBytes = 14;

        internal static bool TryPatch(MethodBase original, MethodBase replacement)
        {
            if (original == null || replacement == null) return false;

#if !UNITY_EDITOR_WIN && !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX
            Debug.LogWarning("[PerformanceDiagnostics] Method detouring is not supported on " +
                             "this platform — stack traces unavailable.");
            return false;
#else
            try
            {
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
                Debug.LogError($"[PerformanceDiagnostics] Patch failed on '{original.Name}': {ex}");
                return false;
            }
#endif
        }

        static void WriteJmp(byte* dst, IntPtr target)
        {
            dst[0] = 0xFF; dst[1] = 0x25;
            dst[2] = 0x00; dst[3] = 0x00; dst[4] = 0x00; dst[5] = 0x00;
            *((ulong*)(dst + 6)) = (ulong)(long)target;
        }

        static void MakeWritable(IntPtr ptr, int size)
        {
#if UNITY_EDITOR_WIN
            VirtualProtect(ptr, new UIntPtr((uint)size), PAGE_EXECUTE_READWRITE, out _);
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            long start = (long)ptr & ~(k_PageSize - 1);
            long end   = ((long)ptr + size + k_PageSize - 1) & ~(k_PageSize - 1);
            mprotect((IntPtr)start, (IntPtr)(end - start), PROT_RWX);
#endif
        }
    }
}
