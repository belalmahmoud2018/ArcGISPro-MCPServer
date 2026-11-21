using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ArcGISProMcp.Server.Setup
{
    /// <summary>One AI agent / MCP client whose configuration we can write automatically.</summary>
    internal abstract class AiClient
    {
        public string Id { get; protected set; }
        public string Name { get; protected set; }
        public abstract string ConfigPath { get; }
        /// <summary>Folder whose existence means the client is installed / has been used.</summary>
        public abstract string DetectPath { get; }
        public virtual bool IsDetected => DetectPath != null && (Directory.Exists(DetectPath) || File.Exists(DetectPath));
        public abstract bool IsConfigured();
        public abstract void Configure(string exe);
        public abstract void Remove();

        protected static void Backup(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var bak = path + ".bak-arcgispro-mcp";
                    if (!File.Exists(bak)) File.Copy(path, bak);
                }
            }
            catch { }
        }

        protected static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public override string ToString() => Name;
    }

    /// <summary>Clients using a JSON file with { "&lt;container&gt;": { "arcgis-pro": {...} } }.</summary>
    internal sealed class JsonFileClient : AiClient
    {
        private readonly Func<string> _path;
        private readonly Func<string> _detect;
        private readonly string[] _container;
        private readonly Func<string, JsonObject> _entry;

        public JsonFileClient(string id, string name, Func<string> path, Func<string> detect, string container, Func<string, JsonObject> entry)
        {
            Id = id; Name = name; _path = path; _detect = detect; _container = container.Split('.'); _entry = entry;
        }

        public override string ConfigPath => _path();
        public override string DetectPath => _detect();

        private JsonObject Load()
        {
            if (!File.Exists(ConfigPath)) return new JsonObject();
            var text = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return Json.ParseLenient(text) as JsonObject ?? throw new InvalidDataException(ConfigPath + " is not a JSON object.");
        }

        private JsonObject Container(JsonObject root, bool create)
        {
            JsonObject cur = root;
            foreach (var key in _container)
            {
                if (cur[key] is JsonObject next) { cur = next; continue; }
                if (!create) return null;
                next = new JsonObject();
                cur[key] = next;
                cur = next;
            }
            return cur;
        }

        public override bool IsConfigured()
        {
            try { return Container(Load(), false)?.ContainsKey(AppInfo.Name) == true; } catch { return false; }
        }

        public override void Configure(string exe)
        {
            var root = Load();
            Backup(ConfigPath);
            Container(root, true)[AppInfo.Name] = _entry(exe);
            WriteAtomic(ConfigPath, root.ToJsonString(Json.Indented));
        }

        public override void Remove()
        {
            if (!File.Exists(ConfigPath)) return;
            var root = Load();
            var c = Container(root, false);
            if (c == null || !c.Remove(AppInfo.Name)) return;
            WriteAtomic(ConfigPath, root.ToJsonString(Json.Indented));
        }
    }

    /// <summary>OpenAI Codex CLI / IDE extension: ~/.codex/config.toml</summary>
    internal sealed class CodexClient : AiClient
    {
        public CodexClient() { Id = "codex"; Name = "OpenAI Codex (CLI / IDE / App)"; }
        public override string ConfigPath => Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(AppInfo.Home, ".codex"), "config.toml");
        public override string DetectPath => Path.GetDirectoryName(ConfigPath);
        private const string Header = "[mcp_servers.arcgis-pro]";

        private static List<string> StripBlock(IEnumerable<string> lines)
        {
            var result = new List<string>();
            bool skipping = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("["))
                    skipping = t == Header || t.StartsWith("[mcp_servers.arcgis-pro.");
                if (!skipping) result.Add(line);
            }
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
            return result;
        }

        public override bool IsConfigured() => File.Exists(ConfigPath) && File.ReadAllLines(ConfigPath).Any(l => l.Trim() == Header);

        public override void Configure(string exe)
        {
            var lines = File.Exists(ConfigPath) ? StripBlock(File.ReadAllLines(ConfigPath)) : new List<string>();
            Backup(ConfigPath);
            if (lines.Count > 0) lines.Add("");
            lines.Add(Header);
            lines.Add($"command = '{exe}'");
            lines.Add("args = [\"--stdio\"]");
            lines.Add("startup_timeout_sec = 60");
            lines.Add("tool_timeout_sec = 3600");
            WriteAtomic(ConfigPath, string.Join("\n", lines) + "\n");
        }

        public override void Remove()
        {
            if (!IsConfigured()) return;
            WriteAtomic(ConfigPath, string.Join("\n", StripBlock(File.ReadAllLines(ConfigPath))) + "\n");
        }
    }

    /// <summary>Claude Code: uses the official CLI when available, else ~/.claude.json (user scope).</summary>
    internal sealed class ClaudeCodeClient : AiClient
    {
        private readonly JsonFileClient _json;
        public ClaudeCodeClient()
        {
            Id = "claude-code"; Name = "Claude Code (CLI / VS Code / JetBrains)";
            _json = new JsonFileClient("claude-code-json", Name, () => Path.Combine(AppInfo.Home, ".claude.json"),
                () => Path.Combine(AppInfo.Home, ".claude"), "mcpServers", ClientRegistry.StdioEntry(true));
        }
        public override string ConfigPath => _json.ConfigPath;
        public override string DetectPath => Path.Combine(AppInfo.Home, ".claude");
        public override bool IsDetected => base.IsDetected || File.Exists(ConfigPath) || FindCli() != null;
        public override bool IsConfigured() => _json.IsConfigured();

        private static string FindCli()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                foreach (var n in new[] { "claude.exe", "claude.cmd" })
                {
                    try { var p = Path.Combine(dir.Trim(), n); if (File.Exists(p)) return p; } catch { }
                }
            var local = Path.Combine(AppInfo.Home, ".local", "bin", "claude.exe");
            return File.Exists(local) ? local : null;
        }

        private static bool RunCli(string cli, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(cli, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                p.WaitForExit(30000);
                return p.HasExited && p.ExitCode == 0;
            }
            catch { return false; }
        }

        public override void Configure(string exe)
        {
            var cli = FindCli();
            if (cli != null)
            {
                RunCli(cli, "mcp remove arcgis-pro --scope user");
                var json = ClientRegistry.StdioEntry(true)(exe).ToJsonString(Json.Compact).Replace("\"", "\\\"");
                if (RunCli(cli, $"mcp add-json arcgis-pro \"{json}\" --scope user")) return;
            }
            _json.Configure(exe);
        }

        public override void Remove()
        {
            var cli = FindCli();
            if (cli != null) RunCli(cli, "mcp remove arcgis-pro --scope user");
            try { _json.Remove(); } catch { }
        }
    }

    internal static class ClientRegistry
    {
        public static Func<string, JsonObject> StdioEntry(bool withType = false, int? timeoutMs = null, int? timeoutSec = null) => exe =>
        {
            var o = new JsonObject();
            if (withType) o["type"] = "stdio";
            o["command"] = exe;
            o["args"] = new JsonArray("--stdio");
            if (timeoutMs != null) o["timeout"] = timeoutMs;
            if (timeoutSec != null) o["timeout"] = timeoutSec;
            return o;
        };

        private static string H(params string[] p) => Path.Combine(new[] { AppInfo.Home }.Concat(p).ToArray());
        private static string R(params string[] p) => Path.Combine(new[] { AppInfo.Roaming }.Concat(p).ToArray());

        private static string ClaudeDesktopDir()
        {
            // Store (MSIX) build keeps its config in the package's virtualised AppData.
            var pkgRoot = Path.Combine(AppInfo.Local, "Packages");
            try
            {
                if (Directory.Exists(pkgRoot))
                    foreach (var d in Directory.GetDirectories(pkgRoot, "Claude_*"))
                    {
                        var p = Path.Combine(d, "LocalCache", "Roaming", "Claude");
                        if (Directory.Exists(p)) return p;
                    }
            }
            catch { }
            return R("Claude");
        }

        public static List<AiClient> All() => new List<AiClient>
        {
            new JsonFileClient("claude-desktop", "Claude Desktop", () => Path.Combine(ClaudeDesktopDir(), "claude_desktop_config.json"), ClaudeDesktopDir, "mcpServers", StdioEntry()),
            new ClaudeCodeClient(),
            new CodexClient(),
            new JsonFileClient("gemini-cli", "Gemini CLI", () => H(".gemini", "settings.json"), () => H(".gemini"), "mcpServers", StdioEntry(false, 3600000)),
            new JsonFileClient("antigravity", "Google Antigravity", () => H(".gemini", "antigravity", "mcp_config.json"), () => H(".gemini", "antigravity"), "mcpServers", StdioEntry()),
            new JsonFileClient("cursor", "Cursor", () => H(".cursor", "mcp.json"), () => H(".cursor"), "mcpServers", StdioEntry()),
            new JsonFileClient("windsurf", "Windsurf", () => H(".codeium", "windsurf", "mcp_config.json"), () => H(".codeium", "windsurf"), "mcpServers", StdioEntry()),
            new JsonFileClient("vscode", "VS Code (GitHub Copilot agent)", () => R("Code", "User", "mcp.json"), () => R("Code", "User"), "servers", StdioEntry(true)),
            new JsonFileClient("vscode-insiders", "VS Code Insiders", () => R("Code - Insiders", "User", "mcp.json"), () => R("Code - Insiders", "User"), "servers", StdioEntry(true)),
            new JsonFileClient("cline", "Cline (VS Code)", () => R("Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                () => R("Code", "User", "globalStorage", "saoudrizwan.claude-dev"), "mcpServers", StdioEntry(false, null, 3600)),
            new JsonFileClient("roo", "Roo Code (VS Code)", () => R("Code", "User", "globalStorage", "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json"),
                () => R("Code", "User", "globalStorage", "rooveterinaryinc.roo-cline"), "mcpServers", StdioEntry(false, null, 3600)),
            new JsonFileClient("kiro", "Kiro", () => H(".kiro", "settings", "mcp.json"), () => H(".kiro"), "mcpServers", StdioEntry()),
            new JsonFileClient("trae", "Trae", () => R("Trae", "User", "mcp.json"), () => R("Trae"), "mcpServers", StdioEntry()),
            new JsonFileClient("qwen", "Qwen Code", () => H(".qwen", "settings.json"), () => H(".qwen"), "mcpServers", StdioEntry(false, 3600000)),
            new JsonFileClient("lmstudio", "LM Studio (local models: DeepSeek, Qwen, Llama ...)", () => H(".lmstudio", "mcp.json"), () => H(".lmstudio"), "mcpServers", StdioEntry()),
            new JsonFileClient("amazonq", "Amazon Q Developer", () => H(".aws", "amazonq", "mcp.json"), () => H(".aws", "amazonq"), "mcpServers", StdioEntry()),
            new JsonFileClient("opencode", "opencode", () => H(".config", "opencode", "opencode.json"), () => H(".config", "opencode"), "mcp",
                exe => new JsonObject { ["type"] = "local", ["command"] = new JsonArray(exe, "--stdio"), ["enabled"] = true }),
        };

        /// <summary>Generic snippet for any other MCP client (Cherry Studio, Chatbox, DeepSeek-based agents, Continue, Zed, Goose ...).</summary>
        public static string GenericJson(string exe) =>
            new JsonObject { ["mcpServers"] = new JsonObject { [AppInfo.Name] = StdioEntry()(exe) } }.ToJsonString(Json.Indented);
    }
}
