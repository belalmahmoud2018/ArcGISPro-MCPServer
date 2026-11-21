using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ArcGISProMcp.Server.Bridge;
using ArcGISProMcp.Server.Setup;

namespace ArcGISProMcp.Server.Mcp
{
    internal static class LocalTools
    {
        public static async Task<JsonObject> CallAsync(string name, JsonObject args, IMcpPeer peer, CancellationToken ct)
        {
            switch (name)
            {
                case "pro_status": return await Status(ct);
                case "launch_arcgis_pro": return await Launch(args, ct);
                case "select_pro_instance":
                    var pid = (int?)args["pid"];
                    var inst = BridgeClient.Discover().FirstOrDefault(i => i.Pid == pid);
                    if (inst == null) return McpServer.ToolError($"No ArcGIS Pro instance with pid {pid}. Use pro_status.");
                    BridgeClient.SelectedPid = pid;
                    return McpServer.ToolText($"Commands now go to ArcGIS Pro pid {pid} ({inst.ProVersion}).");
                default: return McpServer.ToolError("Unknown local tool " + name);
            }
        }

        private static async Task<JsonObject> Status(CancellationToken ct)
        {
            var (exe, ver) = ProLocator.Find();
            var instances = BridgeClient.Discover();
            var o = new JsonObject
            {
                ["mcpServerVersion"] = AppInfo.Version,
                ["installedArcGISPro"] = exe == null ? null : new JsonObject { ["version"] = ver, ["exe"] = exe },
                ["supportedProVersions"] = "3.0 - 3.7 (any 3.x)",
                ["proProcessRunning"] = BridgeClient.IsProRunning(),
                ["addinInstalled"] = Installer.IsAddinInstalled(),
                ["connectedInstances"] = new JsonArray(instances.Select(i => (JsonNode)i.ToJson()).ToArray()),
                ["selectedPid"] = BridgeClient.Current()?.Pid
            };
            if (instances.Count > 0)
            {
                try
                {
                    var r = await BridgeClient.CallAsync("get_application_info", new JsonObject(), TimeSpan.FromSeconds(20), ct);
                    o["session"] = r["result"]?.DeepClone();
                }
                catch (Exception ex) { o["sessionError"] = ex.Message; }
            }
            else if (BridgeClient.IsProRunning())
                o["hint"] = "ArcGIS Pro is running but the MCP Bridge add-in has not registered. It may still be starting, or the add-in is blocked by Pro's add-in security setting.";
            else
                o["hint"] = "ArcGIS Pro is not running. Use launch_arcgis_pro.";
            return McpServer.ToolText(o.ToJsonString(Json.Indented));
        }

        private static async Task<JsonObject> Launch(JsonObject args, CancellationToken ct)
        {
            var project = (string)args["project_path"];
            var wait = (int?)args["wait_seconds"] ?? 240;
            if (BridgeClient.Current() != null && string.IsNullOrWhiteSpace(project))
                return McpServer.ToolText("ArcGIS Pro is already running and connected.");

            if (BridgeClient.Current() != null && !string.IsNullOrWhiteSpace(project))
            {
                var r = await BridgeClient.CallAsync("open_project", new JsonObject { ["path"] = project }, TimeSpan.FromSeconds(300), ct);
                return McpServer.ToContent(r);
            }

            var (exe, ver) = ProLocator.Find();
            if (exe == null) return McpServer.ToolError("ArcGIS Pro is not installed on this computer (ArcGISPro.exe not found).");
            if (!string.IsNullOrWhiteSpace(project) && !File.Exists(project))
                return McpServer.ToolError("Project not found: " + project);

            if (!BridgeClient.IsProRunning())
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(project)) psi.Arguments = "\"" + project + "\"";
                Process.Start(psi);
                Log.Info("Launched ArcGIS Pro " + ver);
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < wait)
            {
                ct.ThrowIfCancellationRequested();
                if (BridgeClient.Current() != null)
                {
                    try
                    {
                        await BridgeClient.CallAsync("bridge_ping", new JsonObject(), TimeSpan.FromSeconds(10), ct);
                        if (!string.IsNullOrWhiteSpace(project))
                        {
                            // The bridge starts before the project finishes opening: wait for it.
                            var info = await BridgeClient.CallAsync("get_application_info", new JsonObject(), TimeSpan.FromSeconds(20), ct);
                            if (info["result"]?["project"] == null) { await Task.Delay(2000, ct); continue; }
                            await Task.Delay(3000, ct); // let the map views finish initialising
                        }
                        return McpServer.ToolText($"ArcGIS Pro {ver} is ready (took {sw.Elapsed.TotalSeconds:0}s)." +
                            (string.IsNullOrWhiteSpace(project) ? " No project was specified: if Pro shows its start page, use open_project or new_project." : ""));
                    }
                    catch { }
                }
                await Task.Delay(2000, ct);
            }
            return McpServer.ToolError($"ArcGIS Pro did not become ready within {wait}s. It may be waiting for sign-in/licensing, or the add-in is blocked (Project > Add-In Manager > Options).");
        }
    }
}
