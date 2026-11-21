using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcGISProMcp.Server.Mcp
{
    /// <summary>stdio transport: newline-delimited JSON-RPC on stdin/stdout (Claude Desktop, Claude Code, Codex, Gemini CLI, Cursor ...).</summary>
    internal sealed class StdioTransport : IMcpPeer
    {
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private Stream _out;
        public bool SupportsNotifications => true;

        public async Task RunAsync()
        {
            var utf8 = new UTF8Encoding(false);
            _out = Console.OpenStandardOutput();
            var reader = new StreamReader(Console.OpenStandardInput(), utf8);
            var server = new McpServer();
            Log.Info("stdio transport started (pid " + Environment.ProcessId + ")");
            var pending = new ConcurrentDictionary<Task, bool>();
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode msg;
                try { msg = JsonNode.Parse(line); }
                catch
                {
                    await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } });
                    continue;
                }
                // Handle concurrently so long tools don't block pings / cancellations.
                var t = Task.Run(async () =>
                {
                    var resp = await server.HandleMessageAsync(msg, this);
                    if (resp != null) await WriteAsync(resp);
                });
                pending[t] = true;
                _ = t.ContinueWith(x => pending.TryRemove(x, out _));
            }
            await Task.WhenAll(pending.Keys.ToArray());
            Log.Info("stdin closed, exiting");
        }

        public Task NotifyAsync(JsonObject n) => WriteAsync(n);

        private async Task WriteAsync(JsonNode node)
        {
            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(Json.Compact) + "\n");
            await _writeLock.WaitAsync();
            try { await _out.WriteAsync(bytes); await _out.FlushAsync(); }
            finally { _writeLock.Release(); }
        }
    }

    internal sealed class HttpOptions
    {
        public string Host = "127.0.0.1";
        public int Port = 3939;
        public string Token;
    }

    /// <summary>
    /// HTTP transports:
    ///   POST /mcp           Streamable HTTP (MCP 2025-03-26+): ChatGPT connectors / developer mode, Claude.ai, Copilot, n8n ...
    ///   GET  /sse + POST /messages   legacy HTTP+SSE transport (older clients)
    /// Auth: optional token via "Authorization: Bearer", "?token=" or path /mcp/{token}.
    /// </summary>
    internal sealed class HttpTransport
    {
        private readonly HttpOptions _opt;
        private WebApplication _app;
        private readonly McpServer _server = new McpServer();
        private readonly ConcurrentDictionary<string, SseSession> _sse = new ConcurrentDictionary<string, SseSession>();

        public HttpTransport(HttpOptions opt) { _opt = opt; }

        public string BaseUrl => $"http://{(_opt.Host == "0.0.0.0" ? "localhost" : _opt.Host)}:{_opt.Port}";

        private sealed class SseSession : IMcpPeer
        {
            public readonly Channel<string> Out = Channel.CreateUnbounded<string>();
            public bool SupportsNotifications => true;
            public Task NotifyAsync(JsonObject n) { Out.Writer.TryWrite(n.ToJsonString(Json.Compact)); return Task.CompletedTask; }
        }

        private sealed class NoPeer : IMcpPeer
        {
            public bool SupportsNotifications => false;
            public Task NotifyAsync(JsonObject n) => Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://{_opt.Host}:{_opt.Port}");
            _app = builder.Build();

            _app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                ctx.Response.Headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id";
                if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
                if (ctx.Request.Path == "/" || ctx.Request.Path == "/health") { await next(); return; }
                if (!Authorized(ctx)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("Unauthorized: missing or wrong token"); return; }
                await next();
            });

            _app.MapGet("/", () => Results.Text($"{AppInfo.DisplayName} {AppInfo.Version}\nStreamable HTTP endpoint: /mcp\nLegacy SSE endpoint: /sse\n", "text/plain"));
            _app.MapGet("/health", () => Results.Json(new { ok = true, version = AppInfo.Version }));

            _app.MapPost("/mcp", HandlePost);
            _app.MapPost("/mcp/{token}", HandlePost);
            _app.MapGet("/mcp", (HttpContext c) => Results.StatusCode(405));
            _app.MapGet("/mcp/{token}", (HttpContext c) => Results.StatusCode(405));
            _app.MapDelete("/mcp", () => Results.Ok());
            _app.MapDelete("/mcp/{token}", () => Results.Ok());

            _app.MapGet("/sse", HandleSse);
            _app.MapGet("/sse/{token}", HandleSse);
            _app.MapPost("/messages", HandleSseMessage);

            await _app.StartAsync(ct);
            Log.Info($"HTTP transport listening on http://{_opt.Host}:{_opt.Port} (token {(string.IsNullOrEmpty(_opt.Token) ? "none" : "required")})");
        }

        public async Task StopAsync()
        {
            if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); _app = null; }
        }

        private bool Authorized(HttpContext ctx)
        {
            // Block DNS-rebinding style calls from random web pages when no token is configured.
            var origin = ctx.Request.Headers.Origin.ToString();
            if (string.IsNullOrEmpty(_opt.Token))
            {
                if (!string.IsNullOrEmpty(origin) && !(origin.Contains("://localhost") || origin.Contains("://127.0.0.1")))
                    return false;
                return true;
            }
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && auth.Substring(7).Trim() == _opt.Token) return true;
            if (ctx.Request.Query["token"] == _opt.Token) return true;
            if (ctx.Request.RouteValues.TryGetValue("token", out var t) && (string)t == _opt.Token) return true;
            var path = ctx.Request.Path.Value ?? "";
            return path.EndsWith("/" + _opt.Token, StringComparison.Ordinal);
        }

        private async Task HandlePost(HttpContext ctx)
        {
            string body;
            using (var r = new StreamReader(ctx.Request.Body, Encoding.UTF8)) body = await r.ReadToEndAsync();
            JsonNode msg;
            try { msg = JsonNode.Parse(body); }
            catch
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = (object)null, error = new { code = -32700, message = "Parse error" } });
                return;
            }
            var isInit = msg is JsonObject o && (string)o["method"] == "initialize";
            var resp = await _server.HandleMessageAsync(msg, new NoPeer());
            if (isInit) ctx.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
            if (resp == null) { ctx.Response.StatusCode = 202; return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(resp.ToJsonString(Json.Compact));
        }

        private async Task HandleSse(HttpContext ctx)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new SseSession();
            _sse[id] = session;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var tokenPart = string.IsNullOrEmpty(_opt.Token) ? "" : "&token=" + Uri.EscapeDataString(_opt.Token);
            await ctx.Response.WriteAsync($"event: endpoint\ndata: /messages?sessionId={id}{tokenPart}\n\n");
            await ctx.Response.Body.FlushAsync();
            var ct = ctx.RequestAborted;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var readTask = session.Out.Reader.WaitToReadAsync(ct).AsTask();
                    var done = await Task.WhenAny(readTask, Task.Delay(15000, ct));
                    if (done != readTask) { await ctx.Response.WriteAsync(": keepalive\n\n", ct); await ctx.Response.Body.FlushAsync(ct); continue; }
                    if (!await readTask) break;
                    while (session.Out.Reader.TryRead(out var m))
                        await ctx.Response.WriteAsync($"event: message\ndata: {m}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { _sse.TryRemove(id, out _); }
        }

        private async Task HandleSseMessage(HttpContext ctx)
        {
            var id = ctx.Request.Query["sessionId"].ToString();
            if (!_sse.TryGetValue(id, out var session)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Unknown session"); return; }
            string body;
            using (var r = new StreamReader(ctx.Request.Body, Encoding.UTF8)) body = await r.ReadToEndAsync();
            JsonNode msg;
            try { msg = JsonNode.Parse(body); } catch { ctx.Response.StatusCode = 400; return; }
            ctx.Response.StatusCode = 202;
            await ctx.Response.WriteAsync("Accepted");
            _ = Task.Run(async () =>
            {
                var resp = await _server.HandleMessageAsync(msg, session);
                if (resp != null) session.Out.Writer.TryWrite(resp.ToJsonString(Json.Compact));
            });
        }
    }
}
