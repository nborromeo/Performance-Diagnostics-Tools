using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PerformanceDiagnostics;

namespace PerformanceDiagnostics.Detectors
{
    /// <summary>
    /// Detects dynamic font atlas updates — cases where a FontAsset grows its glyph table at
    /// runtime, forcing a texture re-upload and potential canvas/UI invalidation.
    ///
    /// Patches (canvas-tracker pattern — patch wrapper, forward to implementation via reflection):
    ///
    ///   TMP_FontAssetUtilities.GetCharacterFromFontAsset(...)   [TMP render path]
    ///       → forwards to GetCharacterFromFontAsset_Internal; records only when atlas grew
    ///   TextCore.Text.FontAssetUtilities.GetCharacterFromFontAsset(...)  [UI Toolkit render path]
    ///       → same pattern as above but for TextCore utility class
    ///   TMP_FontAsset.TryAddCharacters(uint[], bool)            [user code batch API]
    ///       → forwards to TryAddCharacters(uint[], out uint[], bool)
    ///   TMP_FontAsset.TryAddCharacters(string, bool)            [user code batch API]
    ///       → forwards to TryAddCharacters(string, out string, bool)
    /// </summary>
    [InitializeOnLoad]
    public sealed class DynamicFontDetector : IDiagnosticDetector
    {
        static readonly DynamicFontDetector s_Instance = new DynamicFontDetector();

        static DynamicFontDetector()
        {
            DiagnosticRegistry.Register(s_Instance);
        }

        // ── Internal data ─────────────────────────────────────────────────────
        public sealed class FontAtlasEntry
        {
            public int    Id;
            public int    Count = 1;
            public int    FrameNumber;
            public float  Time;
            public bool   IsInPlayMode;
            public string FontAssetName;
            public int    FontAssetInstanceId;
            public int    GlyphCount;
            public string StackTrace;
            // Characters that triggered this atlas update
            public readonly List<uint> Unicodes = new();
            // UITK source element (populated asynchronously via delayCall scan)
            public string SourceElementName;
            public string SourceElementPath;
        }

        static string FormatChar(uint u)
        {
            string glyph = u < 0x110000 ? char.ConvertFromUtf32((int)u) : "?";
            return $"U+{u:X4} '{glyph}'";
        }

        // ── Colors ────────────────────────────────────────────────────────────
        static readonly Color k_Category = new Color(0.85f, 0.45f, 1.00f, 1f);

        // ── State ─────────────────────────────────────────────────────────────
        readonly List<FontAtlasEntry>  m_Entries = new();
        readonly List<DiagnosticIssue> m_Issues  = new();
        // Last entry per font asset instance — for same-frame dedup
        readonly Dictionary<int, FontAtlasEntry> m_LastEntry = new();

        bool m_IsPaused;
        int  m_MaxEntries = 200;
        int  m_NextId;

        // ── Reflection / patch state ──────────────────────────────────────────
        // TMP forwarding targets (NOT patched — called via reflection from hooks)
        static MethodInfo s_TmpGetCharImpl;          // TMP_FontAssetUtilities.GetCharacterFromFontAsset_Internal
        static MethodInfo s_TmpAddCharsUIntImpl;     // TMP_FontAsset.TryAddCharacters(uint[], out uint[], bool)
        static MethodInfo s_TmpAddCharsStrImpl;      // TMP_FontAsset.TryAddCharacters(string, out string, bool)

        // TextCore forwarding target (NOT patched)
        static Type       s_TextCoreUtilsType;
        static FieldInfo  s_TextCoreSearchedAssets;  // k_SearchedAssets equivalent
        static MethodInfo s_TextCoreGetCharImpl;     // FontAssetUtilities.GetCharacterFromFontAsset_Internal

        // TextCore polling helpers
        static Type        s_TextCoreFontAssetType;
        static PropertyInfo s_TextCoreCharTableProp;  // FontAsset.characterTable
        static PropertyInfo s_TextCoreCharUnicodeProp; // TextCoreCharacter.unicode

        static bool s_TmpPatchActive;
        static bool s_TextCorePatchActive;

        // ── Polling state (fallback when native render path skips managed hooks) ──
        readonly Dictionary<int, HashSet<uint>> m_PollKnown      = new();
        readonly Dictionary<int, int>           m_PollLastCount  = new();

        // ── Constructor ───────────────────────────────────────────────────────
        DynamicFontDetector()
        {
            InitReflectionAndPatch();
            EditorApplication.update += OnEditorUpdate;
        }

        // ── Reflection + patching ─────────────────────────────────────────────
        static void InitReflectionAndPatch()
        {
            PatchTmpFontAsset();
            PatchTextCoreFontAsset();
        }

        static void PatchTmpFontAsset()
        {
            var utils    = typeof(TMP_FontAssetUtilities);
            var fa       = typeof(TMP_FontAsset);
            var hookType = typeof(DynamicFontDetector);

            // 1. GetCharacterFromFontAsset (public)  →  GetCharacterFromFontAsset_Internal (private)
            //    Canvas-tracker pattern: patch the wrapper, forward to impl via reflection.
            s_TmpGetCharImpl = utils.GetMethod("GetCharacterFromFontAsset_Internal",
                BindingFlags.NonPublic | BindingFlags.Static);

            var getCharWrapper = utils.GetMethod("GetCharacterFromFontAsset",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(uint), typeof(TMP_FontAsset), typeof(bool),
                        typeof(FontStyles), typeof(FontWeight), typeof(bool).MakeByRefType() }, null);
            var hook1 = hookType.GetMethod(nameof(HookTmpGetCharacterFromFontAsset),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (getCharWrapper != null && hook1 != null)
                s_TmpPatchActive |= MinimalPatcher.TryPatch(getCharWrapper, hook1);

            // 2. TryAddCharacters(uint[], bool)  →  TryAddCharacters(uint[], out uint[], bool)
            s_TmpAddCharsUIntImpl = fa.GetMethod("TryAddCharacters",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(uint[]), typeof(uint[]).MakeByRefType(), typeof(bool) }, null);

            var addCharsUInt = fa.GetMethod("TryAddCharacters",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(uint[]), typeof(bool) }, null);
            var hook2 = hookType.GetMethod(nameof(HookTmpAddCharsUInt),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (addCharsUInt != null && hook2 != null)
                s_TmpPatchActive |= MinimalPatcher.TryPatch(addCharsUInt, hook2);

            // 3. TryAddCharacters(string, bool)  →  TryAddCharacters(string, out string, bool)
            s_TmpAddCharsStrImpl = fa.GetMethod("TryAddCharacters",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(string).MakeByRefType(), typeof(bool) }, null);

            var addCharsStr = fa.GetMethod("TryAddCharacters",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(bool) }, null);
            var hook3 = hookType.GetMethod(nameof(HookTmpAddCharsStr),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (addCharsStr != null && hook3 != null)
                s_TmpPatchActive |= MinimalPatcher.TryPatch(addCharsStr, hook3);
        }

        static void PatchTextCoreFontAsset()
        {
            s_TextCoreUtilsType = FindType("UnityEngine.TextCore.Text.FontAssetUtilities");
            Debug.Log($"[DynFont] TextCoreUtilsType: {s_TextCoreUtilsType?.FullName ?? "NULL"}");
            if (s_TextCoreUtilsType == null) return;

            s_TextCoreGetCharImpl = s_TextCoreUtilsType.GetMethod("GetCharacterFromFontAsset_Internal",
                BindingFlags.NonPublic | BindingFlags.Static);
            Debug.Log($"[DynFont] GetCharacterFromFontAsset_Internal: {s_TextCoreGetCharImpl?.Name ?? "NULL"}");

            s_TextCoreSearchedAssets = s_TextCoreUtilsType.GetField("k_SearchedAssets",
                BindingFlags.NonPublic | BindingFlags.Static);

            var fontAssetType  = FindType("UnityEngine.TextCore.Text.FontAsset");
            var fontStylesType = FindType("UnityEngine.TextCore.Text.FontStyles");
            var fontWeightType = FindType("UnityEngine.TextCore.Text.TextFontWeight");
            Debug.Log($"[DynFont] FontAsset={fontAssetType?.Name ?? "NULL"} FontStyles={fontStylesType?.Name ?? "NULL"} TextFontWeight={fontWeightType?.Name ?? "NULL"}");
            if (fontAssetType == null || fontStylesType == null || fontWeightType == null) return;

            s_TextCoreFontAssetType  = fontAssetType;
            s_TextCoreGlyphTableProp = fontAssetType.GetProperty("glyphTable",
                BindingFlags.Public | BindingFlags.Instance);
            Debug.Log($"[DynFont] glyphTable prop: {s_TextCoreGlyphTableProp?.Name ?? "NULL"}");

            // Grab characterTable for polling (IList<TextCoreCharacter> → .unicode)
            s_TextCoreCharTableProp = fontAssetType.GetProperty("characterTable",
                BindingFlags.Public | BindingFlags.Instance);
            var charItemType = s_TextCoreCharTableProp?.PropertyType.GetGenericArguments()
                                                       .FirstOrDefault();
            s_TextCoreCharUnicodeProp = charItemType?.GetProperty("unicode",
                BindingFlags.Public | BindingFlags.Instance);
            Debug.Log($"[DynFont] charTable={s_TextCoreCharTableProp?.Name ?? "NULL"} " +
                      $"charUnicode={s_TextCoreCharUnicodeProp?.Name ?? "NULL"}");

            // Accept both internal (older Unity) and public (newer Unity) accessibility.
            var allBindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var wrapper = s_TextCoreUtilsType.GetMethod("GetCharacterFromFontAsset",
                allBindings, null,
                new[] { typeof(uint), fontAssetType, typeof(bool),
                        fontStylesType, fontWeightType, typeof(bool).MakeByRefType(),
                        typeof(bool) }, null);
            var hook = typeof(DynamicFontDetector).GetMethod(nameof(HookTextCoreGetCharacterFromFontAsset),
                BindingFlags.NonPublic | BindingFlags.Static);
            Debug.Log($"[DynFont] wrapper={wrapper?.Name ?? "NULL"} hook={hook?.Name ?? "NULL"}");
            if (wrapper != null && hook != null)
            {
                s_TextCorePatchActive = MinimalPatcher.TryPatch(wrapper, hook);
                Debug.Log($"[DynFont] TryPatch result: {s_TextCorePatchActive}");
            }
        }

        // Searches all loaded assemblies — more robust than Type.GetType with a fixed assembly name.
        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // ── Hook methods ──────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.NoInlining)]
        static TMP_Character HookTmpGetCharacterFromFontAsset(
            uint unicode, TMP_FontAsset sourceFontAsset,
            bool includeFallbacks, TMPro.FontStyles fontStyle, TMPro.FontWeight fontWeight,
            out bool isAlternativeTypeface)
        {
            int glyphsBefore = sourceFontAsset?.glyphTable?.Count ?? 0;
            string trace = CaptureTrace();

            // Replicate k_SearchedAssets.Clear() that the wrapper does before calling _Internal
            if (includeFallbacks && s_TmpGetCharImpl != null)
            {
                var searched = typeof(TMP_FontAssetUtilities).GetField("k_SearchedAssets",
                    BindingFlags.NonPublic | BindingFlags.Static);
                (searched?.GetValue(null) as System.Collections.ICollection)
                    ?.GetType().GetMethod("Clear")?.Invoke(searched.GetValue(null), null);
            }

            TMP_Character result = null;
            if (s_TmpGetCharImpl != null)
            {
                var args = new object[] { unicode, sourceFontAsset, includeFallbacks,
                                          fontStyle, fontWeight, false };
                result = s_TmpGetCharImpl.Invoke(null, args) as TMP_Character;
                isAlternativeTypeface = (bool)args[5];
            }
            else
            {
                isAlternativeTypeface = false;
            }

            int glyphsAfter = sourceFontAsset?.glyphTable?.Count ?? 0;
            if (glyphsAfter > glyphsBefore && sourceFontAsset != null)
                s_Instance.RecordEvent(GetId(sourceFontAsset), sourceFontAsset.name,
                    glyphsAfter, trace, new[] { unicode });

            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static object HookTextCoreGetCharacterFromFontAsset(
            uint unicode, object sourceFontAsset,
            bool includeFallbacks, int fontStyle, int fontWeight,
            out bool isAlternativeTypeface, bool populateLigatures)
        {
            int glyphsBefore = GetTextCoreGlyphCount(sourceFontAsset);
            Debug.Log($"[DynFont] UITK hook fired U+{unicode:X4} glyphsBefore={glyphsBefore} prop={s_TextCoreGlyphTableProp?.Name ?? "NULL"}");
            string trace = CaptureTrace();

            // Replicate k_SearchedAssets.Clear()
            if (includeFallbacks && s_TextCoreGetCharImpl != null && s_TextCoreSearchedAssets != null)
            {
                (s_TextCoreSearchedAssets.GetValue(null) as System.Collections.ICollection)
                    ?.GetType().GetMethod("Clear")?.Invoke(s_TextCoreSearchedAssets.GetValue(null), null);
            }

            object result = null;
            if (s_TextCoreGetCharImpl != null)
            {
                var args = new object[] { unicode, sourceFontAsset, includeFallbacks,
                                          fontStyle, fontWeight, false, populateLigatures };
                result = s_TextCoreGetCharImpl.Invoke(null, args);
                isAlternativeTypeface = (bool)args[5];
            }
            else
            {
                isAlternativeTypeface = false;
            }

            int glyphsAfter = GetTextCoreGlyphCount(sourceFontAsset);
            var uo = sourceFontAsset as UnityEngine.Object;
            if (glyphsAfter > glyphsBefore && uo != null)
                s_Instance.RecordEvent(GetId(uo), uo.name, glyphsAfter, trace, new[] { unicode },
                                       isUitk: true);

            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool HookTmpAddCharsUInt(TMP_FontAsset fa, uint[] unicodes, bool includeFontFeatures)
        {
            string trace = CaptureTrace();
            bool result  = false;
            if (s_TmpAddCharsUIntImpl != null)
            {
                var args = new object[] { unicodes, null, includeFontFeatures };
                result = (bool)s_TmpAddCharsUIntImpl.Invoke(fa, args);
            }
            s_Instance.RecordEvent(GetId(fa), fa.name, fa.glyphTable?.Count ?? 0, trace, unicodes);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool HookTmpAddCharsStr(TMP_FontAsset fa, string characters, bool includeFontFeatures)
        {
            string trace = CaptureTrace();
            bool result  = false;
            if (s_TmpAddCharsStrImpl != null)
            {
                var args = new object[] { characters, null, includeFontFeatures };
                result = (bool)s_TmpAddCharsStrImpl.Invoke(fa, args);
            }
            // Convert string to unicode codepoints (handles surrogate pairs)
            var unicodes = new List<uint>();
            if (characters != null)
                for (int i = 0; i < characters.Length; )
                {
                    uint cp = char.IsHighSurrogate(characters[i]) && i + 1 < characters.Length
                        ? (uint)char.ConvertToUtf32(characters[i], characters[i + 1])
                        : characters[i];
                    unicodes.Add(cp);
                    i += cp > 0xFFFF ? 2 : 1;
                }
            s_Instance.RecordEvent(GetId(fa), fa.name, fa.glyphTable?.Count ?? 0, trace,
                unicodes.Count > 0 ? unicodes.ToArray() : null);
            return result;
        }

        static string CaptureTrace() =>
            new System.Diagnostics.StackTrace(2, true).ToString();

        // Call GetInstanceID() via reflection to avoid CS0619 (error-level obsolete in Unity 6+).
        static readonly MethodInfo s_GetInstanceIdMethod =
            typeof(UnityEngine.Object).GetMethod("GetInstanceID",
                BindingFlags.Public | BindingFlags.Instance);

        static int GetId(UnityEngine.Object obj) =>
            obj == null ? 0 : (int)(s_GetInstanceIdMethod?.Invoke(obj, null) ?? 0);

        // Initialized in PatchTextCoreFontAsset() using FindType (Type.GetType fails for Unity built-in modules).
        static PropertyInfo s_TextCoreGlyphTableProp;

        static int GetTextCoreGlyphCount(object fa) =>
            (s_TextCoreGlyphTableProp?.GetValue(fa) as System.Collections.ICollection)?.Count ?? 0;

        // ── Polling (native render path fallback) ─────────────────────────────
        void OnEditorUpdate()
        {
            if (m_IsPaused) return;
            PollTmpFontAssets();
            PollTextCoreFontAssets();
        }

        void PollTmpFontAssets()
        {
            foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                int id    = GetId(fa);
                int count = fa.glyphTable?.Count ?? 0;

                if (!m_PollLastCount.TryGetValue(id, out int last))
                {
                    m_PollLastCount[id] = count;
                    SeedKnown(id, GetTmpUnicodes(fa));
                    continue;
                }
                if (count <= last) { m_PollLastCount[id] = count; continue; }
                m_PollLastCount[id] = count;

                // Skip if the managed hook already captured this event this frame
                if (m_LastEntry.TryGetValue(id, out var existing) &&
                    existing.FrameNumber == Time.frameCount) continue;

                var newUnicodes = DiffUnicodes(id, GetTmpUnicodes(fa));
                RecordEvent(id, fa.name, count, k_PollTrace,
                            newUnicodes.Count > 0 ? newUnicodes.ToArray() : null,
                            isUitk: true);
            }
        }

        void PollTextCoreFontAssets()
        {
            if (s_TextCoreFontAssetType == null) return;
            foreach (UnityEngine.Object fa in
                     Resources.FindObjectsOfTypeAll(s_TextCoreFontAssetType))
            {
                int id    = GetId(fa);
                int count = GetTextCoreGlyphCount(fa);

                if (!m_PollLastCount.TryGetValue(id, out int last))
                {
                    m_PollLastCount[id] = count;
                    SeedKnown(id, GetTextCoreUnicodes(fa));
                    continue;
                }
                if (count <= last) { m_PollLastCount[id] = count; continue; }
                m_PollLastCount[id] = count;

                if (m_LastEntry.TryGetValue(id, out var existing) &&
                    existing.FrameNumber == Time.frameCount) continue;

                var newUnicodes = DiffUnicodes(id, GetTextCoreUnicodes(fa));
                RecordEvent(id, fa.name, count, k_PollTrace,
                            newUnicodes.Count > 0 ? newUnicodes.ToArray() : null,
                            isUitk: true);
            }
        }

        static IEnumerable<uint> GetTmpUnicodes(TMP_FontAsset fa)
        {
            var table = fa.characterTable;
            if (table == null) yield break;
            foreach (var c in table) yield return c.unicode;
        }

        IEnumerable<uint> GetTextCoreUnicodes(UnityEngine.Object fa)
        {
            if (s_TextCoreCharTableProp == null || s_TextCoreCharUnicodeProp == null)
                yield break;
            if (s_TextCoreCharTableProp.GetValue(fa) is not System.Collections.IEnumerable table)
                yield break;
            foreach (var item in table)
            {
                var val = s_TextCoreCharUnicodeProp.GetValue(item);
                if (val is uint u) yield return u;
            }
        }

        void SeedKnown(int id, IEnumerable<uint> unicodes)
        {
            if (!m_PollKnown.TryGetValue(id, out var set))
                set = m_PollKnown[id] = new HashSet<uint>();
            foreach (var u in unicodes) set.Add(u);
        }

        List<uint> DiffUnicodes(int id, IEnumerable<uint> current)
        {
            if (!m_PollKnown.TryGetValue(id, out var known))
                known = m_PollKnown[id] = new HashSet<uint>();
            var newOnes = new List<uint>();
            foreach (var u in current)
                if (known.Add(u)) newOnes.Add(u); // Add returns true when newly inserted
            return newOnes;
        }

        const string k_PollTrace = "[Detected via glyph-count polling — native render path]";

        // ── Recording ─────────────────────────────────────────────────────────
        void RecordEvent(int instanceId, string assetName, int glyphCount, string trace,
                         uint[] unicodes = null, bool isUitk = false)
        {
            if (m_IsPaused) return;

            // Same font asset called multiple times in the same frame → accumulate chars, increment count
            if (m_LastEntry.TryGetValue(instanceId, out var existing)
                && existing.FrameNumber == Time.frameCount)
            {
                existing.Count++;
                existing.GlyphCount = glyphCount;
                if (unicodes != null)
                    foreach (var u in unicodes) existing.Unicodes.Add(u);
                RebuildIssues();
                Changed?.Invoke();
                return;
            }

            if (m_Entries.Count >= m_MaxEntries)
                m_Entries.RemoveAt(0);

            var entry = new FontAtlasEntry
            {
                Id                  = m_NextId++,
                FrameNumber         = Time.frameCount,
                Time                = (float)EditorApplication.timeSinceStartup,
                IsInPlayMode        = Application.isPlaying,
                FontAssetName       = assetName ?? "Unknown",
                FontAssetInstanceId = instanceId,
                GlyphCount          = glyphCount,
                StackTrace          = trace,
            };
            if (unicodes != null)
                foreach (var u in unicodes) entry.Unicodes.Add(u);
            m_Entries.Add(entry);
            m_LastEntry[instanceId] = entry;

            if (isUitk && entry.Unicodes.Count > 0)
                ScheduleElementLookup(entry);

            RebuildIssues();
            Changed?.Invoke();
        }

        // ── UITK source-element lookup ────────────────────────────────────────
        // Deferred to the end of the current editor tick so the visual tree is
        // still intact. We query every active panel for a TextElement whose text
        // contains one of the characters that just triggered the atlas growth.
        void ScheduleElementLookup(FontAtlasEntry entry)
        {
            EditorApplication.delayCall += () =>
            {
                if (!TryFindSourceElement(entry.Unicodes,
                        out string name, out string path))
                    return;
                entry.SourceElementName = name;
                entry.SourceElementPath = path;
                RebuildIssues();
                Changed?.Invoke();
            };
        }

        static bool TryFindSourceElement(List<uint> unicodes,
                                          out string name, out string path)
        {
            name = path = null;

            // Build a char set for fast lookup (BMP only; surrogate pairs skipped)
            var chars = new System.Collections.Generic.HashSet<char>();
            foreach (uint u in unicodes)
                if (u < 0x10000) chars.Add((char)u);
            if (chars.Count == 0) return false;

            // Editor-window panels
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                var root = window.rootVisualElement;
                if (root == null) continue;
                var hit = root.Query<UnityEngine.UIElements.TextElement>()
                              .Where(te => ContainsAny(te.text, chars))
                              .First();
                if (hit != null)
                {
                    name = string.IsNullOrEmpty(hit.name) ? hit.GetType().Name : hit.name;
                    path = BuildPath(hit);
                    return true;
                }
            }

            // UIDocument components (Play Mode / runtime UI)
            foreach (var doc in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                var root = doc.rootVisualElement;
                if (root == null) continue;
                var hit = root.Query<UnityEngine.UIElements.TextElement>()
                              .Where(te => ContainsAny(te.text, chars))
                              .First();
                if (hit != null)
                {
                    name = string.IsNullOrEmpty(hit.name) ? hit.GetType().Name : hit.name;
                    path = BuildPath(hit);
                    return true;
                }
            }

            return false;
        }

        static bool ContainsAny(string text, System.Collections.Generic.HashSet<char> chars)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (chars.Contains(c)) return true;
            return false;
        }

        static string BuildPath(UnityEngine.UIElements.VisualElement ve)
        {
            var segments = new System.Collections.Generic.List<string>();
            var current = ve;
            while (current != null)
            {
                var label = string.IsNullOrEmpty(current.name)
                    ? current.GetType().Name
                    : current.name;
                segments.Add(label);
                current = current.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        // ── Issue sync ────────────────────────────────────────────────────────
        void RebuildIssues()
        {
            m_Issues.Clear();
            foreach (var e in m_Entries)
            {
                m_Issues.Add(new DiagnosticIssue
                {
                    Id              = e.Id,
                    Count           = e.Count,
                    FrameNumber     = e.FrameNumber,
                    Time            = e.Time,
                    IsInPlayMode    = e.IsInPlayMode,
                    Category        = Category,
                    CategoryColor   = k_Category,
                    IssueType       = "Atlas Update",
                    IssueTypeColor  = k_Category,
                    ObjectName      = e.FontAssetName,
                    HierarchyPath   = e.FontAssetName,
                    Target          = null,
                    ContextName     = e.Unicodes.Count > 0
                        ? $"{e.GlyphCount} glyphs · {FormatChar(e.Unicodes[0])}{(e.Unicodes.Count > 1 ? $" +{e.Unicodes.Count - 1}" : "")}"
                        : $"{e.GlyphCount} glyphs",
                    DetectorPayload = e,
                });
            }
        }

        // ── IDiagnosticDetector ───────────────────────────────────────────────
        public string Category      => "Dynamic Font";
        public Color  CategoryColor => k_Category;
        public bool   IsEnabled     { get; set; } = true;

        public IReadOnlyList<DiagnosticIssue> Issues => m_Issues;
        public event Action Changed;

        public void Clear()
        {
            m_Entries.Clear();
            m_LastEntry.Clear();
            m_Issues.Clear();
            m_PollKnown.Clear();
            m_PollLastCount.Clear();
            m_NextId = 0;
            Changed?.Invoke();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        public void DrawToolbarControls()
        {
            if (GUILayout.Button(m_IsPaused ? "▶ Resume" : "⏸ Pause",
                    EditorStyles.toolbarButton, GUILayout.Width(72)))
                m_IsPaused = !m_IsPaused;

            bool anyPatch = s_TmpPatchActive || s_TextCorePatchActive;
            var prev = GUI.color;
            GUI.color = anyPatch ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.5f, 0.2f);
            string patchLabel = (s_TmpPatchActive ? "TMP" : "") +
                                (s_TmpPatchActive && s_TextCorePatchActive ? "+" : "") +
                                (s_TextCorePatchActive ? "UITK" : "");
            GUILayout.Label(anyPatch ? $"● {patchLabel}" : "● No Traces",
                EditorStyles.miniLabel, GUILayout.Width(80));
            GUI.color = prev;
        }

        // ── Settings popup ────────────────────────────────────────────────────
        public bool    HasSettings       => true;
        public Vector2 SettingsPopupSize => new Vector2(260f, 80f);

        public void DrawSettingsGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max entries:", EditorStyles.miniLabel, GUILayout.Width(80));
            int newMax = EditorGUILayout.IntField(m_MaxEntries, GUILayout.Width(60));
            if (newMax != m_MaxEntries && newMax > 0) m_MaxEntries = newMax;
            GUILayout.EndHorizontal();

            int tmpCount = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Length;
            int tcCount  = s_TextCoreFontAssetType != null
                ? Resources.FindObjectsOfTypeAll(s_TextCoreFontAssetType).Length : 0;
            GUILayout.Label($"Polling: {tmpCount} TMP  |  {tcCount} TextCore font assets",
                EditorStyles.miniLabel);
            GUILayout.Label($"Known glyph baselines: {m_PollLastCount.Count}",
                EditorStyles.miniLabel);
        }

        // ── Details panel ─────────────────────────────────────────────────────
        public void DrawIssueDetails(DiagnosticIssue issue, ref Vector2 scroll,
                                     ref int traceIndex, ref Vector2 traceScroll)
        {
            if (issue?.DetectorPayload is not FontAtlasEntry e) return;

            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Space(6);

            var prev = GUI.color;
            GUI.color = k_Category;
            GUILayout.Label("  Font Atlas Update", EditorStyles.boldLabel);
            GUI.color = prev;
            CanvasInvalidationDetector.DrawDivider();
            GUILayout.Space(4);

            CanvasInvalidationDetector.DrawSectionHeader("Font Asset");
            CanvasInvalidationDetector.DrawField("Name",        e.FontAssetName);
            CanvasInvalidationDetector.DrawField("Glyph Count", $"{e.GlyphCount}");
            CanvasInvalidationDetector.DrawField("Frame",       $"#{e.FrameNumber}");
            CanvasInvalidationDetector.DrawField("Time",        $"{e.Time:F3} s");
            CanvasInvalidationDetector.DrawField("Mode",        e.IsInPlayMode ? "Play Mode" : "Edit Mode");
            if (e.Count > 1)
                CanvasInvalidationDetector.DrawField("Count", $"{e.Count}× this frame");

            if (!string.IsNullOrEmpty(e.SourceElementName))
            {
                GUILayout.Space(6);
                CanvasInvalidationDetector.DrawSectionHeader("Source Text Element");
                CanvasInvalidationDetector.DrawField("Name", e.SourceElementName);
                if (!string.IsNullOrEmpty(e.SourceElementPath))
                    CanvasInvalidationDetector.DrawField("Path", e.SourceElementPath);
            }
            else if (s_TextCorePatchActive)
            {
                GUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "Source text element not yet identified (pending panel scan).",
                    MessageType.None);
            }

            if (e.Unicodes.Count > 0)
            {
                GUILayout.Space(6);
                CanvasInvalidationDetector.DrawSectionHeader(
                    $"Characters Added ({e.Unicodes.Count})");
                // Show up to 32 chars inline; beyond that summarise
                int show = Mathf.Min(e.Unicodes.Count, 32);
                for (int ci = 0; ci < show; ci++)
                    GUILayout.Label($"  {FormatChar(e.Unicodes[ci])}", EditorStyles.miniLabel);
                if (e.Unicodes.Count > 32)
                    GUILayout.Label($"  … and {e.Unicodes.Count - 32} more",
                        EditorStyles.miniLabel);
            }
            GUILayout.Space(6);

            CanvasInvalidationDetector.DrawSectionHeader("Detection");
            CanvasInvalidationDetector.DrawField("TMP patches",  s_TmpPatchActive  ? "active" : "inactive");
            CanvasInvalidationDetector.DrawField("UITK patches", s_TextCorePatchActive ? "active" : "inactive");
            GUILayout.Space(6);

            if (string.IsNullOrEmpty(e.StackTrace))
            {
                EditorGUILayout.HelpBox(
                    "No stack trace was captured for this entry.", MessageType.Warning);
            }
            else
            {
                CanvasInvalidationDetector.DrawSectionHeader("Stack Trace");
                var frameStyle = new GUIStyle(EditorStyles.label)
                {
                    font     = Font.CreateDynamicFontFromOSFont("Courier New", 11),
                    fontSize = 11,
                    wordWrap = false,
                    richText = false,
                    clipping = TextClipping.Clip,
                    padding  = new RectOffset(4, 4, 2, 2),
                };
                frameStyle.normal.textColor = EditorStyles.label.normal.textColor;

                var lines = e.StackTrace.Split('\n');
                float lineH = frameStyle.lineHeight + 2f;
                float textH = Mathf.Max(60f, lines.Length * lineH);

                traceScroll = GUILayout.BeginScrollView(
                    traceScroll, GUILayout.Height(Mathf.Min(textH, 280f)));
                GUILayout.Label(e.StackTrace, frameStyle);
                GUILayout.EndScrollView();

                if (GUILayout.Button("Copy to Clipboard", EditorStyles.miniButton,
                                     GUILayout.Width(130)))
                    GUIUtility.systemCopyBuffer = e.StackTrace;
            }

            GUILayout.EndScrollView();
        }
    }
}
