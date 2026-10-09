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

#if UNITY_EDITOR_OSX
        // Apple Silicon: JIT code lives in MAP_JIT pages with hardware W^X. mprotect cannot make
        // them writable; pthread_jit_write_protect_np(0) can, but it also makes ALL JIT pages
        // non-executable on the calling thread — including the managed P/Invoke stub we'd return
        // into. So the toggle + write + re-protect must run entirely in native code: we emit a tiny
        // arm64 stub into a plain (non-MAP_JIT) RX page, which is allowed by the editor's
        // com.apple.security.cs.allow-unsigned-executable-memory entitlement.
        const string k_LibSystem = "/usr/lib/libSystem.dylib";
        [DllImport(k_LibSystem)] static extern int    pthread_jit_write_protect_supported_np();
        [DllImport(k_LibSystem)] static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport(k_LibSystem)] static extern IntPtr mmap(IntPtr addr, UIntPtr len, int prot, int flags, int fd, IntPtr offset);
        [DllImport(k_LibSystem, EntryPoint = "mprotect")] static extern int mprotect_sys(IntPtr addr, UIntPtr len, int prot);

        static readonly IntPtr k_RtldDefault = new IntPtr(-2);
        const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
        const int MAP_PRIVATE = 0x2, MAP_ANON = 0x1000;

        // void stub(void* dst, const void* src16, void (*jitWriteProtect)(int), void (*icacheInvalidate)(void*, size_t))
        //   jitWriteProtect(0); memcpy(dst, src16, 16); jitWriteProtect(1); icacheInvalidate(dst, 16);
        static readonly uint[] k_JitWriteStubCode =
        {
            0xa9bd7bfd, 0x910003fd, 0xa90153f3, 0xa9025bf5, // stp x29,x30,[sp,#-48]! ; mov x29,sp ; save x19-x22
            0xaa0003f3, 0xaa0103f4, 0xaa0203f5, 0xaa0303f6, // mov x19..x22, x0..x3
            0x52800000, 0xd63f02a0,                         // jitWriteProtect(0)
            0xa9402a89, 0xa9002a69,                         // ldp x9,x10,[src] ; stp x9,x10,[dst]
            0x52800020, 0xd63f02a0,                         // jitWriteProtect(1)
            0xaa1303e0, 0xd2800201, 0xd63f02c0,             // icacheInvalidate(dst, 16)
            0xa9425bf5, 0xa94153f3, 0xa8c37bfd, 0xd65f03c0, // restore ; ret
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void JitWriteStub(IntPtr dst, IntPtr src, IntPtr jitWriteProtect, IntPtr icacheInvalidate);

        static JitWriteStub s_JitWriteStub;
        static IntPtr s_JitWriteProtectFn, s_IcacheInvalidateFn;
        static bool s_JitWriteStubInitialized;

        static bool EnsureJitWriteStub()
        {
            if (s_JitWriteStubInitialized) return s_JitWriteStub != null;
            s_JitWriteStubInitialized = true;

            if (pthread_jit_write_protect_supported_np() == 0) return false;

            s_JitWriteProtectFn  = dlsym(k_RtldDefault, "pthread_jit_write_protect_np");
            s_IcacheInvalidateFn = dlsym(k_RtldDefault, "sys_icache_invalidate");
            if (s_JitWriteProtectFn == IntPtr.Zero || s_IcacheInvalidateFn == IntPtr.Zero) return false;

            var size = (UIntPtr)k_PageSize;
            IntPtr page = mmap(IntPtr.Zero, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, IntPtr.Zero);
            if (page == IntPtr.Zero || page == new IntPtr(-1)) return false;

            uint* code = (uint*)page;
            for (int i = 0; i < k_JitWriteStubCode.Length; i++) code[i] = k_JitWriteStubCode[i];

            if (mprotect_sys(page, size, PROT_READ | PROT_EXEC) != 0) return false;

            // Fresh page never held code, but flush anyway for correctness.
            var icache = (IcacheInvalidate)Marshal.GetDelegateForFunctionPointer(s_IcacheInvalidateFn, typeof(IcacheInvalidate));
            icache(page, (UIntPtr)(k_JitWriteStubCode.Length * 4));

            s_JitWriteStub = (JitWriteStub)Marshal.GetDelegateForFunctionPointer(page, typeof(JitWriteStub));
            return true;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void IcacheInvalidate(IntPtr start, UIntPtr len);
#endif

        const int k_PatchBytesX64   = 14; // jmp [rip+0] ; <abs64>
        const int k_PatchBytesArm64 = 16; // ldr x16, #8 ; br x16 ; <abs64>

        internal static bool TryPatch(MethodBase original, MethodBase replacement)
        {
            if (original == null || replacement == null) return false;

#if !UNITY_EDITOR_WIN && !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX
            Debug.LogWarning("[PerformanceDiagnostics] Method detouring is not supported on " +
                             "this platform — stack traces unavailable.");
            return false;
#else
            // Memory faults inside the write are not catchable managed exceptions, so every
            // precondition must be validated before touching code memory.
            try
            {
                var arch = RuntimeInformation.ProcessArchitecture;
                if (arch != Architecture.X64 && arch != Architecture.Arm64)
                {
                    Debug.LogWarning($"[PerformanceDiagnostics] Method detouring is not supported on {arch}.");
                    return false;
                }

                RuntimeHelpers.PrepareMethod(original.MethodHandle);
                RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

                IntPtr origPtr = original.MethodHandle.GetFunctionPointer();
                IntPtr replPtr = replacement.MethodHandle.GetFunctionPointer();
                if (origPtr == IntPtr.Zero || replPtr == IntPtr.Zero) return false;

                return arch == Architecture.Arm64
                    ? PatchArm64(origPtr, replPtr, original.Name)
                    : PatchX64(origPtr, replPtr, original.Name);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PerformanceDiagnostics] Patch failed on '{original.Name}': {ex}");
                return false;
            }
#endif
        }

        static bool PatchX64(IntPtr origPtr, IntPtr replPtr, string name)
        {
            if (!MakeWritable(origPtr, k_PatchBytesX64))
            {
                Debug.LogWarning($"[PerformanceDiagnostics] Could not make '{name}' writable — patch skipped.");
                return false;
            }
            WriteJmpX64((byte*)origPtr, replPtr);
            return true;
        }

        static bool PatchArm64(IntPtr origPtr, IntPtr replPtr, string name)
        {
#if UNITY_EDITOR_OSX
            if (!EnsureJitWriteStub())
            {
                Debug.LogWarning($"[PerformanceDiagnostics] Could not set up JIT write access — '{name}' not patched.");
                return false;
            }

            // The pointer must be 4-byte aligned for arm64 instructions.
            if (((long)origPtr & 3) != 0) return false;

            uint* jmp = stackalloc uint[4];
            WriteJmpArm64(jmp, replPtr);
            s_JitWriteStub(origPtr, (IntPtr)jmp, s_JitWriteProtectFn, s_IcacheInvalidateFn);
            return true;
#else
            Debug.LogWarning($"[PerformanceDiagnostics] Method detouring on arm64 is only supported on macOS — '{name}' not patched.");
            return false;
#endif
        }

        static void WriteJmpX64(byte* dst, IntPtr target)
        {
            dst[0] = 0xFF; dst[1] = 0x25;
            dst[2] = 0x00; dst[3] = 0x00; dst[4] = 0x00; dst[5] = 0x00;
            *((ulong*)(dst + 6)) = (ulong)(long)target;
        }

        static void WriteJmpArm64(uint* dst, IntPtr target)
        {
            dst[0] = 0x58000050;                 // ldr x16, #8
            dst[1] = 0xD61F0200;                 // br  x16
            *((ulong*)(dst + 2)) = (ulong)(long)target;
        }

        static bool MakeWritable(IntPtr ptr, int size)
        {
#if UNITY_EDITOR_WIN
            return VirtualProtect(ptr, new UIntPtr((uint)size), PAGE_EXECUTE_READWRITE, out _);
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            long start = (long)ptr & ~(k_PageSize - 1);
            long end   = ((long)ptr + size + k_PageSize - 1) & ~(k_PageSize - 1);
            return mprotect((IntPtr)start, (IntPtr)(end - start), PROT_RWX) == 0;
#else
            return false;
#endif
        }
    }
}
