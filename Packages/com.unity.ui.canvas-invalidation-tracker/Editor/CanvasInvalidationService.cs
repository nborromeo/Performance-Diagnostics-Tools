using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CanvasInvalidationTracker
{
    /// <summary>
    /// Captures Canvas invalidation events with call-site stack traces.
    ///
    /// How it works
    /// ─────────────
    /// 1. InstallPatches() overwrites the first 14 bytes of each public
    ///    CanvasUpdateRegistry registration method with a JMP to one of our
    ///    static hook methods (Hook_*).
    ///
    /// 2. The hook captures a StackTrace at that exact moment (= the call
    ///    site that triggered the invalidation), stores it in s_PendingTraces,
    ///    then calls the private InternalRegister* method via reflection so
    ///    the element still lands in the real queue.
    ///
    /// 3. We also subscribe to Canvas.willRenderCanvases BEFORE forcing
    ///    CanvasUpdateRegistry.instance to exist, so our Capture() fires
    ///    first and can read the full queues to pair each element with its
    ///    pending trace.
    /// </summary>
    [InitializeOnLoad]
    public static class CanvasInvalidationService
    {
        // ── Reflection handles — CanvasUpdateRegistry ────────────────────────
        static readonly FieldInfo  s_RegistryInstance;
        static readonly FieldInfo  s_LayoutQueueField;
        static readonly FieldInfo  s_GraphicQueueField;
        static readonly FieldInfo  s_VertsDirtyField;
        static readonly FieldInfo  s_MaterialDirtyField;
        static readonly MethodInfo s_InternalLayout;
        static readonly MethodInfo s_InternalGraphic;

        // ── Reflection handles — CrossFade tween forwarding ─────────────────
        // 5-param CrossFadeColor is the actual implementation; 4-param and CrossFadeAlpha
        // delegate to it.  We patch the entry points and forward to the 5-param overload.
        static readonly MethodInfo s_CrossFadeColor5;  // CrossFadeColor(Color,float,bool,bool,bool)

        // Tween-trace store: captures the originating stack when CrossFadeColor /
        // CrossFadeAlpha is called.  Keyed by Graphic so that when the per-frame
        // TweenValue → CanvasRenderer.SetColor fires (with a truncated coroutine
        // stack), we substitute the rich originating trace instead.
        static readonly Dictionary<Graphic, (string trace, StackFrameInfo[] frames)>
            s_TweenTraces = new Dictionary<Graphic, (string, StackFrameInfo[])>();

        // ── Reflection handles — CanvasRenderer native forwarding ────────────
        static readonly FieldInfo  s_NativePtrField;  // UnityEngine.Object.m_CachedPtr

        static readonly MethodInfo s_CR_SetColor_Inj;
        static readonly MethodInfo s_CR_EnableRectClipping_Inj;
        static readonly MethodInfo s_CR_DisableRectClipping_Inj;
        static readonly MethodInfo s_CR_SetMaterial_Inj;
        static readonly MethodInfo s_CR_SetPopMaterial_Inj;
        static readonly MethodInfo s_CR_SetTexture_Inj;
        static readonly MethodInfo s_CR_SetSecondaryTextureCount_Inj;
        static readonly MethodInfo s_CR_SetAlphaTexture_Inj;
        static readonly MethodInfo s_CR_SetMesh_Inj;
        static readonly MethodInfo s_CR_Clear_Inj;
        static readonly MethodInfo s_CR_set_hasPopInstruction_Inj;
        static readonly MethodInfo s_CR_set_materialCount_Inj;
        static readonly MethodInfo s_CR_set_popMaterialCount_Inj;
        static readonly MethodInfo s_CR_set_cullTransparentMesh_Inj;
        static readonly MethodInfo s_CR_set_cull_Inj;
        static readonly MethodInfo s_CR_set_clippingSoftness_Inj;

        // IndexedSet<ICanvasElement> reflection — resolved lazily
        static PropertyInfo s_QueueCount;
        static PropertyInfo s_QueueIndexer;
        static bool         s_QueueReady;

        // ── Pending traces (populated by hooks, consumed by Capture) ─────────
        // Key: ICanvasElement that was just registered
        // Value: (type, formatted stack trace string, structured frames for click-to-open)
        static readonly Dictionary<ICanvasElement, (InvalidationType type, string trace, StackFrameInfo[] frames)>
            s_PendingTraces = new Dictionary<ICanvasElement, (InvalidationType, string, StackFrameInfo[])>();

        // ── Entry store ──────────────────────────────────────────────────────
        static readonly List<InvalidationEntry> s_Entries = new List<InvalidationEntry>();
        static int  s_NextId;
        static bool s_Paused;
        static int  s_MaxEntries = 1000;

        // ── Pending CanvasRenderer events (populated by hooks, drained in Capture) ──
        // Tuple: renderer, method name, formatted trace, structured frames, frame#, time, playMode
        static readonly List<(CanvasRenderer r, string method, string trace, StackFrameInfo[] frames, int frame, float time, bool play)>
            s_PendingCREvents = new List<(CanvasRenderer, string, string, StackFrameInfo[], int, float, bool)>();

        // Per-capture dedup (RuntimeHelpers.GetHashCode is stable & doesn't call deprecated API)
        static readonly HashSet<long> s_SeenThisCapture = new HashSet<long>();

        // Cross-frame dedup: maps ComputeDedupKey(type, canvasName, stackTrace) → canonical entry
        static readonly Dictionary<long, InvalidationEntry> s_DedupMap =
            new Dictionary<long, InvalidationEntry>();

        // Trace cache: maps cheap (no-file-info) trace key → full (file-info) formatted result.
        // PDB symbol resolution is paid exactly once per unique call path, then reused forever.
        static readonly Dictionary<string, (string trace, StackFrameInfo[] frames)> s_TraceCache =
            new Dictionary<string, (string, StackFrameInfo[])>();

        // ── Public API ───────────────────────────────────────────────────────
        public static IReadOnlyList<InvalidationEntry> Entries  => s_Entries;
        public static bool IsPaused    { get => s_Paused; set => s_Paused = value; }
        public static int  MaxEntries
        {
            get => s_MaxEntries;
            set { s_MaxEntries = Mathf.Max(1, value); Trim(); }
        }

        public static bool IsReflectionReady =>
            s_RegistryInstance != null && s_LayoutQueueField != null && s_GraphicQueueField != null;

        public static bool IsPatchingActive { get; private set; }

        public static event Action Changed;

        // ── Initialisation ───────────────────────────────────────────────────
        // The static constructor only resolves readonly reflection handles
        // (pure type-system work, no Unity runtime calls).  Everything that
        // touches live Unity systems — method patching, Canvas event
        // subscription, CanvasUpdateRegistry singleton creation — is deferred
        // via delayCall so it runs after the Editor has fully booted and
        // EditorStyles / PropertyEditor are initialised.
        static CanvasInvalidationService()
        {
            const BindingFlags instPriv   = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticPriv = BindingFlags.Static   | BindingFlags.NonPublic;

            var regType = typeof(CanvasUpdateRegistry);

            s_RegistryInstance  = regType.GetField("s_Instance",           staticPriv);
            s_LayoutQueueField  = regType.GetField("m_LayoutRebuildQueue",  instPriv);
            s_GraphicQueueField = regType.GetField("m_GraphicRebuildQueue", instPriv);

            s_InternalLayout  = regType.GetMethod("InternalRegisterCanvasElementForLayoutRebuild",  instPriv);
            s_InternalGraphic = regType.GetMethod("InternalRegisterCanvasElementForGraphicRebuild", instPriv);

            var graphicType      = typeof(Graphic);
            s_VertsDirtyField    = graphicType.GetField("m_VertsDirty",    instPriv);
            s_MaterialDirtyField = graphicType.GetField("m_MaterialDirty", instPriv);

            // 5-param CrossFadeColor is the internal implementation that both the
            // 4-param overload and CrossFadeAlpha delegate to — not patched, used
            // as the forwarding target from our CrossFade hooks.
            const BindingFlags instPubVirt = BindingFlags.Instance | BindingFlags.Public;
            s_CrossFadeColor5 = graphicType.GetMethod("CrossFadeColor", instPubVirt, null,
                new[] { typeof(Color), typeof(float), typeof(bool), typeof(bool), typeof(bool) }, null);

            // ── CanvasRenderer native forwarding handles ─────────────────────
            s_NativePtrField = typeof(UnityEngine.Object)
                .GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance);

            var crType = typeof(CanvasRenderer);
            const BindingFlags crPriv = BindingFlags.Static | BindingFlags.NonPublic;

            s_CR_SetColor_Inj                 = crType.GetMethod("SetColor_Injected",                crPriv);
            s_CR_EnableRectClipping_Inj       = crType.GetMethod("EnableRectClipping_Injected",       crPriv);
            s_CR_DisableRectClipping_Inj      = crType.GetMethod("DisableRectClipping_Injected",      crPriv);
            s_CR_SetMaterial_Inj              = crType.GetMethod("SetMaterial_Injected",              crPriv);
            s_CR_SetPopMaterial_Inj           = crType.GetMethod("SetPopMaterial_Injected",           crPriv);
            s_CR_SetTexture_Inj               = crType.GetMethod("SetTexture_Injected",               crPriv);
            s_CR_SetSecondaryTextureCount_Inj = crType.GetMethod("SetSecondaryTextureCount_Injected", crPriv);
            s_CR_SetAlphaTexture_Inj          = crType.GetMethod("SetAlphaTexture_Injected",          crPriv);
            s_CR_SetMesh_Inj                  = crType.GetMethod("SetMesh_Injected",                  crPriv);
            s_CR_Clear_Inj                    = crType.GetMethod("Clear_Injected",                    crPriv);
            s_CR_set_hasPopInstruction_Inj    = crType.GetMethod("set_hasPopInstruction_Injected",    crPriv);
            s_CR_set_materialCount_Inj        = crType.GetMethod("set_materialCount_Injected",        crPriv);
            s_CR_set_popMaterialCount_Inj     = crType.GetMethod("set_popMaterialCount_Injected",     crPriv);
            s_CR_set_cullTransparentMesh_Inj  = crType.GetMethod("set_cullTransparentMesh_Injected",  crPriv);
            s_CR_set_cull_Inj                 = crType.GetMethod("set_cull_Injected",                 crPriv);
            s_CR_set_clippingSoftness_Inj     = crType.GetMethod("set_clippingSoftness_Injected",     crPriv);

            EditorApplication.playModeStateChanged += _ => s_SeenThisCapture.Clear();
            EditorApplication.delayCall += LateInitialize;
        }

        static void LateInitialize()
        {
            if (!IsReflectionReady)
            {
                UnityEngine.Debug.LogWarning(
                    "[CanvasInvalidationTracker] Core reflection init failed — " +
                    "CanvasUpdateRegistry internal fields not found.");
                return;
            }

            // ── Patch the four public registration entry points ──────────────
            const BindingFlags staticPub  = BindingFlags.Static | BindingFlags.Public;
            const BindingFlags selfFlags  = BindingFlags.Static | BindingFlags.NonPublic;
            var regType  = typeof(CanvasUpdateRegistry);
            var selfType = typeof(CanvasInvalidationService);

            IsPatchingActive =
                MinimalPatcher.TryPatch(
                    regType.GetMethod("RegisterCanvasElementForLayoutRebuild",     staticPub),
                    selfType.GetMethod(nameof(Hook_RegisterLayout),                selfFlags))
                &
                MinimalPatcher.TryPatch(
                    regType.GetMethod("TryRegisterCanvasElementForLayoutRebuild",  staticPub),
                    selfType.GetMethod(nameof(Hook_TryRegisterLayout),             selfFlags))
                &
                MinimalPatcher.TryPatch(
                    regType.GetMethod("RegisterCanvasElementForGraphicRebuild",    staticPub),
                    selfType.GetMethod(nameof(Hook_RegisterGraphic),               selfFlags))
                &
                MinimalPatcher.TryPatch(
                    regType.GetMethod("TryRegisterCanvasElementForGraphicRebuild", staticPub),
                    selfType.GetMethod(nameof(Hook_TryRegisterGraphic),            selfFlags));

            // If the registry singleton already exists (persisted from a prior
            // domain in the scene), its PerformUpdate is already subscribed and
            // will drain the queues before our Capture.  Fix the order by:
            //   1. getting the PerformUpdate delegate via reflection,
            //   2. removing it from the event,
            //   3. subscribing our Capture,
            //   4. re-adding PerformUpdate so it fires after us.
            const BindingFlags instPriv2 = BindingFlags.Instance | BindingFlags.NonPublic;
            var performUpdate = typeof(CanvasUpdateRegistry)
                                    .GetMethod("PerformUpdate", instPriv2);
            var existing = s_RegistryInstance.GetValue(null) as CanvasUpdateRegistry;
            if (existing != null && performUpdate != null)
            {
                var del = (Canvas.WillRenderCanvases)
                    System.Delegate.CreateDelegate(
                        typeof(Canvas.WillRenderCanvases), existing, performUpdate);
                Canvas.willRenderCanvases -= del;
                Canvas.willRenderCanvases += Capture;
                Canvas.willRenderCanvases += del;
            }
            else
            {
                // Registry not yet alive — subscribe Capture first, then force
                // creation so PerformUpdate is added after us.
                Canvas.willRenderCanvases += Capture;
                var _ = CanvasUpdateRegistry.instance;
            }

            // ── Patch CanvasRenderer native-path setters ─────────────────────
            var crType2 = typeof(CanvasRenderer);
            const BindingFlags instPub  = BindingFlags.Instance | BindingFlags.Public;
            const BindingFlags selfPriv = BindingFlags.Static   | BindingFlags.NonPublic;

            void PatchCR(MethodBase orig, string hookName)
            {
                if (orig == null) return;
                var hook = selfType.GetMethod(hookName, selfPriv);
                if (!MinimalPatcher.TryPatch(orig, hook))
                    UnityEngine.Debug.LogWarning(
                        $"[CanvasInvalidationTracker] CanvasRenderer patch failed: {hookName}");
            }

            PatchCR(crType2.GetMethod("SetColor",      instPub, null, new[] { typeof(Color) },    null), nameof(Hook_CR_SetColor));
            PatchCR(crType2.GetMethod("EnableRectClipping",  instPub, null, new[] { typeof(Rect) }, null), nameof(Hook_CR_EnableRectClipping));
            PatchCR(crType2.GetMethod("DisableRectClipping", instPub, null, Type.EmptyTypes,         null), nameof(Hook_CR_DisableRectClipping));
            PatchCR(crType2.GetMethod("SetMaterial",   instPub, null, new[] { typeof(Material), typeof(int) },      null), nameof(Hook_CR_SetMaterial));
            PatchCR(crType2.GetMethod("SetPopMaterial",instPub, null, new[] { typeof(Material), typeof(int) },      null), nameof(Hook_CR_SetPopMaterial));
            PatchCR(crType2.GetMethod("SetTexture",    instPub, null, new[] { typeof(Texture) },   null), nameof(Hook_CR_SetTexture));
            PatchCR(crType2.GetMethod("SetSecondaryTextureCount", instPub, null, new[] { typeof(int) }, null), nameof(Hook_CR_SetSecondaryTextureCount));
            PatchCR(crType2.GetMethod("SetAlphaTexture", instPub, null, new[] { typeof(Texture) }, null), nameof(Hook_CR_SetAlphaTexture));
            PatchCR(crType2.GetMethod("SetMesh",       instPub, null, new[] { typeof(Mesh) },      null), nameof(Hook_CR_SetMesh));
            PatchCR(crType2.GetMethod("Clear",         instPub, null, Type.EmptyTypes,              null), nameof(Hook_CR_Clear));

            PatchCR(crType2.GetProperty("hasPopInstruction",   instPub)?.GetSetMethod(), nameof(Hook_CR_set_hasPopInstruction));
            PatchCR(crType2.GetProperty("materialCount",       instPub)?.GetSetMethod(), nameof(Hook_CR_set_materialCount));
            PatchCR(crType2.GetProperty("popMaterialCount",    instPub)?.GetSetMethod(), nameof(Hook_CR_set_popMaterialCount));
            PatchCR(crType2.GetProperty("cullTransparentMesh", instPub)?.GetSetMethod(), nameof(Hook_CR_set_cullTransparentMesh));
            PatchCR(crType2.GetProperty("cull",                instPub)?.GetSetMethod(), nameof(Hook_CR_set_cull));
            PatchCR(crType2.GetProperty("clippingSoftness",    instPub)?.GetSetMethod(), nameof(Hook_CR_set_clippingSoftness));

            // ── Patch CrossFade entry points to capture originating stacks ────
            // We only patch the 4-param CrossFadeColor and CrossFadeAlpha (the
            // public entry points called by Selectable etc.), and forward to the
            // 5-param overload which is the actual implementation and stays unpatched.
            var graphicType2 = typeof(Graphic);
            PatchCR(graphicType2.GetMethod("CrossFadeColor", instPub, null,
                        new[] { typeof(Color), typeof(float), typeof(bool), typeof(bool) }, null),
                    nameof(Hook_CrossFadeColor));
            PatchCR(graphicType2.GetMethod("CrossFadeAlpha", instPub, null,
                        new[] { typeof(float), typeof(float), typeof(bool) }, null),
                    nameof(Hook_CrossFadeAlpha));
        }

        // ── Hook methods (replacements for the patched originals) ────────────
        // These MUST be static with a signature identical to the method they replace.
        // NoInlining prevents the JIT from folding them into callers.

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_RegisterLayout(ICanvasElement element)
        {
            StorePendingTrace(element, InvalidationType.Layout);
            s_InternalLayout?.Invoke(CanvasUpdateRegistry.instance, new object[] { element });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Hook_TryRegisterLayout(ICanvasElement element)
        {
            StorePendingTrace(element, InvalidationType.Layout);
            return s_InternalLayout != null
                && (bool)s_InternalLayout.Invoke(CanvasUpdateRegistry.instance, new object[] { element });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_RegisterGraphic(ICanvasElement element)
        {
            StorePendingTrace(element, InvalidationType.Graphic);
            s_InternalGraphic?.Invoke(CanvasUpdateRegistry.instance, new object[] { element });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Hook_TryRegisterGraphic(ICanvasElement element)
        {
            StorePendingTrace(element, InvalidationType.Graphic);
            return s_InternalGraphic != null
                && (bool)s_InternalGraphic.Invoke(CanvasUpdateRegistry.instance, new object[] { element });
        }

        // ── CrossFade hooks — capture originating stack before coroutine spawns ─
        // These fire while the full call stack is still intact (before StartCoroutine
        // hands control to the scheduler).  We store the trace per-Graphic so that
        // subsequent per-frame TweenValue → CanvasRenderer.SetColor calls can look
        // it up instead of recording a truncated coroutine-resume stack.
        // Forwarding goes to the 5-param overload which is the actual implementation
        // and is deliberately left unpatched.

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CrossFadeColor(Graphic self, Color targetColor, float duration,
                                        bool ignoreTimeScale, bool useAlpha)
        {
            StoreTweenTrace(self);
            s_CrossFadeColor5?.Invoke(self,
                new object[] { targetColor, duration, ignoreTimeScale, useAlpha, true });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CrossFadeAlpha(Graphic self, float alpha, float duration, bool ignoreTimeScale)
        {
            StoreTweenTrace(self);
            // CrossFadeAlpha delegates to CrossFadeColor(5-param) with useRGB=false.
            if (s_CrossFadeColor5 != null && self != null)
            {
                var c = self.canvasRenderer != null ? self.canvasRenderer.GetColor() : Color.white;
                c.a = alpha;
                s_CrossFadeColor5.Invoke(self, new object[] { c, duration, ignoreTimeScale, true, false });
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StoreTweenTrace(Graphic graphic)
        {
            if (s_Paused || graphic == null || Changed == null) return;

            string cacheKey = new StackTrace(skipFrames: 2, fNeedFileInfo: false).ToString();
            if (!s_TraceCache.TryGetValue(cacheKey, out var cached))
            {
                cached = FormatTrace(new StackTrace(skipFrames: 2, fNeedFileInfo: true));
                s_TraceCache[cacheKey] = cached;
            }
            s_TweenTraces[graphic] = cached;
        }

        // ── CanvasRenderer hook methods ──────────────────────────────────────
        // Each hook: (1) captures a pending trace, (2) forwards to the _Injected
        // native method via reflection so the renderer state is still updated.
        // First parameter maps to the implicit `this` of the instance method.

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetColor(CanvasRenderer self, Color color)
        {
            // If this SetColor originates from a CrossFade tween, substitute the
            // originating stack (captured when CrossFadeColor/Alpha was called)
            // instead of the truncated coroutine-resume stack.
            if (self != null)
            {
                var g = self.gameObject.GetComponent<Graphic>();
                if (g != null && s_TweenTraces.TryGetValue(g, out var tweenTrace))
                {
                    if (!s_Paused && Changed != null)
                        s_PendingCREvents.Add((self, "SetColor (CrossFade)", tweenTrace.trace,
                                              tweenTrace.frames, Time.frameCount,
                                              Time.realtimeSinceStartup, Application.isPlaying));
                    var ptr2 = GetNativePtr(self);
                    if (ptr2 != IntPtr.Zero) s_CR_SetColor_Inj?.Invoke(null, new object[] { ptr2, color });
                    return;
                }
            }
            StorePendingCRTrace(self, "SetColor");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetColor_Inj?.Invoke(null, new object[] { ptr, color });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_EnableRectClipping(CanvasRenderer self, Rect rect)
        {
            StorePendingCRTrace(self, "EnableRectClipping");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_EnableRectClipping_Inj?.Invoke(null, new object[] { ptr, rect });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_DisableRectClipping(CanvasRenderer self)
        {
            StorePendingCRTrace(self, "DisableRectClipping");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_DisableRectClipping_Inj?.Invoke(null, new object[] { ptr });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetMaterial(CanvasRenderer self, Material material, int index)
        {
            StorePendingCRTrace(self, "SetMaterial");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetMaterial_Inj?.Invoke(null, new object[] { ptr, GetNativePtr(material), index });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetPopMaterial(CanvasRenderer self, Material material, int index)
        {
            StorePendingCRTrace(self, "SetPopMaterial");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetPopMaterial_Inj?.Invoke(null, new object[] { ptr, GetNativePtr(material), index });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetTexture(CanvasRenderer self, Texture texture)
        {
            StorePendingCRTrace(self, "SetTexture");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetTexture_Inj?.Invoke(null, new object[] { ptr, GetNativePtr(texture) });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetSecondaryTextureCount(CanvasRenderer self, int size)
        {
            StorePendingCRTrace(self, "SetSecondaryTextureCount");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetSecondaryTextureCount_Inj?.Invoke(null, new object[] { ptr, size });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetAlphaTexture(CanvasRenderer self, Texture texture)
        {
            StorePendingCRTrace(self, "SetAlphaTexture");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetAlphaTexture_Inj?.Invoke(null, new object[] { ptr, GetNativePtr(texture) });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_SetMesh(CanvasRenderer self, Mesh mesh)
        {
            StorePendingCRTrace(self, "SetMesh");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_SetMesh_Inj?.Invoke(null, new object[] { ptr, GetNativePtr(mesh) });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_Clear(CanvasRenderer self)
        {
            StorePendingCRTrace(self, "Clear");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_Clear_Inj?.Invoke(null, new object[] { ptr });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_hasPopInstruction(CanvasRenderer self, bool value)
        {
            StorePendingCRTrace(self, "hasPopInstruction");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_hasPopInstruction_Inj?.Invoke(null, new object[] { ptr, value });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_materialCount(CanvasRenderer self, int value)
        {
            StorePendingCRTrace(self, "materialCount");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_materialCount_Inj?.Invoke(null, new object[] { ptr, value });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_popMaterialCount(CanvasRenderer self, int value)
        {
            StorePendingCRTrace(self, "popMaterialCount");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_popMaterialCount_Inj?.Invoke(null, new object[] { ptr, value });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_cullTransparentMesh(CanvasRenderer self, bool value)
        {
            StorePendingCRTrace(self, "cullTransparentMesh");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_cullTransparentMesh_Inj?.Invoke(null, new object[] { ptr, value });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_cull(CanvasRenderer self, bool value)
        {
            StorePendingCRTrace(self, "cull");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_cull_Inj?.Invoke(null, new object[] { ptr, value });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Hook_CR_set_clippingSoftness(CanvasRenderer self, Vector2 value)
        {
            StorePendingCRTrace(self, "clippingSoftness");
            var ptr = GetNativePtr(self);
            if (ptr != IntPtr.Zero) s_CR_set_clippingSoftness_Inj?.Invoke(null, new object[] { ptr, value });
        }

        static IntPtr GetNativePtr(UnityEngine.Object obj) =>
            s_NativePtrField != null && obj != null
                ? (IntPtr)s_NativePtrField.GetValue(obj)
                : IntPtr.Zero;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StorePendingCRTrace(CanvasRenderer renderer, string methodName)
        {
            if (s_Paused || renderer == null || Changed == null) return;

            string cacheKey = new StackTrace(skipFrames: 2, fNeedFileInfo: false).ToString();

            // Skip CR events that originate inside CanvasUpdateRegistry.PerformUpdate
            // (triggered by Canvas.SendWillRenderCanvases).  Those are the downstream
            // consequence of managed Graphic/Layout invalidations we already captured —
            // recording them again as CanvasRenderer entries would be redundant.
            if (cacheKey.Contains("CanvasUpdateRegistry") || cacheKey.Contains("SendWillRenderCanvases"))
                return;

            if (!s_TraceCache.TryGetValue(cacheKey, out var cached))
            {
                cached = FormatTrace(new StackTrace(skipFrames: 2, fNeedFileInfo: true));
                s_TraceCache[cacheKey] = cached;
            }

            s_PendingCREvents.Add((renderer, methodName, cached.trace, cached.frames,
                                   Time.frameCount, Time.realtimeSinceStartup, Application.isPlaying));
        }

        // ── Trace capture ────────────────────────────────────────────────────

        // skipFrames: 2 skips StorePendingTrace + the Hook_* frame so that
        // the first visible frame is the real caller (e.g. Graphic.SetVerticesDirty).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StorePendingTrace(ICanvasElement element, InvalidationType type)
        {
            if (s_Paused || element == null || Changed == null) return;

            // Keep the first trace for this element within one frame
            if (s_PendingTraces.ContainsKey(element)) return;

            // Cheap capture (no PDB lookups) used as a cache key.
            string cacheKey = new StackTrace(skipFrames: 2, fNeedFileInfo: false).ToString();

            if (!s_TraceCache.TryGetValue(cacheKey, out var cached))
            {
                // First time we see this call path: pay the PDB cost once, then cache it.
                cached = FormatTrace(new StackTrace(skipFrames: 2, fNeedFileInfo: true));
                s_TraceCache[cacheKey] = cached;
            }

            s_PendingTraces[element] = (type, cached.trace, cached.frames);
        }

        static (string trace, StackFrameInfo[] frames) FormatTrace(StackTrace st)
        {
            var sb     = new System.Text.StringBuilder();
            var frames = new List<StackFrameInfo>();
            for (int i = 0; i < st.FrameCount; i++)
            {
                var frame  = st.GetFrame(i);
                var method = frame?.GetMethod();
                if (method == null) continue;

                string typeName   = method.DeclaringType?.FullName ?? "?";
                string methodName = method.Name;
                string file       = frame.GetFileName();
                int    line       = frame.GetFileLineNumber();

                var sb2 = new System.Text.StringBuilder();
                sb2.Append("  at ").Append(typeName).Append('.').Append(methodName).Append("()");
                if (!string.IsNullOrEmpty(file))
                    sb2.Append($"  [{System.IO.Path.GetFileName(file)}:{line}]");

                string displayLine = sb2.ToString();
                sb.AppendLine(displayLine);
                frames.Add(new StackFrameInfo
                {
                    DisplayLine = displayLine,
                    FilePath    = file,
                    Line        = line,
                });
            }
            return (sb.ToString(), frames.ToArray());
        }

        // ── Queue capture (fires before PerformUpdate clears the queues) ─────

        static void Capture()
        {
            if (s_Paused || !IsReflectionReady) return;

            var registry = s_RegistryInstance.GetValue(null) as CanvasUpdateRegistry;
            if (registry == null) { s_PendingTraces.Clear(); return; }

            var layoutQueue  = s_LayoutQueueField.GetValue(registry);
            var graphicQueue = s_GraphicQueueField.GetValue(registry);

            // Lazily resolve IndexedSet<ICanvasElement> reflection
            if (!s_QueueReady)
            {
                var sample = layoutQueue ?? graphicQueue;
                if (sample == null) { s_PendingTraces.Clear(); return; }

                var t = sample.GetType();
                s_QueueCount   = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                s_QueueIndexer = t.GetProperty("Item",  BindingFlags.Public | BindingFlags.Instance);
                s_QueueReady   = s_QueueCount != null && s_QueueIndexer != null;
                if (!s_QueueReady) { s_PendingTraces.Clear(); return; }
            }

            s_SeenThisCapture.Clear();
            bool changed = false;
            changed |= DrainQueue(layoutQueue,  InvalidationType.Layout);
            changed |= DrainQueue(graphicQueue, InvalidationType.Graphic);
            s_PendingTraces.Clear();
            changed |= DrainCREvents();

            if (changed)
                Changed?.Invoke();
        }

        static bool DrainCREvents()
        {
            if (s_PendingCREvents.Count == 0) return false;

            bool changed = false;
            foreach (var (renderer, method, trace, frames, frame, time, play) in s_PendingCREvents)
            {
                if (renderer == null) continue;

                var go = renderer.gameObject;
                // Use same captureKey bit pattern as Graphic so a GO already recorded
                // via the managed hook is not double-counted.
                long captureKey = ((long)RuntimeHelpers.GetHashCode(go) << 1) | 1L;
                if (!s_SeenThisCapture.Add(captureKey)) continue;

                var    canvas     = go.GetComponentInParent<Canvas>(true);
                string canvasName = canvas != null ? canvas.name : "(no canvas)";
                long   goHash     = (long)RuntimeHelpers.GetHashCode(go);
                long   dedupKey   = ComputeDedupKey(InvalidationType.CanvasRenderer, canvasName, goHash, method);

                if (s_DedupMap.TryGetValue(dedupKey, out var existing))
                {
                    existing.Count++;
                    AddTraceIfNew(existing, trace, frames);
                }
                else
                {
                    var comps = go.GetComponents<Component>();
                    var names = new string[comps.Length];
                    for (int i = 0; i < comps.Length; i++)
                        names[i] = comps[i] != null ? comps[i].GetType().Name : "(missing)";

                    var entry = new InvalidationEntry
                    {
                        Id                 = s_NextId++,
                        FrameNumber        = frame,
                        Time               = time,
                        IsInPlayMode       = play,
                        ObjectName         = go.name,
                        HierarchyPath      = BuildPath(go.transform),
                        Target             = go,
                        ComponentTypeNames = names,
                        CanvasName         = canvasName,
                        CanvasRenderMode   = canvas != null ? canvas.renderMode.ToString() : string.Empty,
                        Type               = InvalidationType.CanvasRenderer,
                        MethodName         = method,
                        NativeOnly         = true,
                    };
                    if (trace != null) entry.Traces.Add((trace, frames));
                    s_Entries.Add(entry);
                    s_DedupMap[dedupKey] = entry;
                }
                changed = true;
            }

            s_PendingCREvents.Clear();
            Trim();
            return changed;
        }

        static bool DrainQueue(object queue, InvalidationType type)
        {
            if (queue == null) return false;
            int count = (int)s_QueueCount.GetValue(queue);
            if (count == 0) return false;

            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                var element = s_QueueIndexer.GetValue(queue, new object[] { i }) as ICanvasElement;
                if (element == null || element.IsDestroyed()) continue;

                var tr = element.transform;
                if (tr == null) continue;

                var go  = tr.gameObject;
                long captureKey = ((long)RuntimeHelpers.GetHashCode(go) << 1)
                                | (type == InvalidationType.Layout ? 0L : 1L);
                if (!s_SeenThisCapture.Add(captureKey)) continue;

                string          trace  = null;
                StackFrameInfo[] frames = null;
                if (s_PendingTraces.TryGetValue(element, out var pending))
                {
                    trace  = pending.trace;
                    frames = pending.frames;
                }

                // Resolve canvas here so we can form the dedup key before allocating an entry
                var    canvas     = go.GetComponentInParent<Canvas>(true);
                string canvasName = canvas != null ? canvas.name : "(no canvas)";
                long   goHash     = (long)RuntimeHelpers.GetHashCode(go);
                long   dedupKey   = ComputeDedupKey(type, canvasName, goHash, null);

                if (s_DedupMap.TryGetValue(dedupKey, out var existing))
                {
                    existing.Count++;
                    AddTraceIfNew(existing, trace, frames);
                }
                else
                {
                    var entry = MakeEntry(go, type, element, trace, frames, canvas, canvasName);
                    s_Entries.Add(entry);
                    s_DedupMap[dedupKey] = entry;
                }
                changed = true;
            }

            Trim();
            return changed;
        }

        // Stable hash keyed on identity (type + canvas + GO + method), NOT on stack trace.
        // Different call sites that dirty the same GO are aggregated into one entry whose
        // Traces list grows; Count tracks total invalidation occurrences.
        static long ComputeDedupKey(InvalidationType type, string canvasName, long goHash, string methodName)
        {
            unchecked
            {
                long h = (long)(int)type * 2654435761L;
                h ^= (long)(uint)(canvasName  ?? string.Empty).GetHashCode() * 2246822519L;
                h ^= goHash                                                   * 3266489917L;
                h ^= (long)(uint)(methodName  ?? string.Empty).GetHashCode() * 2870177191L;
                return h;
            }
        }

        static void AddTraceIfNew(InvalidationEntry entry, string trace, StackFrameInfo[] frames)
        {
            if (string.IsNullOrEmpty(trace)) return;
            foreach (var t in entry.Traces)
                if (t.trace == trace) return;
            entry.Traces.Add((trace, frames));
        }

        static InvalidationEntry MakeEntry(GameObject go, InvalidationType type,
                                           ICanvasElement element, string trace,
                                           StackFrameInfo[] frames,
                                           Canvas canvas, string canvasName)
        {
            var comps = go.GetComponents<Component>();
            var names = new string[comps.Length];
            for (int i = 0; i < comps.Length; i++)
                names[i] = comps[i] != null ? comps[i].GetType().Name : "(missing)";

            GraphicDirtyFlags flags = GraphicDirtyFlags.None;
            if (type == InvalidationType.Graphic && element is Graphic g)
            {
                if (s_VertsDirtyField    != null && s_VertsDirtyField.GetValue(g)    is bool v && v) flags |= GraphicDirtyFlags.Vertices;
                if (s_MaterialDirtyField != null && s_MaterialDirtyField.GetValue(g) is bool m && m) flags |= GraphicDirtyFlags.Material;
            }

            var entry = new InvalidationEntry
            {
                Id                 = s_NextId++,
                FrameNumber        = Time.frameCount,
                Time               = Time.realtimeSinceStartup,
                IsInPlayMode       = Application.isPlaying,
                ObjectName         = go.name,
                HierarchyPath      = BuildPath(go.transform),
                Target             = go,
                ComponentTypeNames = names,
                CanvasName         = canvasName,
                CanvasRenderMode   = canvas != null ? canvas.renderMode.ToString() : string.Empty,
                Type               = type,
                DirtyFlags         = flags,
            };
            if (trace != null) entry.Traces.Add((trace, frames));
            return entry;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static string BuildPath(Transform t)
        {
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null) { stack.Push(cur.name); cur = cur.parent; }
            return string.Join("/", stack);
        }

        static void Trim()
        {
            if (s_Entries.Count <= s_MaxEntries) return;
            s_Entries.RemoveRange(0, s_Entries.Count - s_MaxEntries);
            // Rebuild the dedup map so it only references entries that are still alive
            s_DedupMap.Clear();
            foreach (var e in s_Entries)
            {
                long goHash = e.Target != null ? (long)RuntimeHelpers.GetHashCode(e.Target) : 0L;
                s_DedupMap[ComputeDedupKey(e.Type, e.CanvasName, goHash, e.MethodName)] = e;
            }
        }

        public static void Clear()
        {
            s_Entries.Clear();
            s_DedupMap.Clear();
            s_TraceCache.Clear();
            s_NextId = 0;
            s_PendingTraces.Clear();
            s_PendingCREvents.Clear();
            s_TweenTraces.Clear();
            s_SeenThisCapture.Clear();
            Changed?.Invoke();
        }
    }
}
