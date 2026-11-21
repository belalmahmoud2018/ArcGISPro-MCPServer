using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcGISProMcp.Server
{
    internal static class AppInfo
    {
        public const string Name = "arcgis-pro";
        public const string DisplayName = "ArcGIS Pro MCP Server";
        public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        public const string AddinGuid = "{6F1C3B2A-8D4E-4B7A-9C51-2E7D0A9F4B13}";
        public const string AddinFileName = "ArcGISProMcpBridge.esriAddinX";

        public static string DataDir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ArcGISProMCP");
        public static string InstalledExe => Path.Combine(InstallDir, "ArcGISProMCP.exe");
        public static string CurrentExe => Environment.ProcessPath;
        public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    internal static class Log
    {
        private static readonly object _lock = new object();
        public static string FilePath { get; set; } = Path.Combine(AppInfo.DataDir, "logs", "server.log");
        public static Action<string> Sink { get; set; }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : msg + ": " + ex);

        private static void Write(string level, string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
            try { Sink?.Invoke(line); } catch { }
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 10_000_000) fi.Delete();
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static readonly JsonSerializerOptions Compact = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static readonly JsonDocumentOptions Lenient = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        public static JsonNode ParseLenient(string text) =>
            string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, null, Lenient);
    }

    internal static class Native
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")] private static extern int GetFileType(IntPtr hFile);
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        public static Microsoft.Win32.SafeHandles.SafeFileHandle OpenConsoleOut() =>
            CreateFileW("CONOUT$", 0x40000000 /*GENERIC_WRITE*/, 2 /*FILE_SHARE_WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);

        public static bool StdoutIsRedirected()
        {
            var h = GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            var t = GetFileType(h);
            return t == 3 || t == 1;
        }

        /// <summary>True when stdin is a pipe/file, i.e. we were spawned by an MCP client.</summary>
        public static bool StdinIsRedirected()
        {
            var h = GetStdHandle(-10);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            var t = GetFileType(h);
            return t == 3 /*PIPE*/ || t == 1 /*DISK*/;
        }
    }
}
