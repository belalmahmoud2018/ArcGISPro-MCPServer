using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class LayoutTools
    {
        [Tool("list_layouts")]
        public static async Task<ToolResult> List(Args a)
        {
            H.RequireProject();
            var arr = await Mct(() => J.Arr(Project.Current.GetItems<LayoutProjectItem>(), li =>
            {
                var l = li.GetLayout();
                var page = l.GetPage();
                return new JsonObject
                {
                    ["name"] = l.Name,
                    ["width"] = page.Width,
                    ["height"] = page.Height,
                    ["units"] = page.Units?.Name,
                    ["elements"] = J.Arr(l.GetElements(), e => new JsonObject { ["name"] = e.Name, ["type"] = e.GetType().Name })
                };
            }));
            return ToolResult.Of(new JsonObject { ["layouts"] = arr });
        }

        private static LinearUnit Units(string s) => (s ?? "inches").ToLowerInvariant() switch
        {
            "cm" or "centimeters" => LinearUnit.Centimeters,
            "mm" or "millimeters" => LinearUnit.Millimeters,
            "points" or "pt" => LinearUnit.Points,
            _ => LinearUnit.Inches
        };

        [Tool("create_layout")]
        public static async Task<ToolResult> Create(Args a)
        {
            H.RequireProject();
            var units = Units(a.Str("units"));
            // LinearUnit instances are not reference-equal: compare by factory code.
            var inch = units.FactoryCode == LinearUnit.Inches.FactoryCode;
            var cm = units.FactoryCode == LinearUnit.Centimeters.FactoryCode;
            double w = a.Dbl("width", inch ? 11 : cm ? 29.7 : 297);
            double h = a.Dbl("height", inch ? 8.5 : cm ? 21 : 210);
            var layout = await Mct(() =>
            {
                var map = H.GetMap(a);
                var lyt = LayoutFactory.Instance.CreateLayout(w, h, units, true, inch ? 0.5 : 1);
                lyt.SetName(a.Str("name") ?? "Layout");
                var m = Math.Min(w, h) * 0.05;             // margin
                var titleH = a.Has("title") ? h * 0.1 : 0;
                var sideW = a.Bool("legend", true) ? w * 0.22 : 0;
                var mfEnv = EnvelopeBuilderEx.CreateEnvelope(m, m + h * 0.08, w - m - sideW, h - m - titleH);
                var mf = ElementFactory.Instance.CreateMapFrameElement(lyt, mfEnv, map, "Map Frame", false, null);

                if (a.Has("title"))
                {
                    var ts = SymbolFactory.Instance.ConstructTextSymbol(ColorFactory.Instance.BlackRGB, a.Dbl("title_size", 24), "Tahoma", "Bold");
                    ts.HorizontalAlignment = HorizontalAlignment.Center;
                    var pt = MapPointBuilderEx.CreateMapPoint(w / 2, h - m - titleH * 0.6);
                    ElementFactory.Instance.CreateTextGraphicElement(lyt, TextType.PointText, pt, ts, a.Str("title"), "Title", false, null);
                }
                if (a.Bool("legend", true))
                {
                    var env = EnvelopeBuilderEx.CreateEnvelope(w - m - sideW + m * 0.5, h * 0.35, w - m, h - m - titleH);
                    ElementFactory.Instance.CreateMapSurroundElement(lyt, env, new LegendInfo { MapFrameName = mf.Name }, "Legend", false, null);
                }
                if (a.Bool("north_arrow", true))
                {
                    var s = Math.Min(w, h) * 0.08;
                    var env = EnvelopeBuilderEx.CreateEnvelope(w - m - s, m, w - m, m + s * 1.4);
                    ElementFactory.Instance.CreateMapSurroundElement(lyt, env, new NorthArrowInfo { MapFrameName = mf.Name }, "North Arrow", false, null);
                }
                if (a.Bool("scale_bar", true))
                {
                    var env = EnvelopeBuilderEx.CreateEnvelope(m, m * 0.6, m + w * 0.35, m + h * 0.06);
                    ElementFactory.Instance.CreateMapSurroundElement(lyt, env, new ScaleBarInfo { MapFrameName = mf.Name }, "Scale Bar", false, null);
                }
                if (a.Has("subtitle"))
                {
                    var ts = SymbolFactory.Instance.ConstructTextSymbol(ColorFactory.Instance.GreyRGB, 10, "Tahoma", "Regular");
                    ElementFactory.Instance.CreateTextGraphicElement(lyt, TextType.PointText,
                        MapPointBuilderEx.CreateMapPoint(m + w * 0.4, m), ts, a.Str("subtitle"), "Subtitle", false, null);
                }
                return lyt;
            });
            if (a.Bool("open", true))
                await Ui(async () => { await FrameworkApplication.Panes.CreateLayoutPaneAsync(layout); return true; });
            return ToolResult.Ok($"Created layout '{layout.Name}' ({w}x{h} {units.Name}).");
        }

        [Tool("open_layout")]
        public static async Task<ToolResult> Open(Args a)
        {
            var layout = await Mct(() => H.GetLayout(a.Str("name")));
            await Ui(async () =>
            {
                foreach (var pane in FrameworkApplication.Panes)
                    if (pane is ILayoutPane lp && lp.LayoutView?.Layout?.URI == layout.URI)
                    { (pane as ArcGIS.Desktop.Framework.Contracts.Pane)?.Activate(); return true; }
                await FrameworkApplication.Panes.CreateLayoutPaneAsync(layout);
                return true;
            });
            return ToolResult.Ok("Layout opened: " + layout.Name);
        }

        [Tool("add_layout_text")]
        public static async Task<ToolResult> AddText(Args a)
        {
            var name = await Mct(() =>
            {
                var lyt = H.GetLayout(a.Str("layout"));
                var ts = SymbolFactory.Instance.ConstructTextSymbol(H.Color(a.Str("color"), ColorFactory.Instance.BlackRGB),
                    a.Dbl("font_size", 12), a.Str("font") ?? "Tahoma", a.Str("font_style") ?? "Regular");
                var pt = MapPointBuilderEx.CreateMapPoint(a.Dbl("x", 1), a.Dbl("y", 1));
                var el = ElementFactory.Instance.CreateTextGraphicElement(lyt, TextType.PointText, pt, ts, a.Req("text"), a.Str("element_name") ?? "Text", false, null);
                return el.Name;
            });
            return ToolResult.Ok("Added text element " + name);
        }

        [Tool("add_layout_picture")]
        public static async Task<ToolResult> AddPicture(Args a)
        {
            var path = a.Req("path");
            var name = await Mct(() =>
            {
                var lyt = H.GetLayout(a.Str("layout"));
                var env = EnvelopeBuilderEx.CreateEnvelope(a.Dbl("xmin", 1), a.Dbl("ymin", 1), a.Dbl("xmax", 3), a.Dbl("ymax", 3));
                var el = ElementFactory.Instance.CreatePictureGraphicElement(lyt, env, path, a.Str("element_name") ?? "Picture", false, null);
                return el.Name;
            });
            return ToolResult.Ok("Added picture element " + name);
        }

        [Tool("export_layout")]
        public static async Task<ToolResult> Export(Args a)
        {
            var path = a.Str("output_path");
            var returnImage = a.Bool("return_image", string.IsNullOrWhiteSpace(path));
            var temp = string.IsNullOrWhiteSpace(path);
            if (temp) path = Path.Combine(Path.GetTempPath(), "arcgispro_mcp_layout_" + Guid.NewGuid().ToString("N") + ".png");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var lname = await Mct(() =>
            {
                var lyt = H.GetLayout(a.Str("layout"));
                ExportFormat f = ext switch
                {
                    ".pdf" => new PDFFormat(),
                    ".jpg" or ".jpeg" => new JPEGFormat(),
                    ".tif" or ".tiff" => new TIFFFormat(),
                    ".svg" => new SVGFormat(),
                    ".eps" => new EPSFormat(),
                    ".emf" => new EMFFormat(),
                    ".gif" => new GIFFormat(),
                    ".bmp" => new BMPFormat(),
                    ".tga" => new TGAFormat(),
                    _ => new PNGFormat()
                };
                f.OutputFileName = path;
                f.Resolution = a.Int("dpi", temp ? 96 : 300);
                if (!f.ValidateOutputFilePath()) throw new ToolException("Invalid output path: " + path);
                lyt.Export(f);
                return lyt.Name;
            });
            if (!File.Exists(path)) throw new ToolException("Export produced no file.");
            var r = ToolResult.Of(new JsonObject { ["layout"] = lname, ["savedTo"] = temp ? null : path, ["bytes"] = new FileInfo(path).Length });
            if (returnImage && (ext == ".png" || ext == ".jpg" || ext == ".jpeg"))
                r.Images.Add(new ImagePart { Base64 = Convert.ToBase64String(File.ReadAllBytes(path)), MimeType = ext == ".png" ? "image/png" : "image/jpeg" });
            if (temp) try { File.Delete(path); } catch { }
            return r;
        }
    }
}
