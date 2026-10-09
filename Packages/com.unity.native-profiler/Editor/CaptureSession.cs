using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HU.NativeProfiler
{
    internal enum CapturePhase
    {
        Idle,
        Recording,
        Exporting,
        Parsing,
        Ready,
        Error,
    }

    /// <summary>
    /// Drives record -> export -> parse -> symbolicate. The xctrace work runs in a detached shell
    /// script that leaves marker files behind, so a capture survives domain reloads (e.g. entering
    /// Play Mode while recording): after the reload we just keep polling the capture directory.
    /// </summary>
    [InitializeOnLoad]
    internal static class CaptureSession
    {
        const string k_TimeProfileXPath = "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"time-profile\"]";
        const string k_PhaseKey = "HU.NativeProfiler.Phase";
        const string k_DirKey = "HU.NativeProfiler.Dir";
        const string k_ShellPidKey = "HU.NativeProfiler.ShellPid";
        const string k_RecordStartKey = "HU.NativeProfiler.RecordStart";
        const string k_ErrorKey = "HU.NativeProfiler.Error";

        /// <summary>When the current scripting domain came to life. JIT addresses sampled before this point are dead.</summary>
        public static readonly DateTime DomainLoadedUtc;

        public static event Action Changed;

        public static CapturePhase Phase { get; private set; }
        public static string CaptureDirectory { get; private set; }
        public static string Error { get; private set; }
        public static ProfileData Data { get; private set; }
        public static DateTime RecordStartUtc { get; private set; }

        static Task<ProfileData> s_ParseTask;
        static double s_NextPoll;

        static CaptureSession()
        {
            DomainLoadedUtc = DateTime.UtcNow;
            Phase = (CapturePhase)SessionState.GetInt(k_PhaseKey, (int)CapturePhase.Idle);
            CaptureDirectory = SessionState.GetString(k_DirKey, null);
            Error = SessionState.GetString(k_ErrorKey, null);
            RecordStartUtc = new DateTime(long.Parse(SessionState.GetString(k_RecordStartKey, "0")), DateTimeKind.Utc);

            // Data lives in memory only: re-load it from disk after a domain reload.
            if (Phase == CapturePhase.Parsing || Phase == CapturePhase.Ready)
                BeginParse(CaptureDirectory);

            EditorApplication.update += Poll;
        }

        public static string CapturesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "NativeProfiler"));
        public static bool IsBusy => Phase == CapturePhase.Recording || Phase == CapturePhase.Exporting || Phase == CapturePhase.Parsing;
        public static bool IsSupported => Application.platform == RuntimePlatform.OSXEditor;

        // ---------------------------------------------------------------------------------------
        // Public API

        /// <param name="target">pid or process name accepted by `xctrace record --attach`.</param>
        /// <param name="timeLimitSeconds">0 = record until <see cref="StopRecording"/>.</param>
        public static void StartRecording(string target, float timeLimitSeconds)
        {
            if (IsBusy)
                return;
            var dir = NewCaptureDirectory();
            var trace = Path.Combine(dir, "capture.trace");

            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("cd \"$(dirname \"$0\")\"");
            sb.AppendLine("XCTRACE=\"$(xcrun --find xctrace)\" || { echo 127 > record.exit; echo 127 > done; exit 1; }");
            var limit = timeLimitSeconds > 0 ? $" --time-limit {Mathf.CeilToInt(timeLimitSeconds * 1000)}ms" : string.Empty;
            sb.AppendLine($"\"$XCTRACE\" record --template 'Time Profiler' --attach {Quote(target)}{limit} --output capture.trace --no-prompt > record.log 2>&1");
            sb.AppendLine("echo $? > record.exit");
            AppendExport(sb);
            RunScript(dir, sb.ToString(), trace);
            SetPhase(CapturePhase.Recording);
        }

        /// <summary>Imports an existing .trace (made by xctrace or Instruments). Managed frames are only resolvable if a symbols cache sits next to it.</summary>
        public static void ImportTrace(string tracePath)
        {
            if (IsBusy)
                return;
            var dir = NewCaptureDirectory();
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("cd \"$(dirname \"$0\")\"");
            sb.AppendLine("XCTRACE=\"$(xcrun --find xctrace)\" || { echo 127 > done; exit 1; }");
            sb.AppendLine($"ln -s {Quote(tracePath)} capture.trace");
            sb.AppendLine("echo 0 > record.exit");
            AppendExport(sb);
            RunScript(dir, sb.ToString(), tracePath);
            SetPhase(CapturePhase.Exporting);
        }

        public static void StopRecording()
        {
            if (Phase != CapturePhase.Recording)
                return;
            // xctrace finalizes the trace on SIGINT (same as Ctrl-C). It is the child of our shell script.
            var shellPid = SessionState.GetInt(k_ShellPidKey, 0);
            if (shellPid > 0)
                RunAndWait("/usr/bin/pkill", $"-INT -P {shellPid}");
        }

        public static void OpenCapture(string dir)
        {
            if (IsBusy || !File.Exists(Path.Combine(dir, "time-profile.xml")))
                return;
            CaptureDirectory = dir;
            SessionState.SetString(k_DirKey, dir);
            BeginParse(dir);
        }

        public static void Clear()
        {
            if (IsBusy)
                return;
            Data = null;
            SetPhase(CapturePhase.Idle);
        }

        // ---------------------------------------------------------------------------------------
        // Pipeline

        static void AppendExport(StringBuilder sb)
        {
            sb.AppendLine("\"$XCTRACE\" export --input capture.trace --toc --output toc.xml > export.log 2>&1 &&");
            sb.AppendLine($"\"$XCTRACE\" export --input capture.trace --xpath '{k_TimeProfileXPath}' --output time-profile.xml >> export.log 2>&1");
            sb.AppendLine("echo $? > done");
        }

        static string NewCaptureDirectory()
        {
            var dir = Path.Combine(CapturesRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void RunScript(string dir, string script, string tracePath)
        {
            var scriptPath = Path.Combine(dir, "run.sh");
            File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));

            CaptureDirectory = dir;
            Error = null;
            Data = null;
            RecordStartUtc = DateTime.UtcNow;
            SessionState.SetString(k_DirKey, dir);
            SessionState.SetString(k_RecordStartKey, RecordStartUtc.Ticks.ToString());
            SessionState.EraseString(k_ErrorKey);

            var psi = new ProcessStartInfo("/bin/sh", Quote(scriptPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            var process = Process.Start(psi);
            SessionState.SetInt(k_ShellPidKey, process?.Id ?? 0);
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_NextPoll)
                return;
            s_NextPoll = EditorApplication.timeSinceStartup + 0.2;

            switch (Phase)
            {
                case CapturePhase.Recording:
                case CapturePhase.Exporting:
                    PollScript();
                    break;
                case CapturePhase.Parsing:
                    PollParse();
                    break;
            }
        }

        static void PollScript()
        {
            var dir = CaptureDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Fail("Capture directory disappeared.");
                return;
            }

            if (Phase == CapturePhase.Recording && File.Exists(Path.Combine(dir, "record.exit")))
            {
                var code = ReadExitCode(Path.Combine(dir, "record.exit"));
                // xctrace exits non-zero when interrupted with SIGINT but still writes a valid trace.
                if (code != 0 && !Directory.Exists(Path.Combine(dir, "capture.trace")))
                {
                    Fail("xctrace record failed:\n" + ReadTail(Path.Combine(dir, "record.log")));
                    return;
                }
                SetPhase(CapturePhase.Exporting);
            }

            var donePath = Path.Combine(dir, "done");
            if (File.Exists(donePath))
            {
                var code = ReadExitCode(donePath);
                if (code != 0 || !File.Exists(Path.Combine(dir, "time-profile.xml")))
                {
                    Fail($"xctrace export failed (exit {code}):\n" + ReadTail(Path.Combine(dir, "export.log")) + ReadTail(Path.Combine(dir, "record.log")));
                    return;
                }
                BeginParse(dir);
                return;
            }

            // Shell died without leaving the marker (killed, crashed...).
            var shellPid = SessionState.GetInt(k_ShellPidKey, 0);
            if (shellPid > 0 && !IsProcessAlive(shellPid) && !File.Exists(donePath))
                Fail("Capture script exited unexpectedly:\n" + ReadTail(Path.Combine(dir, "record.log")));
        }

        static void BeginParse(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "time-profile.xml")))
            {
                SetPhase(CapturePhase.Idle);
                return;
            }
            SetPhase(CapturePhase.Parsing);
            s_ParseTask = Task.Run(() =>
            {
                var data = new ProfileData { CaptureDirectory = dir, TracePath = Path.Combine(dir, "capture.trace") };
                var toc = Path.Combine(dir, "toc.xml");
                if (File.Exists(toc))
                    XctraceParser.ParseToc(toc, data);
                XctraceParser.ParseTimeProfile(Path.Combine(dir, "time-profile.xml"), data);
                return data;
            });
        }

        static void PollParse()
        {
            if (s_ParseTask == null)
            {
                BeginParse(CaptureDirectory);
                return;
            }
            if (!s_ParseTask.IsCompleted)
                return;

            var task = s_ParseTask;
            s_ParseTask = null;
            if (task.IsFaulted)
            {
                Fail("Failed to parse xctrace export: " + task.Exception?.GetBaseException().Message);
                return;
            }

            var data = task.Result;
            try
            {
                Symbolicator.Symbolicate(data, DomainLoadedUtc);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                data.SymbolicationNote = "Symbolication failed: " + e.Message;
            }
            Data = data;
            SetPhase(CapturePhase.Ready);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers

        static void SetPhase(CapturePhase phase)
        {
            Phase = phase;
            SessionState.SetInt(k_PhaseKey, (int)phase);
            Changed?.Invoke();
        }

        static void Fail(string message)
        {
            Error = message;
            SessionState.SetString(k_ErrorKey, message);
            Debug.LogError("[NativeProfiler] " + message);
            SetPhase(CapturePhase.Error);
        }

        static int ReadExitCode(string path)
        {
            try { return int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : -1; }
            catch (IOException) { return -1; }
        }

        static string ReadTail(string path, int maxChars = 2000)
        {
            if (!File.Exists(path))
                return string.Empty;
            var text = File.ReadAllText(path);
            return text.Length > maxChars ? text.Substring(text.Length - maxChars) : text;
        }

        internal static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

        [DllImport("/usr/lib/libSystem.B.dylib")] static extern int kill(int pid, int sig);

        static bool IsProcessAlive(int pid) => kill(pid, 0) == 0;

        internal static string RunAndWait(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return output;
            }
        }

        public static List<(int pid, string name)> ListGuiProcesses()
        {
            var result = new List<(int, string)>();
            var output = RunAndWait("/bin/ps", "-axo pid=,comm=");
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                var space = trimmed.IndexOf(' ');
                if (space <= 0 || !int.TryParse(trimmed.Substring(0, space), out var pid))
                    continue;
                var path = trimmed.Substring(space + 1).Trim();
                if (!path.Contains(".app/Contents/MacOS/"))
                    continue;
                result.Add((pid, Path.GetFileName(path)));
            }
            result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
