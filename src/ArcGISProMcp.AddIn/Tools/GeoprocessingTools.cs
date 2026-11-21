using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGISProMcpBridge.Bridge;

namespace ArcGISProMcpBridge.Tools
{
    internal static class GeoprocessingTools
    {
        public static JsonObject ResultJson(string tool, IGPResult r)
        {
            return new JsonObject
            {
                ["tool"] = tool,
                ["succeeded"] = !r.IsFailed,
                ["returnValue"] = r.ReturnValue,
                ["outputs"] = J.Arr(r.Values ?? Enumerable.Empty<string>(), v => JsonValue.Create(v)),
                ["messages"] = J.Arr(r.Messages ?? Enumerable.Empty<IGPMessage>(), m => JsonValue.Create(MsgType(m.Type) + m.Text)),
                ["errorCode"] = r.ErrorCode
            };
        }

        private static string MsgType(GPMessageType t) => t switch { GPMessageType.Warning => "WARNING: ", GPMessageType.Error => "ERROR: ", GPMessageType.Abort => "ABORT: ", _ => "" };

        [Tool("run_geoprocessing")]
        public static async Task<ToolResult> Run(Args a)
        {
            var tool = a.Req("tool");
            var values = Geoprocessing.MakeValueArray(H.ParamValues(a.Arr("parameters")).ToArray());
            var envObj = a.Obj("environments");
            List<KeyValuePair<string, string>> env = null;
            if (envObj != null)
            {
                env = new List<KeyValuePair<string, string>>();
                foreach (var kv in envObj)
                {
                    var v = J.ToClr(kv.Value);
                    env.Add(new KeyValuePair<string, string>(kv.Key, v is bool b ? (b ? "true" : "false") : Convert.ToString(v, CultureInfo.InvariantCulture)));
                }
            }
            var flags = GPExecuteToolFlags.AddToHistory | GPExecuteToolFlags.RefreshProjectItems;
            if (a.Bool("add_outputs_to_map", true)) flags |= GPExecuteToolFlags.AddOutputsToMap;
            var r = await Geoprocessing.ExecuteToolAsync(tool, values, env, null, null, flags);
            var json = ResultJson(tool, r);
            if (r.IsFailed && (r.Messages == null || !r.Messages.Any()))
                json["hint"] = "Tool failed without messages: check the tool name (e.g. 'analysis.Buffer' or 'Buffer_analysis') and parameter order with get_geoprocessing_tool_info.";
            return ToolResult.Of(json);
        }

        [Tool("list_geoprocessing_tools")]
        public static async Task<ToolResult> ListTools(Args a)
        {
            var wildcard = (a.Str("filter") ?? "*").Replace("\"", "");
            if (!wildcard.Contains("*")) wildcard = "*" + wildcard + "*";
            var code = $@"
import arcpy
_w = {PyStr(wildcard)}
_tools = sorted(set(arcpy.ListTools(_w)))
result = {{'count': len(_tools), 'tools': _tools[:{a.Int("limit", 400)}], 'toolboxes': sorted(arcpy.ListToolboxes())}}
";
            return await PythonRunner.RunAsResult(code);
        }

        [Tool("get_geoprocessing_tool_info")]
        public static async Task<ToolResult> ToolInfo(Args a)
        {
            var tool = a.Req("tool");
            var code = $@"
import arcpy
_t = {PyStr(tool)}
_params = []
try:
    for p in arcpy.GetParameterInfo(_t):
        _f = None
        try:
            _f = list(p.filter.list) if p.filter and p.filter.list else None
        except Exception:
            pass
        _params.append({{'name': p.name, 'label': p.displayName, 'datatype': p.datatype, 'required': p.parameterType,
                        'direction': p.direction, 'multiValue': p.multiValue, 'default': str(p.value) if p.value is not None else None,
                        'filterValues': _f}})
except Exception as _e:
    _params = 'GetParameterInfo failed: %s' % _e
result = {{'tool': _t, 'usage': arcpy.Usage(_t), 'parameters': _params}}
";
            return await PythonRunner.RunAsResult(code);
        }

        public static string PyStr(string s) => "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "") + "'";
    }

    internal static class PythonRunner
    {
        public static string ToolboxPath => Path.Combine(BridgeHost.DataDir, "python", "McpRunner.pyt");

        public static void EnsureToolbox()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ToolboxPath));
            using var s = typeof(PythonRunner).Assembly.GetManifestResourceStream("McpRunner.pyt");
            using var sr = new StreamReader(s, Encoding.UTF8);
            var content = sr.ReadToEnd();
            if (!File.Exists(ToolboxPath) || File.ReadAllText(ToolboxPath) != content)
                File.WriteAllText(ToolboxPath, content, new UTF8Encoding(false));
        }

        public static async Task<JsonObject> RunAsync(string code, bool reset = false)
        {
            EnsureToolbox();
            var outFile = Path.Combine(Path.GetTempPath(), "arcgispro_mcp_py_" + Guid.NewGuid().ToString("N") + ".json");
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
            var vals = Geoprocessing.MakeValueArray(b64, outFile, reset ? "true" : "false");
            var r = await Geoprocessing.ExecuteToolAsync(ToolboxPath + "\\RunCode", vals, null, null, null, GPExecuteToolFlags.None);
            JsonObject payload;
            if (File.Exists(outFile))
            {
                payload = JsonNode.Parse(File.ReadAllText(outFile, Encoding.UTF8)) as JsonObject ?? new JsonObject();
                try { File.Delete(outFile); } catch { }
            }
            else
            {
                payload = new JsonObject { ["ok"] = false, ["error"] = "Python runner produced no output." };
            }
            var msgs = r.Messages?.Where(m => m.Type == GPMessageType.Warning || m.Type == GPMessageType.Error || m.Type == GPMessageType.Informative).Select(m => m.Text)
                .Where(t => !t.StartsWith("Start Time") && !t.StartsWith("Succeeded at") && !t.StartsWith("Failed at") && !t.StartsWith("Running script")).ToList();
            if (msgs != null && msgs.Count > 0) payload["gpMessages"] = J.Arr(msgs, m => JsonValue.Create(m));
            return payload;
        }

        public static async Task<ToolResult> RunAsResult(string code)
        {
            var p = await RunAsync(code);
            if (p["ok"]?.GetValue<bool>() == false) throw new ToolException("Python error:\n" + p["error"] + "\n" + p["stdout"]);
            return ToolResult.Of(p["result"]?.Copy() ?? p);
        }

        [Tool("run_python")]
        public static async Task<ToolResult> RunPython(Args a)
        {
            var payload = await RunAsync(a.Req("code"), a.Bool("reset_session", false));
            var r = ToolResult.Of(payload);
            var img = a.Str("return_image_path");
            if (!string.IsNullOrWhiteSpace(img) && File.Exists(img))
                r.Images.Add(new ImagePart { Base64 = Convert.ToBase64String(File.ReadAllBytes(img)), MimeType = img.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg" });
            return r;
        }
    }
}
