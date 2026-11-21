using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ArcGISProMcp.Server.Bridge
{
    internal sealed class ProInstance
    {
        public int Pid { get; set; }
        public string Pipe { get; set; }
        public string ProVersion { get; set; }
        public DateTime StartedUtc { get; set; }

        public JsonObject ToJson() => new JsonObject
        {
            ["pid"] = Pid,
            ["arcgisProVersion"] = ProVersion,
            ["started"] = StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    internal sealed class BridgeException : Exception
    {
        public BridgeException(string m) : base(m) { }
    }

    /// <summary>Talks to the ArcGIS Pro add-in over a local named pipe.</summary>
    internal static class BridgeClient
    {
        public static int? SelectedPid { get; set; }
        private static long _id;

        public static string InstancesDir => Path.Combine(AppInfo.DataDir, "instances");

        public static List<ProInstance> Discover()
        {
            var list = new List<ProInstance>();
            if (!Directory.Exists(InstancesDir)) return list;
            foreach (var f in Directory.GetFiles(InstancesDir, "*.json"))
            {
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(f))?.AsObject();
                    var pid = (int)o["pid"];
                    bool alive;
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        alive = !p.HasExited && p.ProcessName.StartsWith("ArcGISPro", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { alive = false; }
                    if (!alive) { try { File.Delete(f); } catch { } continue; }
                    list.Add(new ProInstance
                    {
                        Pid = pid,
                        Pipe = (string)o["pipe"],
                        ProVersion = (string)o["proVersion"],
                        StartedUtc = DateTime.TryParse((string)o["startedUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue
                    });
                }
                catch { }
            }
            return list.OrderByDescending(i => i.StartedUtc).ToList();
        }

        public static ProInstance Current()
        {
            var all = Discover();
            if (SelectedPid != null)
            {
                var sel = all.FirstOrDefault(i => i.Pid == SelectedPid);
                if (sel != null) return sel;
            }
            return all.FirstOrDefault();
        }

        public static bool IsProRunning() =>
            Process.GetProcessesByName("ArcGISPro").Length > 0;

        public static async Task<JsonObject> CallAsync(string method, JsonObject args, TimeSpan timeout, CancellationToken ct)
        {
            var inst = Current();
            if (inst == null)
            {
                if (IsProRunning())
                    throw new BridgeException("ArcGIS Pro is running but the MCP Bridge add-in is not loaded. " +
                        "Make sure the add-in is installed (run ArcGISProMCP.exe and click Install) and allowed in Pro: " +
                        "Project > Add-In Manager > Options > 'Load all Add-Ins without restrictions'. Then restart ArcGIS Pro.");
                throw new BridgeException("ArcGIS Pro is not running. Call the 'launch_arcgis_pro' tool first (optionally with a project path).");
            }

            using var pipe = new NamedPipeClientStream(".", inst.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(5000, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new BridgeException($"Could not connect to ArcGIS Pro (pid {inst.Pid}). Pro may be busy or still starting; retry in a few seconds."); }

            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(pipe, utf8, 65536, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(pipe, utf8, false, 65536, leaveOpen: true);
            var id = Interlocked.Increment(ref _id);
            var req = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args?.DeepClone() ?? new JsonObject() };
            await writer.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            string line;
            try { line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new BridgeException($"ArcGIS Pro did not answer '{method}' within {timeout.TotalSeconds:0}s. The operation may still be running inside Pro.");
            }
            if (line == null) throw new BridgeException("ArcGIS Pro closed the connection (it may have crashed or been closed).");
            var resp = JsonNode.Parse(line)?.AsObject() ?? throw new BridgeException("Empty response from ArcGIS Pro.");
            if (resp["ok"]?.GetValue<bool>() != true) throw new BridgeException((string)resp["error"] ?? "Unknown error in ArcGIS Pro.");
            return resp;
        }
    }

    internal static class ProLocator
    {
        public static (string exe, string version) Find()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var k = hive.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro");
                    var dir = k?.GetValue("InstallDir") as string;
                    if (string.IsNullOrEmpty(dir)) continue;
                    var exe = Path.Combine(dir, "bin", "ArcGISPro.exe");
                    if (!File.Exists(exe)) continue;
                    var ver = k.GetValue("Version") as string ?? FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                    return (exe, ver);
                }
                catch { }
            }
            foreach (var guess in new[]
            {
                @"C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe",
                Path.Combine(AppInfo.Local, @"Programs\ArcGIS\Pro\bin\ArcGISPro.exe")
            })
                if (File.Exists(guess)) return (guess, FileVersionInfo.GetVersionInfo(guess).ProductVersion);
            return (null, null);
        }
    }
}
