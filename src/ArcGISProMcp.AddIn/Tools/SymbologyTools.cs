using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class SymbologyTools
    {
        private static CIMSymbolReference BaseSymbol(FeatureLayer fl, Args a)
        {
            var color = H.Color(a.Str("color"), ColorFactory.Instance.CreateRGBColor(0, 112, 255));
            var outline = H.Color(a.Str("outline_color"), ColorFactory.Instance.CreateRGBColor(80, 80, 80));
            var size = a.Dbl("size", 8);
            var width = a.Dbl("outline_width", 0.7);
            switch (fl.ShapeType)
            {
                case ArcGIS.Core.CIM.esriGeometryType.esriGeometryPoint:
                case ArcGIS.Core.CIM.esriGeometryType.esriGeometryMultipoint:
                    var style = (a.Str("marker") ?? "circle").ToLowerInvariant() switch
                    {
                        "square" => SimpleMarkerStyle.Square,
                        "triangle" => SimpleMarkerStyle.Triangle,
                        "diamond" => SimpleMarkerStyle.Diamond,
                        "cross" => SimpleMarkerStyle.Cross,
                        "star" => SimpleMarkerStyle.Star,
                        "x" => SimpleMarkerStyle.X,
                        _ => SimpleMarkerStyle.Circle
                    };
                    var pt = SymbolFactory.Instance.ConstructPointSymbol(color, size, style);
                    return pt.MakeSymbolReference();
                case ArcGIS.Core.CIM.esriGeometryType.esriGeometryPolyline:
                    var lineStyle = (a.Str("line_style") ?? "solid").ToLowerInvariant() switch
                    {
                        "dash" => SimpleLineStyle.Dash,
                        "dot" => SimpleLineStyle.Dot,
                        "dashdot" => SimpleLineStyle.DashDot,
                        _ => SimpleLineStyle.Solid
                    };
                    return SymbolFactory.Instance.ConstructLineSymbol(color, a.Dbl("width", a.Dbl("size", 1.5)), lineStyle).MakeSymbolReference();
                default:
                    var fillColor = color;
                    var stroke = SymbolFactory.Instance.ConstructStroke(outline, width, SimpleLineStyle.Solid);
                    var poly = SymbolFactory.Instance.ConstructPolygonSymbol(fillColor,
                        (a.Str("fill_style") ?? "solid").ToLowerInvariant() == "hollow" ? SimpleFillStyle.Null : SimpleFillStyle.Solid, stroke);
                    return poly.MakeSymbolReference();
            }
        }

        private static CIMColorRamp Ramp(string name, string fallback)
        {
            var wanted = string.IsNullOrWhiteSpace(name) ? fallback : name;
            if (wanted == null) return null;
            foreach (var style in Project.Current.GetItems<StyleProjectItem>().Where(s => s.Name == "ArcGIS Colors" || s.Name == "ColorBrewer Schemes (RGB)"))
            {
                try
                {
                    var hit = style.SearchColorRamps(wanted).FirstOrDefault();
                    if (hit != null) return hit.ColorRamp;
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(name) && name.Contains("-"))
            {
                // "red-blue" style custom two-color ramp
                var parts = name.Split('-');
                return ColorFactory.Instance.ConstructColorRamp(ColorRampAlgorithm.LinearContinuous, H.Color(parts[0]), H.Color(parts[1]));
            }
            return null;
        }

        [Tool("set_symbology")]
        public static async Task<ToolResult> SetSymbology(Args a)
        {
            var type = (a.Str("renderer") ?? "simple").ToLowerInvariant();
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var fl = H.FindFeatureLayer(map, a.Req("layer"));
                var sym = BaseSymbol(fl, a);
                RendererDefinition def;
                switch (type)
                {
                    case "unique":
                    case "unique_values":
                    case "uniquevalue":
                        var fields = a.StrList("fields") ?? new System.Collections.Generic.List<string> { a.Req("field") };
                        def = new UniqueValueRendererDefinition(fields, sym, Ramp(a.Str("color_ramp"), "Basic Random"), null, a.Bool("show_other", false));
                        break;
                    case "graduated_colors":
                    case "graduated_color":
                    case "choropleth":
                        def = new GraduatedColorsRendererDefinition(a.Req("field"), Method(a.Str("method")), a.Int("classes", 5),
                            Ramp(a.Str("color_ramp"), "Yellow to Red"), sym)
                        { NormalizationField = a.Str("normalization_field") };
                        break;
                    case "graduated_symbols":
                    case "proportional":
                        def = new GraduatedSymbolsRendererDefinition(a.Req("field"), Method(a.Str("method")), a.Int("classes", 5),
                            null, sym, a.Dbl("min_size", 4), a.Dbl("max_size", 24));
                        break;
                    case "heatmap":
                    case "heat_map":
                        def = new HeatMapRendererDefinition { ColorRamp = Ramp(a.Str("color_ramp"), "Heat Map 1"), Radius = a.Int("radius", 18), WeightField = a.Str("field") };
                        break;
                    default:
                        def = new SimpleRendererDefinition { SymbolTemplate = sym };
                        break;
                }
                var renderer = fl.CreateRenderer(def);
                if (renderer == null) throw new ToolException("Renderer could not be created for this layer/field.");
                fl.SetRenderer(renderer);
                if (a.Dbl("transparency") is double t) fl.SetTransparency(t);
                return new JsonObject { ["layer"] = fl.Name, ["renderer"] = renderer.GetType().Name };
            });
            return ToolResult.Of(res);
        }

        private static ClassificationMethod Method(string s) => (s ?? "natural_breaks").ToLowerInvariant().Replace(" ", "_") switch
        {
            "equal_interval" or "equalinterval" => ClassificationMethod.EqualInterval,
            "quantile" => ClassificationMethod.Quantile,
            "standard_deviation" or "stddev" => ClassificationMethod.StandardDeviation,
            "geometrical_interval" or "geometric" => ClassificationMethod.GeometricalInterval,
            "manual" => ClassificationMethod.Manual,
            _ => ClassificationMethod.NaturalBreaks
        };

        [Tool("set_labels")]
        public static async Task<ToolResult> SetLabels(Args a)
        {
            var res = await Mct(() =>
            {
                var map = H.GetMap(a);
                var fl = H.FindFeatureLayer(map, a.Req("layer"));
                var enabled = a.Bool("enabled", true);
                var expr = a.Str("expression") ?? (a.Has("field") ? "$feature." + a.Str("field") : null);
                if (expr != null || a.Has("font_size") || a.Has("color") || a.Has("font"))
                {
                    var def = fl.GetDefinition() as CIMFeatureLayer;
                    var lc = def.LabelClasses?.FirstOrDefault();
                    if (lc == null) throw new ToolException("Layer has no label class; enable labels in Pro once, or use get_layer_cim/set_layer_cim.");
                    if (expr != null)
                    {
                        var engine = (a.Str("language") ?? "arcade").ToLowerInvariant();
                        lc.ExpressionEngine = engine switch
                        {
                            "python" => LabelExpressionEngine.Python,
                            "vbscript" or "vb" => LabelExpressionEngine.VBScript,
                            "jscript" or "js" => LabelExpressionEngine.JScript,
                            _ => LabelExpressionEngine.Arcade
                        };
                        lc.Expression = expr;
                    }
                    if (a.Has("font_size") || a.Has("color") || a.Has("font"))
                    {
                        var ts = SymbolFactory.Instance.ConstructTextSymbol(H.Color(a.Str("color"), ColorFactory.Instance.BlackRGB),
                            a.Dbl("font_size", 10), a.Str("font") ?? "Tahoma", a.Str("font_style") ?? "Regular");
                        if (a.Has("halo_color"))
                        {
                            ts.HaloSize = a.Dbl("halo_size", 1);
                            var halo = SymbolFactory.Instance.ConstructPolygonSymbol(H.Color(a.Str("halo_color")), SimpleFillStyle.Solid);
                            // drop the default black outline so the halo is a clean solid colour
                            halo.SymbolLayers = halo.SymbolLayers.Where(l => !(l is CIMSolidStroke)).ToArray();
                            ts.HaloSymbol = halo;
                        }
                        lc.TextSymbol = ts.MakeSymbolReference();
                    }
                    lc.Visibility = true;
                    fl.SetDefinition(def);
                }
                fl.SetLabelVisibility(enabled);
                return new JsonObject { ["layer"] = fl.Name, ["labels"] = enabled, ["expression"] = expr };
            });
            return ToolResult.Of(res);
        }
    }
}
