using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class LayerTools
    {
        [Tool("list_layers")]
        public static async Task<ToolResult> ListLayers(Args a)
        {
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var layers = new JsonArray();
                foreach (var l in map.GetLayersAsFlattenedList())
                {
                    var o = new JsonObject
                    {
                        ["name"] = l.Name,
                        ["kind"] = H.LayerKind(l),
                        ["visible"] = l.IsVisible,
                        ["parent"] = (l.Parent as Layer)?.Name,
                        ["uri"] = l.URI
                    };
                    if (l is BasicFeatureLayer bfl)
                    {
                        o["selectionCount"] = bfl.SelectionCount;
                        o["definitionQuery"] = bfl.DefinitionQuery;
                        o["selectable"] = bfl.IsSelectable;
                        o["editable"] = bfl.IsEditable;
                    }
                    if (l is FeatureLayer fl) o["labelsOn"] = fl.IsLabelVisible;
                    if (a.Bool("include_source", true))
                    {
                        try { o["source"] = DataSource(l); } catch { }
                    }
                    layers.Add(o);
                }
                var tables = J.Arr(map.GetStandaloneTablesAsFlattenedList(), t => new JsonObject
                {
                    ["name"] = t.Name,
                    ["kind"] = "StandaloneTable",
                    ["selectionCount"] = t.SelectionCount,
                    ["definitionQuery"] = t.DefinitionQuery
                });
                return new JsonObject { ["map"] = map.Name, ["layers"] = layers, ["tables"] = tables };
            });
            return ToolResult.Of(res);
        }

        private static string DataSource(MapMember m)
        {
            var conn = m.GetDataConnection();
            switch (conn)
            {
                case CIMStandardDataConnection sdc: return sdc.WorkspaceConnectionString + " | " + sdc.Dataset;
                case CIMFeatureDatasetDataConnection fds: return fds.WorkspaceConnectionString + " | " + fds.Dataset;
                case CIMWMSServiceConnection _: return "WMS service";
                case CIMAGSServiceConnection ags: return ags.URL;
                case null: return null;
                default: return conn.GetType().Name;
            }
        }

        [Tool("add_layer")]
        public static async Task<ToolResult> AddLayer(Args a)
        {
            var path = a.Req("data_path");
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                Uri uri;
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) uri = new Uri(path);
                else uri = new Uri(Path.GetFullPath(path));
                var name = a.Str("name");
                var index = a.Int("index", 0);
                try
                {
                    var p = new LayerCreationParams(uri) { IsVisible = a.Bool("visible", true) };
                    if (!string.IsNullOrWhiteSpace(name)) p.Name = name;
                    p.MapMemberIndex = index;
                    var layer = LayerFactory.Instance.CreateLayer<Layer>(p, map);
                    if (layer == null) throw new Exception("null layer");
                    return new JsonObject { ["added"] = layer.Name, ["kind"] = H.LayerKind(layer) };
                }
                catch (Exception layerEx)
                {
                    try
                    {
                        var tp = new StandaloneTableCreationParams(uri);
                        if (!string.IsNullOrWhiteSpace(name)) tp.Name = name;
                        var t = StandaloneTableFactory.Instance.CreateStandaloneTable(tp, map);
                        if (t != null) return new JsonObject { ["added"] = t.Name, ["kind"] = "StandaloneTable" };
                    }
                    catch { }
                    throw new ToolException($"Could not add '{path}': {layerEx.Message}");
                }
            });
            return ToolResult.Of(res);
        }

        [Tool("remove_layer")]
        public static async Task<ToolResult> RemoveLayer(Args a)
        {
            var name = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                if (m is Layer l) map.RemoveLayer(l);
                else if (m is StandaloneTable t) map.RemoveStandaloneTable(t);
                return m.Name;
            });
            return ToolResult.Ok($"Removed '{name}'.");
        }

        [Tool("set_layer_properties")]
        public static async Task<ToolResult> SetProps(Args a)
        {
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var done = new List<string>();
                if (a.Has("new_name")) { m.SetName(a.Str("new_name")); done.Add("name"); }
                if (m is Layer l)
                {
                    if (a.Bool("visible") is bool v) { l.SetVisibility(v); done.Add("visible=" + v); }
                    if (a.Dbl("transparency") is double t) { l.SetTransparency(t); done.Add("transparency=" + t); }
                    if (a.Dbl("opacity") is double op) { l.SetTransparency(100 - op); done.Add("opacity=" + op); }
                    if (a.Dbl("min_scale") is double mn) { l.SetMinScale(mn); done.Add("minScale"); }
                    if (a.Dbl("max_scale") is double mx) { l.SetMaxScale(mx); done.Add("maxScale"); }
                    if (a.Bool("expanded") is bool ex) { l.SetExpanded(ex); done.Add("expanded"); }
                    if (a.Has("index")) { map.MoveLayer(l, a.Int("index", 0)); done.Add("moved to index " + a.Int("index", 0)); }
                }
                if (m is BasicFeatureLayer bfl)
                {
                    if (a.Has("definition_query")) { bfl.SetDefinitionQuery(a.Str("definition_query") ?? ""); done.Add("definitionQuery"); }
                    if (a.Bool("selectable") is bool s) { bfl.SetSelectable(s); done.Add("selectable=" + s); }
                    if (a.Bool("editable") is bool e) { bfl.SetEditable(e); done.Add("editable=" + e); }
                }
                else if (m is StandaloneTable st && a.Has("definition_query"))
                {
                    st.SetDefinitionQuery(a.Str("definition_query") ?? ""); done.Add("definitionQuery");
                }
                if (m is FeatureLayer fl && a.Bool("labels") is bool lb) { fl.SetLabelVisibility(lb); done.Add("labels=" + lb); }
                return new JsonObject { ["layer"] = m.Name, ["changed"] = J.Arr(done, d => JsonValue.Create(d)) };
            });
            return ToolResult.Of(res);
        }

        [Tool("describe_layer")]
        public static async Task<ToolResult> Describe(Args a)
        {
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var o = new JsonObject { ["name"] = m.Name, ["kind"] = H.LayerKind(m), ["uri"] = m.URI };
                try { o["source"] = DataSource(m); } catch { }
                if (m is Layer l)
                {
                    o["visible"] = l.IsVisible;
                    o["minScale"] = l.MinScale;
                    o["maxScale"] = l.MaxScale;
                    o["transparency"] = l.Transparency;
                    o["spatialReference"] = H.SrJson(l.GetSpatialReference());
                }
                if (m is BasicFeatureLayer bfl)
                {
                    o["definitionQuery"] = bfl.DefinitionQuery;
                    o["selectionCount"] = bfl.SelectionCount;
                    try { o["extent"] = H.Extent(bfl.QueryExtent()); } catch { }
                }
                if (m is FeatureLayer fl)
                {
                    o["geometryType"] = fl.ShapeType.ToString().Replace("esriGeometry", "");
                    try { o["renderer"] = fl.GetRenderer()?.GetType().Name; } catch { }
                    o["labelsOn"] = fl.IsLabelVisible;
                }
                if (m is IDisplayTable dt)
                {
                    using var table = dt.GetTable();
                    if (table != null)
                    {
                        var def = table.GetDefinition();
                        o["fields"] = J.Arr(def.GetFields(), f => new JsonObject
                        {
                            ["name"] = f.Name,
                            ["alias"] = f.AliasName,
                            ["type"] = f.FieldType.ToString(),
                            ["length"] = f.Length,
                            ["nullable"] = f.IsNullable,
                            ["domain"] = f.GetDomain()?.GetName()
                        });
                        o["objectIdField"] = def.GetObjectIDField();
                        try { o["featureCount"] = table.GetCount(); } catch { }
                        o["path"] = table.GetPath()?.ToString();
                    }
                }
                return o;
            });
            return ToolResult.Of(res);
        }

        [Tool("get_layer_cim")]
        public static async Task<ToolResult> GetCim(Args a)
        {
            var json = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var def = m is Layer l ? (CIMObject)l.GetDefinition() : ((StandaloneTable)m).GetDefinition();
                return def.ToJson(null);
            });
            var max = a.Int("max_chars", 200000);
            return ToolResult.Of(new JsonObject
            {
                ["cimJson"] = json.Length > max ? json.Substring(0, max) : json,
                ["truncated"] = json.Length > max,
                ["length"] = json.Length
            });
        }

        [Tool("set_layer_cim")]
        public static async Task<ToolResult> SetCim(Args a)
        {
            var cim = a.Req("cim_json");
            var name = await Mct(() =>
            {
                var map = H.GetMap(a);
                var l = H.FindLayer(map, a.Req("layer"));
                var current = l.GetDefinition();
                var def = CIMObject.FromJson(cim, null) as CIMBaseLayer
                          ?? throw new ToolException("cim_json did not deserialize to a CIM layer definition.");
                if (def.GetType() != current.GetType())
                    throw new ToolException($"CIM type mismatch: layer is {current.GetType().Name}, JSON is {def.GetType().Name}.");
                l.SetDefinition(def);
                return l.Name;
            });
            return ToolResult.Ok($"CIM definition applied to '{name}'.");
        }

        [Tool("open_attribute_table")]
        public static async Task<ToolResult> OpenTable(Args a)
        {
            var m = await Mct(() => H.FindMember(H.GetMap(a), a.Req("layer")));
            await Ui(() =>
            {
                if (!FrameworkApplication.Panes.CanOpenTablePane(m)) throw new ToolException("Cannot open a table for " + m.Name);
                FrameworkApplication.Panes.OpenTablePane(m, a.Bool("selected_only", false) ? TableViewMode.eSelectedRecords : TableViewMode.eAllRecords);
                return true;
            });
            return ToolResult.Ok("Opened attribute table of " + m.Name);
        }
    }
}
