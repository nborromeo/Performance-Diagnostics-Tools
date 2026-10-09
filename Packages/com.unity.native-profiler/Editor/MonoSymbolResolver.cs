using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace NativeProfiler
{
    /// <summary>
    /// Maps raw instruction pointers to managed method names by asking the Mono runtime that is
    /// running this Editor (mono_jit_info_table_find). Only valid for addresses sampled from this
    /// very process, while the domain that JIT-compiled them is still alive. Main thread only.
    /// </summary>
    internal static class MonoSymbolResolver
    {
        const string LibSystem = "/usr/lib/libSystem.B.dylib";
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);

        [DllImport(LibSystem)] static extern IntPtr dlsym(IntPtr handle, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetDomainFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr JitInfoFindFn(IntPtr domain, IntPtr address);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrToPtrFn(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr MethodFullNameFn(IntPtr method, int signature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr p);

        static bool s_Initialized;
        static bool s_Available;
        static GetDomainFn s_DomainGet, s_RootDomain;
        static JitInfoFindFn s_JitInfoFind;
        static PtrToPtrFn s_JitInfoGetMethod, s_MethodGetClass, s_ClassGetImage, s_ImageGetName;
        static MethodFullNameFn s_MethodFullName;
        static FreeFn s_Free;

        public static bool IsAvailable
        {
            get
            {
                if (!s_Initialized)
                    Initialize();
                return s_Available;
            }
        }

        static void Initialize()
        {
            s_Initialized = true;
            if (Application.platform != RuntimePlatform.OSXEditor)
                return;
            try
            {
                s_DomainGet = Load<GetDomainFn>("mono_domain_get");
                s_RootDomain = Load<GetDomainFn>("mono_get_root_domain");
                s_JitInfoFind = Load<JitInfoFindFn>("mono_jit_info_table_find");
                s_JitInfoGetMethod = Load<PtrToPtrFn>("mono_jit_info_get_method");
                s_MethodGetClass = Load<PtrToPtrFn>("mono_method_get_class");
                s_ClassGetImage = Load<PtrToPtrFn>("mono_class_get_image");
                s_ImageGetName = Load<PtrToPtrFn>("mono_image_get_name");
                s_MethodFullName = Load<MethodFullNameFn>("mono_method_full_name");
                s_Free = Load<FreeFn>("mono_free");
                s_Available = s_DomainGet != null && s_RootDomain != null && s_JitInfoFind != null && s_JitInfoGetMethod != null
                    && s_MethodGetClass != null && s_ClassGetImage != null && s_ImageGetName != null && s_MethodFullName != null && s_Free != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NativeProfiler] Mono symbol resolution unavailable: {e.Message}");
                s_Available = false;
            }
        }

        static T Load<T>(string symbol) where T : Delegate
        {
            var ptr = dlsym(RTLD_DEFAULT, symbol);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return null;
            var length = 0;
            while (Marshal.ReadByte(p, length) != 0)
                length++;
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public static bool TryResolve(ulong address, out string methodName, out string assembly)
        {
            methodName = null;
            assembly = null;
            if (!IsAvailable || address == 0)
                return false;

            var ip = new IntPtr(unchecked((long)address));
            var domain = s_DomainGet();
            var ji = domain != IntPtr.Zero ? s_JitInfoFind(domain, ip) : IntPtr.Zero;
            if (ji == IntPtr.Zero)
            {
                var root = s_RootDomain();
                if (root != IntPtr.Zero && root != domain)
                    ji = s_JitInfoFind(root, ip);
            }
            if (ji == IntPtr.Zero)
                return false;

            var method = s_JitInfoGetMethod(ji);
            if (method == IntPtr.Zero)
                return false;

            var namePtr = s_MethodFullName(method, 0);
            if (namePtr == IntPtr.Zero)
                return false;
            methodName = Utf8(namePtr);
            s_Free(namePtr);

            var klass = s_MethodGetClass(method);
            var image = klass != IntPtr.Zero ? s_ClassGetImage(klass) : IntPtr.Zero;
            // mono_image_get_name returns a pointer owned by the image: do not free.
            assembly = image != IntPtr.Zero ? Utf8(s_ImageGetName(image)) : "managed";
            return !string.IsNullOrEmpty(methodName);
        }
    }
}
