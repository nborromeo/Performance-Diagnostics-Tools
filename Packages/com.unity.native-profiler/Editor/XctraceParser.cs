using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NativeProfiler
{
    /// <summary>
    /// Parses the output of
    ///   xctrace export --toc
    ///   xctrace export --xpath '/trace-toc/run[@number="1"]/data/table[@schema="time-profile"]'
    /// The time-profile export de-duplicates every element through id="N" / ref="N" attributes,
    /// so we keep a single id -> parsed value table while streaming rows. Thread-safe (no Unity API).
    /// </summary>
    internal static class XctraceParser
    {
        public static void ParseToc(string tocPath, ProfileData data)
        {
            var doc = XDocument.Load(tocPath);
            var run = doc.Root?.Element("run");
            if (run == null)
                return;

            var info = run.Element("info");
            var process = info?.Element("target")?.Element("process");
            if (process != null)
            {
                int.TryParse((string)process.Attribute("pid"), out data.TargetPid);
                data.TargetName = (string)process.Attribute("name");
            }

            var summary = info?.Element("summary");
            var start = (string)summary?.Element("start-date");
            if (start != null && DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                data.StartUtc = dto.UtcDateTime;
            var duration = (string)summary?.Element("duration");
            if (duration != null)
                double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out data.DurationSeconds);
        }

        public static void ParseTimeProfile(string xmlPath, ProfileData data)
        {
            var parser = new RowParser(data);
            var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore };
            using (var reader = XmlReader.Create(xmlPath, settings))
            {
                reader.MoveToContent();
                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "row")
                        parser.ParseRow((XElement)XNode.ReadFrom(reader));
                    else
                        reader.Read();
                }
            }

            foreach (var s in data.Samples)
                data.Threads[s.Thread].TotalWeightNs += s.WeightNs;
        }

        sealed class RowParser
        {
            readonly ProfileData m_Data;
            readonly Dictionary<int, object> m_ById = new Dictionary<int, object>();
            readonly List<int> m_Scratch = new List<int>(128);

            sealed class Binary { public string Name; public string Path; }
            sealed class Boxed<T> { public T Value; }

            public RowParser(ProfileData data) { m_Data = data; }

            public void ParseRow(XElement row)
            {
                long time = 0, weight = 0;
                int thread = -1;
                int[] frames = null;

                foreach (var e in row.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "sample-time": time = ResolveLong(e); break;
                        case "weight": weight = ResolveLong(e); break;
                        case "thread": thread = ResolveThread(e); break;
                        case "backtrace": frames = ResolveBacktrace(e); break;
                    }
                }

                if (thread < 0 || frames == null || frames.Length == 0)
                    return;
                m_Data.Samples.Add(new Sample { Thread = thread, TimeNs = time, WeightNs = weight > 0 ? weight : 1000000, Frames = frames });
            }

            static int? Id(XElement e) => int.TryParse((string)e.Attribute("id"), out var v) ? v : (int?)null;
            static int? Ref(XElement e) => int.TryParse((string)e.Attribute("ref"), out var v) ? v : (int?)null;

            long ResolveLong(XElement e)
            {
                if (Ref(e) is int r)
                    return m_ById.TryGetValue(r, out var o) ? ((Boxed<long>)o).Value : 0;
                long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
                if (Id(e) is int id)
                    m_ById[id] = new Boxed<long> { Value = v };
                return v;
            }

            int ResolveThread(XElement e)
            {
                if (Ref(e) is int r)
                    return m_ById.TryGetValue(r, out var o) ? ((Boxed<int>)o).Value : -1;

                var fmt = (string)e.Attribute("fmt") ?? "Thread";
                // "Main Thread 0x18486f2 (Unity, pid: 71999)" -> "Main Thread 0x18486f2"
                var paren = fmt.LastIndexOf(" (", StringComparison.Ordinal);
                var name = paren > 0 ? fmt.Substring(0, paren) : fmt;
                long.TryParse(e.Element("tid")?.Value, out var tid);

                var index = m_Data.Threads.Count;
                m_Data.Threads.Add(new ThreadInfo { Name = name, Tid = tid });
                if (Id(e) is int id)
                    m_ById[id] = new Boxed<int> { Value = index };
                return index;
            }

            int[] ResolveBacktrace(XElement e)
            {
                if (Ref(e) is int r)
                    return m_ById.TryGetValue(r, out var o) ? (int[])o : null;

                m_Scratch.Clear();
                foreach (var f in e.Elements("frame"))
                    m_Scratch.Add(ResolveFrame(f));
                var frames = m_Scratch.ToArray();
                if (Id(e) is int id)
                    m_ById[id] = frames;
                return frames;
            }

            int ResolveFrame(XElement f)
            {
                if (Ref(f) is int r)
                    return m_ById.TryGetValue(r, out var o) ? ((Boxed<int>)o).Value : AddUnknownFrame();

                var name = (string)f.Attribute("name");
                var addrText = (string)f.Attribute("addr");
                Binary binary = null;
                var b = f.Element("binary");
                if (b != null)
                {
                    if (Ref(b) is int br)
                        binary = m_ById.TryGetValue(br, out var bo) ? (Binary)bo : null;
                    else
                    {
                        binary = new Binary { Name = (string)b.Attribute("name"), Path = (string)b.Attribute("path") };
                        if (Id(b) is int bid)
                            m_ById[bid] = binary;
                    }
                }

                // Unsymbolicated frames come as name="0x49378709f" addr="0x4937870a0": the name is the
                // return address minus one, i.e. it points inside the call instruction. Prefer it for lookups.
                var isHexName = string.IsNullOrEmpty(name) || name.StartsWith("0x", StringComparison.Ordinal);
                var address = ParseHex(isHexName && !string.IsNullOrEmpty(name) ? name : addrText);

                var frame = new RawFrame
                {
                    Address = address,
                    Name = string.IsNullOrEmpty(name) ? addrText : name,
                    Module = binary?.Name,
                    ModulePath = binary?.Path,
                    IsJitCandidate = binary == null && isHexName,
                };
                if (!frame.IsJitCandidate)
                    frame.FunctionId = m_Data.InternFunction(frame.Name, frame.Module, isHexName ? FunctionKind.Unknown : FunctionKind.Native);

                var index = m_Data.Frames.Count;
                m_Data.Frames.Add(frame);
                if (Id(f) is int id)
                    m_ById[id] = new Boxed<int> { Value = index };
                return index;
            }

            int AddUnknownFrame()
            {
                m_Data.Frames.Add(new RawFrame { Name = "<missing frame>", FunctionId = m_Data.InternFunction("<missing frame>", null, FunctionKind.Unknown) });
                return m_Data.Frames.Count - 1;
            }

            static ulong ParseHex(string s)
            {
                if (string.IsNullOrEmpty(s))
                    return 0;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(2);
                ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v);
                return v;
            }
        }
    }
}
