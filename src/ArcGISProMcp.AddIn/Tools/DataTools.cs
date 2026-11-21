using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class DataTools
    {
        private static QueryFilter Filter(Args a)
        {
            var qf = new QueryFilter { WhereClause = a.Str("where") ?? "" };
            var fields = a.StrList("fields");
            if (fields != null && fields.Count > 0 && !(fields.Count == 1 && fields[0] == "*")) qf.SubFields = string.Join(",", fields);
            var order = a.Str("order_by");
            if (!string.IsNullOrWhiteSpace(order)) qf.PostfixClause = "ORDER BY " + order;
            var oids = a.Arr("object_ids");
            if (oids != null && oids.Count > 0) qf.ObjectIDs = oids.Select(x => Convert.ToInt64(J.ToClr(x), CultureInfo.InvariantCulture)).ToList();
            return qf;
        }

        [Tool("query_features")]
        public static async Task<ToolResult> Query(Args a)
        {
            var max = Math.Min(a.Int("max_records", 50), 5000);
            var withGeom = a.Bool("return_geometry", false);
            var geomFormat = (a.Str("geometry_format") ?? "wkt").ToLowerInvariant();
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var dt = H.FindDisplayTable(map, a.Req("layer"));
                var qf = Filter(a);
                var outSr = a.Dbl("out_wkid") is double w ? SpatialReferenceBuilder.CreateSpatialReference((int)w) : null;
                var rows = new JsonArray();
                long total = 0;
                RowCursor cursor;
                if (a.Bool("selected_only", false))
                {
                    var sel = dt.GetSelection();
                    cursor = sel.Search(qf, false);
                }
                else cursor = dt.Search(qf);
                using (cursor)
                {
                    while (cursor.MoveNext())
                    {
                        total++;
                        if (rows.Count >= max) { if (total > max + 100000) break; continue; }
                        using var row = cursor.Current;
                        var o = new JsonObject();
                        var flds = row.GetFields();
                        for (int i = 0; i < flds.Count; i++)
                        {
                            var f = flds[i];
                            if (f.FieldType == FieldType.Geometry)
                            {
                                if (!withGeom) continue;
                                if (row is Feature feat)
                                {
                                    var g = feat.GetShape();
                                    if (g != null && outSr != null) g = GeometryEngine.Instance.Project(g, outSr);
                                    o["_geometry"] = g == null ? null : geomFormat == "json"
                                        ? JsonNode.Parse(g.ToJson())
                                        : JsonValue.Create(GeometryEngine.Instance.ExportToWKT(WktExportFlags.WktExportDefaults, g));
                                }
                                continue;
                            }
                            if (f.FieldType == FieldType.Blob || f.FieldType == FieldType.Raster) continue;
                            o[f.Name] = J.Val(row[i]);
                        }
                        rows.Add(o);
                    }
                }
                return new JsonObject
                {
                    ["layer"] = ((MapMember)dt).Name,
                    ["returned"] = rows.Count,
                    ["matched"] = total,
                    ["matchedIsLowerBound"] = total > max + 100000,
                    ["records"] = rows
                };
            });
            return ToolResult.Of(res);
        }

        [Tool("summarize_field")]
        public static async Task<ToolResult> Summarize(Args a)
        {
            var field = a.Req("field");
            var top = a.Int("top_values", 25);
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var dt = H.FindDisplayTable(map, a.Req("layer"));
                var qf = new QueryFilter { WhereClause = a.Str("where") ?? "", SubFields = field };
                long count = 0, nulls = 0, numeric = 0;
                double sum = 0, min = double.MaxValue, max = double.MinValue;
                var freq = new Dictionary<string, long>();
                using (var c = dt.Search(qf))
                {
                    int idx = -1;
                    while (c.MoveNext())
                    {
                        using var row = c.Current;
                        if (idx < 0) idx = row.FindField(field);
                        if (idx < 0) throw new ToolException($"Field '{field}' not found.");
                        count++;
                        var v = row[idx];
                        if (v == null || v is DBNull) { nulls++; continue; }
                        if (v is IConvertible && !(v is string) && !(v is DateTime))
                        {
                            try
                            {
                                var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                                numeric++; sum += d; if (d < min) min = d; if (d > max) max = d;
                            }
                            catch { }
                        }
                        var key = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                        if (freq.Count < 100000 || freq.ContainsKey(key)) freq[key] = freq.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
                }
                var o = new JsonObject
                {
                    ["field"] = field,
                    ["count"] = count,
                    ["nulls"] = nulls,
                    ["distinctValues"] = freq.Count
                };
                if (numeric > 0)
                {
                    o["min"] = min; o["max"] = max; o["sum"] = sum; o["mean"] = sum / numeric;
                }
                o["topValues"] = J.Arr(freq.OrderByDescending(k => k.Value).Take(top), kv => new JsonObject { ["value"] = kv.Key, ["count"] = kv.Value });
                return o;
            });
            return ToolResult.Of(res);
        }

        [Tool("select_features")]
        public static async Task<ToolResult> Select(Args a)
        {
            var mode = (a.Str("mode") ?? "new").ToLowerInvariant();
            var method = mode switch
            {
                "add" => SelectionCombinationMethod.Add,
                "remove" or "subtract" => SelectionCombinationMethod.Subtract,
                "subset" or "and" => SelectionCombinationMethod.And,
                "xor" => SelectionCombinationMethod.XOR,
                _ => SelectionCombinationMethod.New
            };
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var dt = H.FindDisplayTable(map, a.Req("layer"));
                QueryFilter qf = Filter(a);
                if (a.Has("geometry") && dt is BasicFeatureLayer bfl)
                {
                    var g = H.ParseGeometry(a.Raw["geometry"], H.Sr((int?)a.Dbl("wkid"), bfl.GetSpatialReference()));
                    qf = new SpatialQueryFilter
                    {
                        WhereClause = qf.WhereClause,
                        FilterGeometry = GeometryEngine.Instance.Project(g, bfl.GetSpatialReference()),
                        SpatialRelationship = SpatialRelationship.Intersects
                    };
                }
                using var sel = dt.Select(qf, method);
                long n; using (var s2 = dt.GetSelection()) n = s2?.GetCount() ?? 0;
                return new JsonObject { ["layer"] = ((MapMember)dt).Name, ["selectionCount"] = n };
            });
            return ToolResult.Of(res);
        }

        [Tool("select_by_location")]
        public static async Task<ToolResult> SelectByLocation(Args a)
        {
            var (target, source) = await Mct(() =>
            {
                var map = H.GetMap(a);
                return (H.FindLayer(map, a.Req("layer")), H.FindLayer(map, a.Req("selecting_layer")));
            });
            var rel = a.Str("relationship") ?? "INTERSECT";
            var dist = a.Str("search_distance") ?? "";
            var type = (a.Str("mode") ?? "NEW_SELECTION").ToUpperInvariant();
            if (type == "NEW") type = "NEW_SELECTION"; else if (type == "ADD") type = "ADD_TO_SELECTION";
            else if (type == "REMOVE") type = "REMOVE_FROM_SELECTION"; else if (type == "SUBSET") type = "SUBSET_SELECTION";
            var vals = Geoprocessing.MakeValueArray(target, rel, source, dist, type, a.Bool("invert", false) ? "INVERT" : "NOT_INVERT");
            var r = await Geoprocessing.ExecuteToolAsync("management.SelectLayerByLocation", vals, null, null, null, GPExecuteToolFlags.None);
            if (r.IsFailed) throw new ToolException("SelectLayerByLocation failed: " + string.Join(" | ", r.Messages.Select(m => m.Text)));
            var n = await Mct(() => (target as BasicFeatureLayer)?.SelectionCount ?? 0);
            return ToolResult.Of(new JsonObject { ["layer"] = target.Name, ["selectionCount"] = n });
        }

        [Tool("clear_selection")]
        public static async Task<ToolResult> ClearSel(Args a)
        {
            await Mct(() =>
            {
                var map = H.GetMap(a);
                if (a.Has("layer")) H.FindDisplayTable(map, a.Str("layer")).ClearSelection();
                else map.ClearSelection();
            });
            return ToolResult.Ok("Selection cleared.");
        }

        [Tool("get_selection")]
        public static async Task<ToolResult> GetSel(Args a)
        {
            var maxIds = a.Int("max_ids", 1000);
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var arr = new JsonArray();
                var members = a.Has("layer")
                    ? new List<MapMember> { H.FindMember(map, a.Str("layer")) }
                    : map.GetLayersAsFlattenedList().Cast<MapMember>().Concat(map.GetStandaloneTablesAsFlattenedList()).ToList();
                foreach (var m in members)
                {
                    if (!(m is IDisplayTable dt)) continue;
                    using var sel = dt.GetSelection();
                    var cnt = sel?.GetCount() ?? 0;
                    if (cnt == 0 && !a.Has("layer")) continue;
                    arr.Add(new JsonObject
                    {
                        ["layer"] = m.Name,
                        ["count"] = cnt,
                        ["objectIds"] = J.Arr(sel?.GetObjectIDs().Take(maxIds) ?? Enumerable.Empty<long>(), id => JsonValue.Create(id))
                    });
                }
                return new JsonObject { ["selections"] = arr };
            });
            return ToolResult.Of(res);
        }
    }
}
