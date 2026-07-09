using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ImportActivityViewer.Editor
{
    // Unity does not expose the Import Activity window's data through a public API.
    // This bridges into UnityEditor's internals (AssetDatabase.GetImportActivityWindowStartupData
    // and the internal ArtifactInfo/ArtifactInfoImportStats/ArtifactInfoDependency types) via
    // reflection so the raw per-asset import data can be re-analyzed here. Everything is wrapped
    // defensively since these types/members are not a supported contract and can change between
    // Unity versions.
    internal static class ImportActivityReflection
    {
        const BindingFlags k_AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags k_AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Matches the exact wording UnityEditor.ArtifactDifferenceReporter uses (same text shown by
        // the built-in Import Activity window) so we can pull a deterministic cause->effect edge
        // straight out of Unity's own reasoning instead of re-deriving it from the dependency graph.
        static readonly Regex k_DependencyReasonRegex = new Regex(
            @"the asset at '(?<dep>.*?)' (?:changed, which is registered as a dependency of|was added as a dependency of|was removed as a dependency of) '(?<self>.*?)'",
            RegexOptions.Compiled);

        public static string LastError { get; private set; }

        // -1 while the total isn't known yet (or couldn't be determined up front).
        public static int LastTotalCount { get; private set; } = -1;
        public static int LastProcessedCount { get; private set; }

        // Runs as an editor-driven coroutine (the caller pumps MoveNext(), e.g. from
        // EditorApplication.update) instead of blocking the main thread for the whole fetch.
        // On a log with tens of thousands of entries this can take a long time -- most of it is
        // native round-trips (GetArtifactInfos / GatherDifferences) we can't make cheaper, so the
        // only way to keep the editor responsive is to yield control back periodically instead of
        // doing it all in one call. outputRecords is populated in place so the caller can read
        // partial results (and refresh its UI) between batches.
        //
        // Per-item cost is wildly uneven -- most assets are cheap, but any asset that needs the
        // GetArtifactInfos/GatherDifferences round-trip can be expensive, and there's no way to
        // know which ones up front. Batching by a fixed item count can therefore still bundle up
        // a run of expensive items into one long stall before yielding. Batching by a time budget
        // instead (checked after every single item) bounds how long any one step can run
        // regardless of which items land in it.
        //
        // getMillisecondsPerStep is a delegate rather than a plain value so a caller-side UI
        // control (e.g. a toolbar field) can change the budget live, mid-run, without needing to
        // cancel and restart -- a plain parameter would be captured once when the enumerator is
        // created and wouldn't pick up later changes.
        public static IEnumerator FetchAllCurrentRevisionsIncremental(List<AssetImportRecord> outputRecords, Func<double> getMillisecondsPerStep = null)
        {
            Func<double> millisecondsPerStep = getMillisecondsPerStep ?? (() => 8.0);
            LastError = null;
            LastTotalCount = -1;
            LastProcessedCount = 0;
            outputRecords.Clear();

            Assembly editorAssembly = typeof(AssetDatabase).Assembly;
            Type startupDataEnumType = editorAssembly.GetType("UnityEditor.ImportActivityWindowStartupData");
            Type artifactInfoType = editorAssembly.GetType("UnityEditor.ArtifactInfo");

            if (startupDataEnumType == null || artifactInfoType == null)
            {
                LastError = "Unity's internal Import Activity types were not found. This tool targets Unity 6000.x internals and may need updating for this editor version.";
                yield break;
            }

            MethodInfo getStartupData = typeof(AssetDatabase).GetMethod("GetImportActivityWindowStartupData", k_AnyStatic);
            if (getStartupData == null)
            {
                LastError = "AssetDatabase.GetImportActivityWindowStartupData was not found via reflection.";
                yield break;
            }

            MethodInfo getArtifactInfos = typeof(AssetDatabase).GetMethod("GetArtifactInfos", k_AnyStatic);
            Type reporterType = editorAssembly.GetType("UnityEditor.ArtifactDifferenceReporter");
            MethodInfo gatherDifferences = reporterType?.GetMethod(
                "GatherDifferences", k_AnyInstance, null, new[] { artifactInfoType, artifactInfoType }, null);
            // GatherDifferences is a pure function of the two ArtifactInfo arguments passed in,
            // so one reporter instance can be reused for every asset instead of allocating one
            // per asset via reflection (Activator.CreateInstance is not free at this volume).
            object reporter = reporterType != null ? Activator.CreateInstance(reporterType) : null;

            object flags = Enum.Parse(startupDataEnumType, "AllCurrentRevisions");
            object startupResult = null;
            try
            {
                startupResult = getStartupData.Invoke(null, new[] { flags });
            }
            catch (Exception e)
            {
                LastError = "Reflection into Unity's internal Import Activity API failed: " + e.Message;
            }

            if (startupResult == null)
                yield break;

            if (!(startupResult is IEnumerable revisions))
            {
                LastError = "Import activity data returned no result.";
                yield break;
            }

            // The startup data call returns ArtifactInfo[] directly, so this is a free property
            // read (Array implements ICollection), not an extra enumeration pass.
            if (revisions is ICollection collection)
                LastTotalCount = collection.Count;

            // Unity can cache more than one ArtifactInfo entry for the same underlying asset GUID
            // within one session (see CascadeAnalyzer.Deduplicate). Without this cache, each
            // duplicate would independently call GetArtifactInfos/GatherDifferences for the same
            // GUID -- multiplying both the native round-trip cost and the number of native-backed
            // ArtifactInfo wrappers left leaked for the session (see the note on GC.SuppressFinalize
            // below). Memoizing bounds both to once per distinct GUID actually queried.
            var reasonCache = new Dictionary<string, (string causedByPath, List<string> reasonMessages)>();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (object artifactInfoObj in revisions)
            {
                AssetImportRecord record = null;
                try
                {
                    record = ToRecord(artifactInfoType, artifactInfoObj);

                    // Diffing against import history is a handful of extra native calls per asset,
                    // so only pay for it when the asset actually has dependencies that could have
                    // triggered the reimport.
                    if (record != null && record.Dependencies.Count > 0 && getArtifactInfos != null && reporter != null && gatherDifferences != null)
                    {
                        if (!string.IsNullOrEmpty(record.ArtifactKey) && reasonCache.TryGetValue(record.ArtifactKey, out var cached))
                        {
                            record.CausedByPath = cached.causedByPath;
                            record.ReasonMessages.AddRange(cached.reasonMessages);
                        }
                        else
                        {
                            try
                            {
                                ComputeReasonForReimport(artifactInfoType, artifactInfoObj, record, getArtifactInfos, reporter, gatherDifferences);
                            }
                            catch
                            {
                                // Best-effort: fall back to structural dependency matching for this asset.
                            }

                            if (!string.IsNullOrEmpty(record.ArtifactKey))
                                reasonCache[record.ArtifactKey] = (record.CausedByPath, new List<string>(record.ReasonMessages));
                        }
                    }
                }
                catch (Exception e)
                {
                    LastError = "Reflection into Unity's internal Import Activity API failed: " + e.Message;
                }

                if (record != null)
                    outputRecords.Add(record);

                LastProcessedCount++;
                if (stopwatch.Elapsed.TotalMilliseconds >= millisecondsPerStep())
                {
                    yield return null;
                    stopwatch.Restart();
                }
            }
        }

        // Fetches the asset's cached import history and diffs the current revision against the
        // immediately preceding one via Unity's own ArtifactDifferenceReporter -- the same mechanism
        // that produces the "reason for reimport" text in the built-in Import Activity window. When a
        // dependency-change message is found, it names the asset that actually caused the reimport,
        // which is far more reliable than inferring it from the raw dependency hash set.
        static void ComputeReasonForReimport(
            Type artifactInfoType, object currentArtifactInfo, AssetImportRecord record,
            MethodInfo getArtifactInfos, object reporter, MethodInfo gatherDifferences)
        {
            if (string.IsNullOrEmpty(record.ArtifactKey) || !GUID.TryParse(record.ArtifactKey, out GUID guid))
                return;

            if (!(getArtifactInfos.Invoke(null, new object[] { guid }) is Array history))
                return;

            // These wrappers point at Unity's own internally-cached native artifact records
            // (the same ones the built-in Import Activity window reads), not memory we own.
            // ArtifactInfo has a finalizer that frees that native memory on GC, so calling
            // Dispose() -- or simply letting these become unreachable and get finalized --
            // corrupts Unity's cache and crashes the editor the next time it's touched
            // (reopening this window, or opening the built-in one). Suppressing the finalizer
            // on every entry we obtained is the only safe option since we never allocated them.
            foreach (object entry in history)
                if (entry is IDisposable)
                    GC.SuppressFinalize(entry);

            if (history.Length < 2)
                return;

            long currentStamp = Convert.ToInt64(GetMember(artifactInfoType, currentArtifactInfo, "timeStamp") ?? 0L);

            object currentEntry = null;
            object previousEntry = null;
            long bestPreviousStamp = long.MinValue;

            foreach (object entry in history)
            {
                long stamp = Convert.ToInt64(GetMember(artifactInfoType, entry, "timeStamp") ?? 0L);
                if (currentEntry == null && stamp == currentStamp)
                    currentEntry = entry;
                else if (stamp < currentStamp && stamp > bestPreviousStamp)
                {
                    bestPreviousStamp = stamp;
                    previousEntry = entry;
                }
            }

            if (currentEntry == null || previousEntry == null)
                return;

            if (!(gatherDifferences.Invoke(reporter, new[] { previousEntry, currentEntry }) is IEnumerable messages))
                return;

            foreach (object msgObj in messages)
            {
                if (msgObj is not string msg || string.IsNullOrEmpty(msg))
                    continue;

                record.ReasonMessages.Add(msg);

                if (record.CausedByPath == null)
                {
                    Match match = k_DependencyReasonRegex.Match(msg);
                    if (match.Success)
                        record.CausedByPath = match.Groups["dep"].Value;
                }
            }
        }

        static AssetImportRecord ToRecord(Type artifactInfoType, object artifactInfo)
        {
            if (artifactInfo == null)
                return null;

            string assetPath = GetMember(artifactInfoType, artifactInfo, "assetPath") as string;
            if (string.IsNullOrEmpty(assetPath))
                return null;

            var record = new AssetImportRecord { AssetPath = assetPath };

            // artifactKey is a UnityEditor.Experimental.ArtifactKey struct with no ToString()
            // override, so the identity we actually want is its public "guid" field (the
            // asset's GUID) -- that's also what dependency entries key off of below.
            object artifactKeyObj = GetMember(artifactInfoType, artifactInfo, "artifactKey");
            if (artifactKeyObj != null)
            {
                object guidObj = GetMember(artifactKeyObj.GetType(), artifactKeyObj, "guid");
                record.ArtifactKey = guidObj?.ToString();
            }
            if (string.IsNullOrEmpty(record.ArtifactKey))
            {
                object importResultId = GetMember(artifactInfoType, artifactInfo, "importResultID");
                record.ArtifactKey = importResultId?.ToString();
            }

            object durationObj = GetMember(artifactInfoType, artifactInfo, "importDuration");
            if (durationObj != null)
            {
                try { record.ImportDurationMicroseconds = Convert.ToInt64(durationObj); }
                catch { /* leave at 0 if the underlying type can't be converted */ }
            }

            object timeStampObj = GetMember(artifactInfoType, artifactInfo, "timeStamp");
            record.ImportedAt = TryParseTimestamp(timeStampObj);
            record.TimeStampDisplay = record.ImportedAt.HasValue
                ? record.ImportedAt.Value.ToString("yyyy-MM-dd HH:mm:ss")
                : (timeStampObj?.ToString() ?? "-");

            object statsObj = GetMember(artifactInfoType, artifactInfo, "importStats");
            if (statsObj != null)
            {
                Type statsType = statsObj.GetType();
                record.ImporterName = GetMember(statsType, statsObj, "importerClassName") as string;

                object revisionObj = GetMember(statsType, statsObj, "editorRevision");
                if (revisionObj != null)
                {
                    try
                    {
                        record.EditorRevision = Convert.ToUInt64(revisionObj);
                        record.HasEditorRevision = true;
                    }
                    catch { /* editor revision isn't correlatable on this Unity version */ }
                }
            }

            // dependencies is IDictionary<string, ArtifactInfoDependency>, not a plain sequence of
            // dependency objects -- enumerating it yields KeyValuePair<string, ArtifactInfoDependency>
            // entries. The dictionary KEY is the depended-upon asset's GUID (matches artifactKey.guid
            // above); ArtifactInfoDependency.value is just an opaque hash used for cache invalidation,
            // not an asset identity, so it must not be used for chaining.
            if (GetMember(artifactInfoType, artifactInfo, "dependencies") is IEnumerable deps)
            {
                foreach (object entry in deps)
                {
                    if (entry == null) continue;
                    Type entryType = entry.GetType();
                    object depKey = GetMember(entryType, entry, "Key");
                    object depValue = GetMember(entryType, entry, "Value");
                    if (depValue == null) continue;

                    Type depType = depValue.GetType();
                    object depKind = GetMember(depType, depValue, "type");
                    record.Dependencies.Add(new DependencyRef
                    {
                        Key = depKey?.ToString(),
                        IsDynamic = string.Equals(depKind?.ToString(), "Dynamic", StringComparison.Ordinal),
                    });
                }
            }

            // ArtifactInfo has a finalizer that frees native memory on GC -- but these wrappers
            // point at Unity's own internally-cached artifact records (also read by the built-in
            // Import Activity window), so we don't actually own that memory. Suppressing the
            // finalizer (never Dispose()-ing) stops our managed wrapper from ever freeing it.
            if (artifactInfo is IDisposable)
                GC.SuppressFinalize(artifactInfo);

            return record;
        }

        // GetProperty/GetField do a linear scan of a type's declared members on every call --
        // with no caching that cost is paid again for every asset in the log (assetPath,
        // artifactKey, timeStamp, importStats, dependencies, etc. each re-resolved per record).
        // Caching the resolved MemberInfo per (Type, name) turns repeat lookups into a dictionary
        // hit, which matters a lot once the import log has thousands of entries.
        static readonly Dictionary<(Type, string), MemberInfo> s_MemberCache = new Dictionary<(Type, string), MemberInfo>();

        static object GetMember(Type type, object instance, string name)
        {
            var key = (type, name);
            if (!s_MemberCache.TryGetValue(key, out MemberInfo member))
            {
                member = (MemberInfo)type.GetProperty(name, k_AnyInstance) ?? type.GetField(name, k_AnyInstance);
                s_MemberCache[key] = member;
            }

            return member switch
            {
                PropertyInfo prop => prop.GetValue(instance),
                FieldInfo field => field.GetValue(instance),
                _ => null,
            };
        }

        static DateTime? TryParseTimestamp(object raw)
        {
            if (raw == null) return null;

            if (raw is DateTime dt)
                return dt.ToLocalTime();

            if (raw is long || raw is ulong || raw is int || raw is uint)
            {
                long asLong = Convert.ToInt64(raw);
                try
                {
                    // Values in this range look like Windows file-time ticks rather than a Unix timestamp.
                    if (asLong > 100000000000L)
                    {
                        DateTime fileTime = DateTime.FromFileTimeUtc(asLong).ToLocalTime();
                        if (fileTime.Year is > 1990 and < 2100)
                            return fileTime;
                    }
                }
                catch { /* fall through to no parsed value */ }
            }

            return null;
        }
    }
}
