using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class MapTools
    {
        [Tool("list_maps")]
        public static async Task<ToolResult> ListMaps(Args a)
        {
            H.RequireProject();
            var activeName = MapView.Active?.Map?.Name;
            var arr = await Mct(() => J.Arr(Project.Current.GetItems<MapProjectItem>(), mi =>
            {
                var m = mi.GetMap();
                return new JsonObject
                {
                    ["name"] = m.Name,
                    ["uri"] = m.URI,
                    ["mapType"] = m.MapType.ToString(),
                    ["isActive"] = m.Name == activeName,
                    ["spatialReference"] = H.SrJson(m.SpatialReference),
                    ["layers"] = m.GetLayersAsFlattenedList().Count,
                    ["tables"] = m.GetStandaloneTablesAsFlattenedList().Count
                };
            }));
            return ToolResult.Of(new JsonObject { ["maps"] = arr });
        }

        private static Basemap ParseBasemap(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Basemap.None;
            var key = s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            foreach (Basemap b in Enum.GetValues(typeof(Basemap)))
                if (b.ToString().ToLowerInvariant() == key) return b;
            if (key.Contains("dark")) return Basemap.DarkGray;
            if (key.Contains("gray") || key.Contains("grey")) return Basemap.Gray;
            if (key.Contains("imag") || key.Contains("sat")) return Basemap.Satellite;
            if (key.Contains("street")) return Basemap.Streets;
            if (key.Contains("topo")) return Basemap.Topographic;
            if (key.Contains("ocean")) return Basemap.Oceans;
            if (key.Contains("osm") || key.Contains("openstreet")) return Basemap.OpenStreetMap;
            throw new ToolException($"Unknown basemap '{s}'. Options: {string.Join(", ", Enum.GetNames(typeof(Basemap)))}");
        }

        [Tool("create_map")]
        public static async Task<ToolResult> CreateMap(Args a)
        {
            H.RequireProject();
            var name = a.Str("name") ?? "Map";
            var kind = (a.Str("type") ?? "map").ToLowerInvariant();
            var mode = kind.Contains("global") ? MapViewingMode.SceneGlobal : kind.Contains("local") ? MapViewingMode.SceneLocal : MapViewingMode.Map;
            var mapType = mode == MapViewingMode.Map ? MapType.Map : MapType.Scene;
            var basemap = ParseBasemap(a.Str("basemap") ?? "Topographic");
            var map = await Mct(() =>
            {
                var m = MapFactory.Instance.CreateMap(name, mapType, mode, basemap);
                var wkid = a.Dbl("wkid");
                if (wkid != null) m.SetSpatialReference(SpatialReferenceBuilder.CreateSpatialReference((int)wkid));
                return m;
            });
            if (a.Bool("open", true))
                await Ui(async () => { await FrameworkApplication.Panes.CreateMapPaneAsync(map, mode); return true; });
            return ToolResult.Ok($"Created map '{map.Name}' ({mode}).");
        }

        [Tool("open_map")]
        public static async Task<ToolResult> OpenMap(Args a)
        {
            var map = await Mct(() => H.GetMap(a, "name"));
            var activated = await Ui(async () =>
            {
                foreach (var pane in FrameworkApplication.Panes)
                {
                    if (pane is IMapPane mp && mp.MapView?.Map?.URI == map.URI)
                    {
                        (pane as ArcGIS.Desktop.Framework.Contracts.Pane)?.Activate();
                        return "activated existing view";
                    }
                }
                await FrameworkApplication.Panes.CreateMapPaneAsync(map);
                return "opened new view";
            });
            return ToolResult.Ok($"Map '{map.Name}': {activated}.");
        }

        [Tool("map_properties")]
        public static async Task<ToolResult> MapProps(Args a)
        {
            var msg = await Mct(() =>
            {
                var map = H.GetMap(a);
                var done = new System.Collections.Generic.List<string>();
                var rename = a.Str("new_name");
                if (!string.IsNullOrWhiteSpace(rename)) { map.SetName(rename); done.Add("renamed to " + rename); }
                var wkid = a.Dbl("wkid");
                if (wkid != null) { map.SetSpatialReference(SpatialReferenceBuilder.CreateSpatialReference((int)wkid)); done.Add("spatial reference " + wkid); }
                var bm = a.Str("basemap");
                if (!string.IsNullOrWhiteSpace(bm)) { map.SetBasemapLayers(ParseBasemap(bm)); done.Add("basemap " + bm); }
                return done.Count == 0 ? "Nothing changed (pass new_name, wkid or basemap)." : $"Map '{map.Name}': " + string.Join(", ", done);
            });
            return ToolResult.Ok(msg);
        }

        [Tool("zoom_to")]
        public static async Task<ToolResult> ZoomTo(Args a)
        {
            var mv = H.ActiveMapView();
            var target = (a.Str("target") ?? "").ToLowerInvariant();
            var dur = TimeSpan.Zero;
            bool ok = true;
            if (a.Has("layer"))
            {
                var lyr = await Mct(() => H.FindLayer(mv.Map, a.Str("layer")));
                ok = await mv.ZoomToAsync(lyr, a.Bool("selected_only", false), dur);
            }
            else if (a.Has("extent"))
            {
                var e = a.Obj("extent");
                var env = await Mct(() =>
                {
                    var sr = H.Sr(e["wkid"] != null ? (int?)(int)(double)e["wkid"] : null, SpatialReferences.WGS84);
                    return (Geometry)EnvelopeBuilderEx.CreateEnvelope((double)e["xmin"], (double)e["ymin"], (double)e["xmax"], (double)e["ymax"], sr);
                });
                ok = await mv.ZoomToAsync(env, dur);
            }
            else if (a.Has("x") && a.Has("y"))
            {
                await Mct(() =>
                {
                    var sr = H.Sr((int?)a.Dbl("wkid"), SpatialReferences.WGS84);
                    var pt = MapPointBuilderEx.CreateMapPoint(a.Dbl("x", 0), a.Dbl("y", 0), sr);
                    var proj = (MapPoint)GeometryEngine.Instance.Project(pt, mv.Map.SpatialReference);
                    var cam = mv.Camera;
                    cam.X = proj.X; cam.Y = proj.Y;
                    var scale = a.Dbl("scale");
                    if (scale != null) cam.Scale = scale.Value;
                    mv.ZoomTo(cam, dur);
                });
            }
            else if (a.Has("scale"))
            {
                await Mct(() => { var cam = mv.Camera; cam.Scale = a.Dbl("scale", cam.Scale); mv.ZoomTo(cam, dur); });
            }
            else if (a.Has("bookmark"))
            {
                var name = a.Str("bookmark");
                await Mct(() =>
                {
                    var bm = mv.Map.GetBookmarks().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ToolException("Bookmark not found: " + name);
                    mv.ZoomTo(bm, dur);
                });
            }
            else
            {
                switch (target)
                {
                    case "selection": case "selected": ok = await mv.ZoomToSelectedAsync(dur); break;
                    case "in": ok = await mv.ZoomInFixedAsync(dur); break;
                    case "out": ok = await mv.ZoomOutFixedAsync(dur); break;
                    case "previous": case "back": ok = await mv.PreviousCameraAsync(dur); break;
                    default: ok = await mv.ZoomToFullExtentAsync(dur); break;
                }
            }
            var ext = await Mct(() => new JsonObject { ["ok"] = ok, ["extent"] = H.Extent(mv.Extent), ["scale"] = mv.Camera.Scale });
            return ToolResult.Of(ext);
        }

        [Tool("bookmarks")]
        public static async Task<ToolResult> Bookmarks(Args a)
        {
            var action = (a.Str("action") ?? "list").ToLowerInvariant();
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                if (action == "create")
                {
                    var mv = H.ActiveMapView();
                    var bm = mv.Map.AddBookmark(mv, a.Req("name"));
                    return (JsonNode)new JsonObject { ["created"] = bm?.Name };
                }
                if (action == "delete")
                {
                    var name = a.Req("name");
                    var bm = map.GetBookmarks().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ToolException("Bookmark not found: " + name);
                    map.RemoveBookmark(bm);
                    return new JsonObject { ["deleted"] = name };
                }
                return new JsonObject { ["bookmarks"] = J.Arr(map.GetBookmarks(), b => JsonValue.Create(b.Name)) };
            });
            return ToolResult.Of(res);
        }

        [Tool("capture_map_view")]
        public static async Task<ToolResult> Capture(Args a)
        {
            var mv = H.ActiveMapView();
            var width = a.Int("width", 1280);
            var height = a.Int("height", 800);
            var path = a.Str("output_path");
            var keep = !string.IsNullOrWhiteSpace(path);
            if (!keep) path = Path.Combine(Path.GetTempPath(), "arcgispro_mcp_" + Guid.NewGuid().ToString("N") + ".png");
            var fmt = (Path.GetExtension(path) ?? ".png").ToLowerInvariant();
            await Mct(() =>
            {
                ExportFormat ef = fmt switch
                {
                    ".jpg" or ".jpeg" => new JPEGFormat(),
                    ".pdf" => new PDFFormat(),
                    ".tif" or ".tiff" => new TIFFFormat(),
                    ".svg" => new SVGFormat(),
                    _ => new PNGFormat()
                };
                ef.OutputFileName = path;
                ef.Width = width;
                ef.Height = height;
                ef.Resolution = a.Int("dpi", 96);
                if (!ef.ValidateOutputFilePath()) throw new ToolException("Invalid output path: " + path);
                mv.Export(ef);
            });
            if (!File.Exists(path)) throw new ToolException("Export produced no file.");
            var result = ToolResult.Of(new JsonObject
            {
                ["map"] = mv.Map.Name,
                ["width"] = width,
                ["height"] = height,
                ["savedTo"] = keep ? path : null
            });
            if (a.Bool("return_image", true) && (fmt == ".png" || fmt == ".jpg" || fmt == ".jpeg"))
                result.Images.Add(new ImagePart { Base64 = Convert.ToBase64String(File.ReadAllBytes(path)), MimeType = fmt == ".png" ? "image/png" : "image/jpeg" });
            if (!keep) try { File.Delete(path); } catch { }
            return result;
        }

        [Tool("undo_redo")]
        public static async Task<ToolResult> UndoRedo(Args a)
        {
            var action = (a.Str("action") ?? "undo").ToLowerInvariant();
            var count = Math.Max(1, a.Int("count", 1));
            var mv = H.ActiveMapView();
            var om = mv.Map.OperationManager;
            for (int i = 0; i < count; i++)
            {
                if (action == "redo") { if (!om.CanRedo) break; await om.RedoAsync(); }
                else { if (!om.CanUndo) break; await om.UndoAsync(); }
            }
            return ToolResult.Of(new JsonObject { ["ok"] = true, ["canUndo"] = om.CanUndo, ["canRedo"] = om.CanRedo });
        }
    }
}
