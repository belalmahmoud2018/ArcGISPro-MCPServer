using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class EditTools
    {
        private static Dictionary<string, object> Attrs(JsonObject o)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (o == null) return d;
            foreach (var kv in o) d[kv.Key] = J.ToClr(kv.Value);
            return d;
        }

        private static List<long> ResolveOids(Args a, IDisplayTable dt)
        {
            var ids = a.Arr("object_ids");
            if (ids != null && ids.Count > 0) return ids.Select(x => Convert.ToInt64(J.ToClr(x), CultureInfo.InvariantCulture)).ToList();
            var where = a.Str("where");
            if (string.IsNullOrWhiteSpace(where))
            {
                if (a.Bool("selected_only", false)) { using var s = dt.GetSelection(); return s.GetObjectIDs().ToList(); }
                throw new ToolException("Provide object_ids, where, or selected_only=true.");
            }
            var list = new List<long>();
            using var c = dt.Search(new QueryFilter { WhereClause = where });
            while (c.MoveNext()) { using var r = c.Current; list.Add(r.GetObjectID()); }
            return list;
        }

        [Tool("add_features")]
        public static async Task<ToolResult> AddFeatures(Args a)
        {
            var feats = a.Arr("features") ?? throw new ToolException("'features' array is required.");
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var op = new EditOperation { Name = "MCP: add features", SelectNewFeatures = a.Bool("select_new", false) };
                var inSr = H.Sr((int?)a.Dbl("wkid"), m is Layer lyr ? lyr.GetSpatialReference() : null);
                string shapeField = null;
                SpatialReference layerSr = null;
                if (m is FeatureLayer fl)
                {
                    using var fc = fl.GetFeatureClass();
                    shapeField = fc.GetDefinition().GetShapeField();
                    layerSr = fc.GetDefinition().GetSpatialReference();
                }
                int n = 0;
                foreach (var f in feats.OfType<JsonObject>())
                {
                    var attrs = Attrs(f["attributes"] as JsonObject);
                    var gNode = f["geometry"];
                    if (gNode != null)
                    {
                        if (shapeField == null) throw new ToolException("This layer has no geometry; omit 'geometry'.");
                        var g = H.ParseGeometry(gNode, inSr ?? layerSr);
                        if (layerSr != null && g.SpatialReference != null && !g.SpatialReference.IsEqual(layerSr))
                            g = GeometryEngine.Instance.Project(g, layerSr);
                        attrs[shapeField] = g;
                    }
                    op.Create(m, attrs);
                    n++;
                }
                if (op.IsEmpty) return new JsonObject { ["created"] = 0 };
                var ok = op.Execute();
                if (!ok) throw new ToolException("Edit failed: " + op.ErrorMessage);
                return new JsonObject { ["created"] = n, ["layer"] = m.Name, ["note"] = "Call save_edits to persist." };
            });
            return ToolResult.Of(res);
        }

        [Tool("update_features")]
        public static async Task<ToolResult> UpdateFeatures(Args a)
        {
            var values = a.Obj("attributes");
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var dt = (IDisplayTable)m;
                var oids = ResolveOids(a, dt);
                if (oids.Count == 0) return new JsonObject { ["updated"] = 0 };
                var op = new EditOperation { Name = "MCP: update features" };
                var attrs = Attrs(values);
                if (a.Has("geometry") && m is FeatureLayer fl)
                {
                    using var fc = fl.GetFeatureClass();
                    var sr = fc.GetDefinition().GetSpatialReference();
                    var g = H.ParseGeometry(a.Raw["geometry"], H.Sr((int?)a.Dbl("wkid"), sr));
                    attrs[fc.GetDefinition().GetShapeField()] = GeometryEngine.Instance.Project(g, sr);
                }
                if (attrs.Count == 0) throw new ToolException("Nothing to update: pass 'attributes' and/or 'geometry'.");
                foreach (var oid in oids) op.Modify(m, oid, attrs);
                if (!op.Execute()) throw new ToolException("Edit failed: " + op.ErrorMessage);
                return new JsonObject { ["updated"] = oids.Count, ["layer"] = m.Name, ["note"] = "Call save_edits to persist." };
            });
            return ToolResult.Of(res);
        }

        [Tool("delete_features")]
        public static async Task<ToolResult> DeleteFeatures(Args a)
        {
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var m = H.FindMember(map, a.Req("layer"));
                var oids = ResolveOids(a, (IDisplayTable)m);
                if (oids.Count == 0) return new JsonObject { ["deleted"] = 0 };
                var op = new EditOperation { Name = "MCP: delete features" };
                op.Delete(m, oids);
                if (!op.Execute()) throw new ToolException("Edit failed: " + op.ErrorMessage);
                return new JsonObject { ["deleted"] = oids.Count, ["layer"] = m.Name, ["note"] = "Call save_edits to persist." };
            });
            return ToolResult.Of(res);
        }

        [Tool("save_edits")]
        public static async Task<ToolResult> SaveEdits(Args a)
        {
            H.RequireProject();
            if (a.Bool("discard", false))
            {
                var d = await Project.Current.DiscardEditsAsync();
                return ToolResult.Ok(d ? "Edits discarded." : "Nothing to discard.");
            }
            if (!Project.Current.HasEdits) return ToolResult.Ok("No pending edits.");
            var ok = await Project.Current.SaveEditsAsync();
            return ToolResult.Ok(ok ? "Edits saved." : "Saving edits failed.");
        }
    }
}
