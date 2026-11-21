using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using ArcGIS.Desktop.Framework.Threading.Tasks;

namespace ArcGISProMcpBridge.Bridge
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ToolAttribute : Attribute
    {
        public string Name { get; }
        public ToolAttribute(string name) { Name = name; }
    }

    /// <summary>Thrown for user-facing errors (bad args, missing layer ...).</summary>
    internal sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    internal sealed class ImagePart
    {
        public string Base64 { get; set; }
        public string MimeType { get; set; } = "image/png";
    }

    internal sealed class ToolResult
    {
        public JsonNode Data { get; set; }
        public List<ImagePart> Images { get; } = new List<ImagePart>();
        public static ToolResult Of(JsonNode data) => new ToolResult { Data = data };
        public static ToolResult Ok(string message) => new ToolResult { Data = new JsonObject { ["ok"] = true, ["message"] = message } };
    }

    internal static class Dispatcher
    {
        private static readonly Dictionary<string, Func<Args, Task<ToolResult>>> _tools = Discover();

        public static IEnumerable<string> ToolNames => _tools.Keys.OrderBy(k => k);

        private static Dictionary<string, Func<Args, Task<ToolResult>>> Discover()
        {
            var d = new Dictionary<string, Func<Args, Task<ToolResult>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in typeof(Dispatcher).Assembly.GetTypes())
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = m.GetCustomAttribute<ToolAttribute>();
                    if (attr == null) continue;
                    d[attr.Name] = (Func<Args, Task<ToolResult>>)Delegate.CreateDelegate(typeof(Func<Args, Task<ToolResult>>), m);
                }
            return d;
        }

        public static async Task<string> HandleAsync(string line)
        {
            JsonNode id = null;
            string method = null;
            try
            {
                var req = JsonNode.Parse(line)?.AsObject() ?? throw new ToolException("Empty request");
                id = req["id"]?.Copy();
                method = req["method"]?.GetValue<string>() ?? throw new ToolException("Missing 'method'");
                BridgeHost.NoteRequest(method);
                var p = req["params"] as JsonObject ?? new JsonObject();
                if (!_tools.TryGetValue(method, out var fn))
                    throw new ToolException($"Unknown bridge method '{method}'. This add-in version supports: {string.Join(", ", ToolNames)}");

                var result = await fn(new Args(p)).ConfigureAwait(false);
                var resp = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result.Data };
                if (result.Images.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var img in result.Images) arr.Add(new JsonObject { ["data"] = img.Base64, ["mimeType"] = img.MimeType });
                    resp["images"] = arr;
                }
                return resp.ToJsonString();
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
                if (inner is TargetInvocationException tie && tie.InnerException != null) inner = tie.InnerException;
                if (!(inner is ToolException)) Log.Error("Tool " + method, inner);
                var msg = inner is ToolException ? inner.Message : inner.GetType().Name + ": " + inner.Message;
                return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = msg }.ToJsonString();
            }
        }

        [Tool("bridge_ping")]
        public static Task<ToolResult> Ping(Args a) => Task.FromResult(ToolResult.Of(new JsonObject
        {
            ["pong"] = true,
            ["protocol"] = BridgeHost.ProtocolVersion,
            ["addinVersion"] = typeof(Dispatcher).Assembly.GetName().Version?.ToString(),
            ["tools"] = new JsonArray(ToolNames.Select(n => (JsonNode)JsonValue.Create(n)).ToArray())
        }));
    }

    /// <summary>Typed accessor over the JSON params object.</summary>
    internal sealed class Args
    {
        public JsonObject Raw { get; }
        public Args(JsonObject raw) { Raw = raw; }

        public bool Has(string name) => Raw.TryGetPropertyValue(name, out var v) && v != null;

        public string Str(string name, string def = null)
        {
            if (!Raw.TryGetPropertyValue(name, out var v) || v == null) return def;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<string>(out var s)) return s;
                return jv.ToJsonString();
            }
            return v.ToJsonString();
        }

        public string Req(string name)
        {
            var s = Str(name);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolException($"Parameter '{name}' is required.");
            return s;
        }

        public double? Dbl(string name)
        {
            if (!Raw.TryGetPropertyValue(name, out var v) || v == null) return null;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out var d)) return d;
                if (jv.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            throw new ToolException($"Parameter '{name}' must be a number.");
        }

        public double Dbl(string name, double def) => Dbl(name) ?? def;
        public int Int(string name, int def) => (int)(Dbl(name) ?? def);

        public bool? Bool(string name)
        {
            if (!Raw.TryGetPropertyValue(name, out var v) || v == null) return null;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var b)) return b;
                if (jv.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
                if (jv.TryGetValue<double>(out var d)) return d != 0;
            }
            return null;
        }

        public bool Bool(string name, bool def) => Bool(name) ?? def;

        public JsonArray Arr(string name)
        {
            if (!Raw.TryGetPropertyValue(name, out var v) || v == null) return null;
            if (v is JsonArray a) return a;
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                s = s.Trim();
                if (s.StartsWith("[")) { try { return JsonNode.Parse(s) as JsonArray; } catch { } }
                return new JsonArray(s.Split(',').Select(x => (JsonNode)JsonValue.Create(x.Trim())).ToArray());
            }
            return new JsonArray(v.Copy());
        }

        public JsonObject Obj(string name)
        {
            if (!Raw.TryGetPropertyValue(name, out var v) || v == null) return null;
            if (v is JsonObject o) return o;
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.TrimStart().StartsWith("{"))
            {
                try { return JsonNode.Parse(s) as JsonObject; } catch { }
            }
            throw new ToolException($"Parameter '{name}' must be a JSON object.");
        }

        public List<string> StrList(string name)
        {
            var a = Arr(name);
            if (a == null) return null;
            return a.Where(x => x != null).Select(x => x is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : x.ToJsonString()).ToList();
        }
    }

    internal static class Threading
    {
        /// <summary>Run on the ArcGIS Pro Main CIM Thread (required for most SDK calls).</summary>
        public static Task<T> Mct<T>(Func<T> fn) => QueuedTask.Run(fn);
        public static Task Mct(Action fn) => QueuedTask.Run(fn);

        /// <summary>Run on the WPF UI thread (panes, commands, dialogs).</summary>
        public static async Task<T> Ui<T>(Func<Task<T>> fn)
        {
            var disp = Application.Current.Dispatcher;
            if (disp.CheckAccess()) return await fn();
            var op = disp.InvokeAsync(fn);
            var task = await op.Task.ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        public static Task<T> Ui<T>(Func<T> fn) => Ui(() => Task.FromResult(fn()));
    }

    internal static class J
    {
        /// <summary>Deep copy (JsonNode.DeepClone is .NET 8+, we target .NET 6 for Pro 3.0).</summary>
        public static JsonNode Copy(this JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());

        public static JsonNode Val(object v)
        {
            switch (v)
            {
                case null: return null;
                case DBNull _: return null;
                case string s: return JsonValue.Create(s);
                case bool b: return JsonValue.Create(b);
                case int i: return JsonValue.Create(i);
                case long l: return JsonValue.Create(l);
                case short sh: return JsonValue.Create((int)sh);
                case byte by: return JsonValue.Create((int)by);
                case float f: return double.IsFinite(f) ? JsonValue.Create((double)f) : null;
                case double d: return double.IsFinite(d) ? JsonValue.Create(d) : null;
                case decimal m: return JsonValue.Create(m);
                case DateTime dt: return JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto: return JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture));
                case Guid g: return JsonValue.Create(g.ToString("B").ToUpperInvariant());
                case byte[] bytes: return JsonValue.Create($"<blob {bytes.Length} bytes>");
                case JsonNode n: return n.Copy();
                default: return JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
        }

        public static JsonArray Arr<T>(IEnumerable<T> items, Func<T, JsonNode> map)
        {
            var a = new JsonArray();
            foreach (var i in items) a.Add(map(i));
            return a;
        }

        public static object ToClr(JsonNode n)
        {
            if (n == null) return null;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
            return n.ToJsonString();
        }

        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions { WriteIndented = true };
    }
}
