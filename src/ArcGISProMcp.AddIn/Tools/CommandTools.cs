using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Xml.Linq;
using ArcGIS.Desktop.Framework;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    /// <summary>
    /// Access to every ribbon button / tool / menu in ArcGIS Pro via its DAML id.
    /// The catalog is built by scanning the DAML files shipped with the installed Pro version,
    /// so it always matches the running release (3.0 ... 3.7+).
    /// </summary>
    internal static class CommandTools
    {
        private sealed class Cmd
        {
            public string Id, Caption, Kind, Tooltip, Extension;
        }

        private static List<Cmd> _catalog;
        private static readonly object _lock = new object();
        private static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "button", "tool", "checkBox", "comboBox", "editBox", "gallery", "menu", "splitButton", "dynamicMenu", "customControl", "labelControl", "dockPane", "pane", "sheet", "tab" };

        private static List<Cmd> Catalog()
        {
            lock (_lock)
            {
                if (_catalog != null) return _catalog;
                var list = new List<Cmd>();
                try
                {
                    var bin = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                    var files = Directory.EnumerateFiles(Path.Combine(bin, "Extensions"), "*.daml", SearchOption.AllDirectories).ToList();
                    foreach (var f in files)
                    {
                        try
                        {
                            var doc = XDocument.Load(f);
                            var ext = Path.GetFileNameWithoutExtension(f);
                            foreach (var el in doc.Descendants())
                            {
                                var kind = el.Name.LocalName;
                                if (!Kinds.Contains(kind)) continue;
                                var id = (string)el.Attribute("id");
                                if (string.IsNullOrEmpty(id)) continue;
                                var tip = el.Elements().FirstOrDefault(x => x.Name.LocalName == "tooltip");
                                list.Add(new Cmd
                                {
                                    Id = id,
                                    Kind = kind,
                                    Caption = (string)el.Attribute("caption") ?? "",
                                    Tooltip = ((string)tip?.Attribute("heading") ?? "") + " " + (tip?.Nodes().OfType<XText>().FirstOrDefault()?.Value.Trim() ?? ""),
                                    Extension = ext
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Log.Error("DAML scan", ex); }
                _catalog = list.GroupBy(c => c.Id).Select(g => g.First()).ToList();
                return _catalog;
            }
        }

        [Tool("search_commands")]
        public static async Task<ToolResult> Search(Args a)
        {
            var q = (a.Str("query") ?? "").Trim();
            var limit = a.Int("limit", 40);
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var cat = await Task.Run(Catalog);
            var hits = cat.Select(c =>
            {
                var hay = (c.Id + " " + c.Caption + " " + c.Tooltip).ToLowerInvariant();
                int score = words.Count(w => hay.Contains(w.ToLowerInvariant()));
                if (c.Caption.Equals(q, StringComparison.OrdinalIgnoreCase)) score += 5;
                return (c, score);
            })
            .Where(x => words.Length == 0 || x.score == words.Length || x.score >= 5)
            .OrderByDescending(x => x.score).ThenBy(x => x.c.Caption.Length)
            .Take(limit)
            .Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.c.Id,
                ["caption"] = x.c.Caption,
                ["kind"] = x.c.Kind,
                ["tooltip"] = string.IsNullOrWhiteSpace(x.c.Tooltip) ? null : x.c.Tooltip.Trim(),
                ["module"] = x.c.Extension
            }).ToArray();
            return ToolResult.Of(new JsonObject { ["totalCommands"] = cat.Count, ["matches"] = new JsonArray(hits) });
        }

        [Tool("execute_command")]
        public static async Task<ToolResult> Execute(Args a)
        {
            var id = a.Req("command_id");
            var res = await Ui(async () =>
            {
                var wrapper = FrameworkApplication.GetPlugInWrapper(id, true);
                if (wrapper == null) throw new ToolException($"Command '{id}' not found. Use search_commands to find valid ids.");
                var kind = Catalog().FirstOrDefault(c => c.Id == id)?.Kind;
                if (string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    await FrameworkApplication.SetCurrentToolAsync(id);
                    return new JsonObject { ["command"] = id, ["caption"] = wrapper.Caption, ["result"] = "activated as current map tool" };
                }
                if (string.Equals(kind, "dockPane", StringComparison.OrdinalIgnoreCase))
                {
                    var dp = FrameworkApplication.DockPaneManager.Find(id);
                    dp?.Activate();
                    return new JsonObject { ["command"] = id, ["result"] = dp != null ? "dock pane shown" : "dock pane not found" };
                }
                if (wrapper is ICommand cmd)
                {
                    var p = a.Str("parameter");
                    if (!cmd.CanExecute(p))
                        return new JsonObject { ["command"] = id, ["caption"] = wrapper.Caption, ["result"] = "command is currently disabled (its condition is not met)", ["enabled"] = false, ["disabledReason"] = wrapper.DisabledTooltip };
                    cmd.Execute(p);
                    return new JsonObject { ["command"] = id, ["caption"] = wrapper.Caption, ["result"] = "executed" };
                }
                throw new ToolException($"'{id}' ({kind ?? wrapper.GetType().Name}) is not executable.");
            });
            return ToolResult.Of(res);
        }
    }
}
