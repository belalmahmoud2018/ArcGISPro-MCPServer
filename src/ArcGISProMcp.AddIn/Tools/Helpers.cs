using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;

namespace ArcGISProMcpBridge.Tools
{
    internal static class H
    {
        public static string ProVersion
        {
            get
            {
                try { return FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).ProductVersion; }
                catch { return "unknown"; }
            }
        }

        public static void RequireProject()
        {
            if (Project.Current == null) throw new ToolException("No project is open in ArcGIS Pro. Use open_project or new_project first.");
        }

        /// <summary>MCT only. Resolves the 'map' argument, else the active map, else the first map in the project.</summary>
        public static Map GetMap(Args a, string argName = "map")
        {
            RequireProject();
            var name = a.Str(argName);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var items = Project.Current.GetItems<MapProjectItem>().ToList();
                var item = items.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                           ?? items.FirstOrDefault(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (item == null) throw new ToolException($"Map '{name}' not found. Maps: {string.Join(", ", items.Select(i => i.Name))}");
                return item.GetMap();
            }
            var active = MapView.Active?.Map;
            if (active != null) return active;
            var first = Project.Current.GetItems<MapProjectItem>().FirstOrDefault();
            if (first == null) throw new ToolException("The project has no maps. Use create_map first.");
            return first.GetMap();
        }

        public static MapView ActiveMapView()
        {
            var mv = MapView.Active;
            if (mv == null) throw new ToolException("No active map view. Use open_map to open/activate a map view first (a layout or table pane may be active).");
            return mv;
        }

        /// <summary>MCT only. Finds a layer or standalone table by name (case-insensitive), URI, or partial name.</summary>
        public static MapMember FindMember(Map map, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ToolException("A layer/table name is required.");
            var layers = map.GetLayersAsFlattenedList().Cast<MapMember>();
            var tables = map.GetStandaloneTablesAsFlattenedList().Cast<MapMember>();
            var all = layers.Concat(tables).ToList();
            var hit = all.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? all.FirstOrDefault(m => string.Equals(m.URI, name, StringComparison.OrdinalIgnoreCase))
                      ?? all.FirstOrDefault(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit == null)
                throw new ToolException($"Layer/table '{name}' not found in map '{map.Name}'. Available: {string.Join(", ", all.Select(m => m.Name).Take(80))}");
            return hit;
        }

        public static Layer FindLayer(Map map, string name)
        {
            var m = FindMember(map, name);
            return m as Layer ?? throw new ToolException($"'{name}' is a standalone table, not a layer.");
        }

        public static FeatureLayer FindFeatureLayer(Map map, string name)
        {
            var m = FindMember(map, name);
            return m as FeatureLayer ?? throw new ToolException($"'{m.Name}' is not a feature layer (it is {m.GetType().Name}).");
        }

        public static IDisplayTable FindDisplayTable(Map map, string name)
        {
            var m = FindMember(map, name);
            return m as IDisplayTable ?? throw new ToolException($"'{m.Name}' ({m.GetType().Name}) has no attribute table.");
        }

        /// <summary>MCT only.</summary>
        public static Layout GetLayout(string name)
        {
            RequireProject();
            var items = Project.Current.GetItems<LayoutProjectItem>().ToList();
            if (string.IsNullOrWhiteSpace(name))
            {
                var active = LayoutView.Active?.Layout;
                if (active != null) return active;
                var f = items.FirstOrDefault() ?? throw new ToolException("The project has no layouts. Use create_layout first.");
                return f.GetLayout();
            }
            var item = items.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? items.FirstOrDefault(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (item == null) throw new ToolException($"Layout '{name}' not found. Layouts: {string.Join(", ", items.Select(i => i.Name))}");
            return item.GetLayout();
        }

        public static CIMColor Color(string s, CIMColor def = null)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            try
            {
                if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    var nums = s.Substring(s.IndexOf('(') + 1).TrimEnd(')').Split(',').Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    var alpha = nums.Length > 3 ? (nums[3] <= 1 ? nums[3] * 100 : nums[3] / 2.55) : 100;
                    return ColorFactory.Instance.CreateRGBColor(nums[0], nums[1], nums[2], alpha);
                }
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
                return ColorFactory.Instance.CreateRGBColor(c.R, c.G, c.B, c.A / 2.55);
            }
            catch
            {
                throw new ToolException($"Unrecognized color '{s}'. Use a name (red), hex (#FF8800 / #80FF8800) or rgb(255,136,0[,alpha]).");
            }
        }

        public static SpatialReference Sr(int? wkid, SpatialReference def = null)
        {
            if (wkid == null || wkid == 0) return def;
            return SpatialReferenceBuilder.CreateSpatialReference(wkid.Value);
        }

        /// <summary>Accepts Esri JSON geometry object, WKT string, or {x,y} shorthand. MCT not required.</summary>
        public static Geometry ParseGeometry(JsonNode g, SpatialReference defaultSr)
        {
            if (g == null) return null;
            Geometry geom;
            if (g is JsonValue v && v.TryGetValue<string>(out var s))
            {
                s = s.Trim();
                if (s.StartsWith("{")) return ParseGeometry(JsonNode.Parse(s), defaultSr);
                geom = GeometryEngine.Instance.ImportFromWKT(WktImportFlags.WktImportDefaults, s, defaultSr);
            }
            else if (g is JsonArray arr && arr.Count >= 2)
            {
                geom = MapPointBuilderEx.CreateMapPoint((double)arr[0], (double)arr[1], defaultSr);
            }
            else if (g is JsonObject o)
            {
                var clone = (JsonObject)o.Copy();
                if (!clone.ContainsKey("spatialReference") && defaultSr != null)
                    clone["spatialReference"] = new JsonObject { ["wkid"] = defaultSr.Wkid };
                geom = GeometryEngine.Instance.ImportFromJson(JsonImportFlags.JsonImportDefaults, clone.ToJsonString());
            }
            else throw new ToolException("Unsupported geometry format. Use Esri JSON ({\"x\":..,\"y\":..} / {\"paths\":..} / {\"rings\":..}) or WKT.");
            return geom;
        }

        public static JsonObject Extent(Envelope e)
        {
            if (e == null || e.IsEmpty) return null;
            return new JsonObject
            {
                ["xmin"] = e.XMin, ["ymin"] = e.YMin, ["xmax"] = e.XMax, ["ymax"] = e.YMax,
                ["wkid"] = e.SpatialReference?.Wkid
            };
        }

        public static JsonObject SrJson(SpatialReference sr)
        {
            if (sr == null) return null;
            return new JsonObject { ["wkid"] = sr.Wkid, ["name"] = sr.Name, ["isGeographic"] = sr.IsGeographic };
        }

        public static string LayerKind(MapMember m) => m switch
        {
            FeatureLayer fl => "FeatureLayer(" + fl.ShapeType.ToString().Replace("esriGeometry", "") + ")",
            StandaloneTable _ => "StandaloneTable",
            _ => m.GetType().Name
        };

        public static List<object> ParamValues(JsonArray arr)
        {
            var list = new List<object>();
            if (arr == null) return list;
            foreach (var n in arr)
            {
                if (n == null) { list.Add(""); continue; }
                if (n is JsonArray sub) { list.Add(sub.Select(x => x == null ? "" : (object)(J.ToClr(x)?.ToString() ?? "")).ToList()); continue; }
                var clr = J.ToClr(n);
                list.Add(clr is bool b ? (b ? "true" : "false") : Convert.ToString(clr, CultureInfo.InvariantCulture));
            }
            return list;
        }
    }
}
