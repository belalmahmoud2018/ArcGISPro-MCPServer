using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ArcGISProMcpBridge.Tools;

namespace ArcGISProMcpBridge.Bridge
{
    /// <summary>
    /// Local named-pipe server living inside ArcGIS Pro. The MCP server (ArcGISProMCP.exe)
    /// connects to it and sends newline-delimited JSON requests: {"id":1,"method":"tool","params":{}}.
    /// Every running Pro instance publishes a discovery file in %LOCALAPPDATA%\ArcGISProMCP\instances.
    /// </summary>
    internal static class BridgeHost
    {
        public const string ProtocolVersion = "1";
        private static CancellationTokenSource _cts;
        private static Task _loop;
        private static long _requests;

        public static string PipeName { get; private set; }
        public static bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
        public static long RequestCount => Interlocked.Read(ref _requests);
        public static string LastRequest { get; private set; } = "-";

        public static string DataDir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string InstanceFile => Path.Combine(DataDir, "instances", Environment.ProcessId + ".json");

        public static void Start()
        {
            if (IsRunning) return;
            PipeName = $"ArcGISProMcp_{Sanitize(Environment.UserName)}_{Environment.ProcessId}";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => AcceptLoop(ct));
            WriteInstanceFile();
            try { PythonRunner.EnsureToolbox(); } catch (Exception ex) { Log.Error("Python toolbox", ex); }
            Log.Info("Bridge started on pipe " + PipeName);
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            try { if (File.Exists(InstanceFile)) File.Delete(InstanceFile); } catch { }
            Log.Info("Bridge stopped");
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "user") sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static void WriteInstanceFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InstanceFile));
                var proExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var ver = "";
                try { ver = FileVersionInfo.GetVersionInfo(proExe).ProductVersion; } catch { }
                var o = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["pipe"] = PipeName,
                    ["proVersion"] = ver,
                    ["proExe"] = proExe,
                    ["bridgeProtocol"] = ProtocolVersion,
                    ["addinVersion"] = typeof(BridgeHost).Assembly.GetName().Version?.ToString(),
                    ["startedUtc"] = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(InstanceFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error("Instance file", ex); }
        }

        private static async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var s = server; server = null;
                    _ = Task.Run(() => HandleConnection(s, ct));
                }
                catch (OperationCanceledException) { server?.Dispose(); break; }
                catch (Exception ex)
                {
                    server?.Dispose();
                    Log.Error("Accept loop", ex);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private static async Task HandleConnection(NamedPipeServerStream stream, CancellationToken ct)
        {
            var utf8 = new UTF8Encoding(false);
            using (stream)
            using (var reader = new StreamReader(stream, utf8))
            using (var writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string line;
                    while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        Interlocked.Increment(ref _requests);
                        var response = await Dispatcher.HandleAsync(line).ConfigureAwait(false);
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                    }
                }
                catch (IOException) { /* client went away */ }
                catch (Exception ex) { Log.Error("Connection", ex); }
            }
        }

        internal static void NoteRequest(string method) => LastRequest = $"{method} @ {DateTime.Now:HH:mm:ss}";
    }

    internal static class Log
    {
        private static readonly object _lock = new object();
        private static string FilePath => Path.Combine(BridgeHost.DataDir, "logs", "addin.log");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg, Exception ex) => Write("ERROR", msg + ": " + ex);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 5_000_000) fi.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
