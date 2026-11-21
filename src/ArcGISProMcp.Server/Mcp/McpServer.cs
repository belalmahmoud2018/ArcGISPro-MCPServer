using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ArcGISProMcp.Server.Bridge;

namespace ArcGISProMcp.Server.Mcp
{
    /// <summary>Sends server -> client notifications (progress, logging) on the current transport.</summary>
    internal interface IMcpPeer
    {
        Task NotifyAsync(JsonObject notification);
        bool SupportsNotifications { get; }
    }

    /// <summary>
    /// Transport independent MCP (JSON-RPC 2.0) implementation.
    /// Supports protocol revisions 2024-11-05, 2025-03-26, 2025-06-18 and 2025-11-25.
    /// </summary>
    internal sealed class McpServer
    {
        private static readonly string[] Supported = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        private readonly Dictionary<string, CancellationTokenSource> _inflight = new Dictionary<string, CancellationTokenSource>();
        public string ClientName { get; private set; } = "unknown";

        public async Task<JsonNode> HandleMessageAsync(JsonNode msg, IMcpPeer peer)
        {
            if (msg is JsonArray batch)
            {
                var tasks = batch.Select(m => HandleSingleAsync(m as JsonObject, peer)).ToArray();
                var results = await Task.WhenAll(tasks);
                var arr = new JsonArray();
                foreach (var r in results) if (r != null) arr.Add(r);
                return arr.Count == 0 ? null : arr;
            }
            return await HandleSingleAsync(msg as JsonObject, peer);
        }

        private async Task<JsonObject> HandleSingleAsync(JsonObject req, IMcpPeer peer)
        {
            if (req == null) return Error(null, -32600, "Invalid Request");
            var id = req["id"]?.DeepClone();
            var method = (string)req["method"];
            var prm = req["params"] as JsonObject ?? new JsonObject();

            if (method == null) return null; // a response to something we sent - ignore
            var isNotification = !req.ContainsKey("id");
            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, Initialize(prm));
                    case "notifications/initialized":
                    case "notifications/roots/list_changed":
                        return null;
                    case "notifications/cancelled":
                        var rid = prm["requestId"]?.ToJsonString();
                        lock (_inflight) if (rid != null && _inflight.TryGetValue(rid, out var c)) c.Cancel();
                        return null;
                    case "ping":
                        return Result(id, new JsonObject());
                    case "tools/list":
                        return Result(id, new JsonObject { ["tools"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode)t.ToJson()).ToArray()) });
                    case "tools/call":
                        return Result(id, await CallToolAsync(id, prm, peer));
                    case "prompts/list":
                        return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
                    case "resources/list":
                        return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                    case "resources/templates/list":
                        return Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
                    case "logging/setLevel":
                        return Result(id, new JsonObject());
                    case "completion/complete":
                        return Result(id, new JsonObject { ["completion"] = new JsonObject { ["values"] = new JsonArray(), ["hasMore"] = false } });
                    default:
                        if (isNotification) return null;
                        return Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Request " + method, ex);
                return isNotification ? null : Error(id, -32603, ex.Message);
            }
        }

        private JsonObject Initialize(JsonObject prm)
        {
            var requested = (string)prm["protocolVersion"];
            var version = Supported.Contains(requested) ? requested : "2025-06-18";
            ClientName = (string)prm["clientInfo"]?["name"] ?? "unknown";
            Log.Info($"initialize from {ClientName} {(string)prm["clientInfo"]?["version"]} (protocol {requested} -> {version})");
            return new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false },
                    ["logging"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = AppInfo.Name,
                    ["title"] = AppInfo.DisplayName,
                    ["version"] = AppInfo.Version
                },
                ["instructions"] = ToolCatalog.Instructions
            };
        }

        private async Task<JsonObject> CallToolAsync(JsonNode id, JsonObject prm, IMcpPeer peer)
        {
            var name = (string)prm["name"];
            var args = prm["arguments"] as JsonObject ?? new JsonObject();
            if (name == null || !ToolCatalog.ByName.TryGetValue(name, out var tool))
                return ToolError($"Unknown tool '{name}'.");

            var progressToken = prm["_meta"]?["progressToken"]?.DeepClone();
            using var cts = new CancellationTokenSource();
            var key = id?.ToJsonString() ?? Guid.NewGuid().ToString();
            lock (_inflight) _inflight[key] = cts;

            // Heartbeat progress notifications keep clients from timing out on long tools.
            Task heartbeat = Task.CompletedTask;
            using var hbCts = new CancellationTokenSource();
            if (progressToken != null && peer.SupportsNotifications)
            {
                heartbeat = Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var n = 0;
                    while (!hbCts.IsCancellationRequested)
                    {
                        try { await Task.Delay(5000, hbCts.Token); } catch { break; }
                        n++;
                        await peer.NotifyAsync(new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["method"] = "notifications/progress",
                            ["params"] = new JsonObject
                            {
                                ["progressToken"] = progressToken.DeepClone(),
                                ["progress"] = n,
                                ["message"] = $"{tool.Title}: running in ArcGIS Pro ({sw.Elapsed.TotalSeconds:0}s)"
                            }
                        });
                    }
                });
            }

            var started = Stopwatch.StartNew();
            try
            {
                JsonObject result = tool.Local
                    ? await LocalTools.CallAsync(name, args, peer, cts.Token)
                    : ToContent(await BridgeClient.CallAsync(name, args, TimeSpan.FromSeconds(tool.TimeoutSec), cts.Token));
                Log.Info($"tool {name} ok in {started.ElapsedMilliseconds} ms");
                return result;
            }
            catch (BridgeException bex)
            {
                Log.Warn($"tool {name} failed: {bex.Message}");
                return ToolError(bex.Message);
            }
            catch (OperationCanceledException)
            {
                return ToolError("Cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("tool " + name, ex);
                return ToolError(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                hbCts.Cancel();
                try { await heartbeat; } catch { }
                lock (_inflight) _inflight.Remove(key);
            }
        }

        /// <summary>Converts a bridge response into MCP content blocks (JSON text + images).</summary>
        public static JsonObject ToContent(JsonObject bridgeResponse)
        {
            var content = new JsonArray();
            var data = bridgeResponse["result"];
            var text = data == null ? "OK" : data.ToJsonString(Json.Indented);
            if (text.Length > 400_000) text = text.Substring(0, 400_000) + "\n... [truncated]";
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            if (bridgeResponse["images"] is JsonArray imgs)
                foreach (var img in imgs)
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = img["data"]?.DeepClone(), ["mimeType"] = img["mimeType"]?.DeepClone() ?? "image/png" });
            var isErr = data is JsonObject o && o["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok;
            return new JsonObject { ["content"] = content, ["isError"] = isErr };
        }

        public static JsonObject ToolText(string text, bool isError = false) =>
            new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };

        public static JsonObject ToolError(string text) => ToolText(text, true);

        private static JsonObject Result(JsonNode id, JsonNode result) =>
            id == null ? null : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode id, int code, string message) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    }
}
