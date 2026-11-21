using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ArcGISProMcp.Server.Mcp
{
    internal sealed class ToolDef
    {
        public string Name;
        public string Title;
        public string Description;
        public JsonObject Schema;
        public bool ReadOnly;
        public bool Destructive;
        public bool Local;          // handled by the MCP server itself (not forwarded to ArcGIS Pro)
        public int TimeoutSec = 120;

        public JsonObject ToJson() => new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = Schema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["openWorldHint"] = false
            }
        };
    }

    /// <summary>
    /// The complete tool surface exposed to AI agents. Schemas are kept deliberately simple
    /// (no oneOf/anyOf, single types) so they are accepted by every MCP client, including
    /// Gemini / Antigravity which reject complex JSON-Schema constructs.
    /// </summary>
    internal static class ToolCatalog
    {
        private sealed class P
        {
            public string Name, Type, Desc, Items;
            public bool Req;
            public string[] Enum;
        }

        private static P S(string n, string d, bool req = false, params string[] en) => new P { Name = n, Type = "string", Desc = d, Req = req, Enum = en.Length > 0 ? en : null };
        private static P N(string n, string d, bool req = false) => new P { Name = n, Type = "number", Desc = d, Req = req };
        private static P I(string n, string d, bool req = false) => new P { Name = n, Type = "integer", Desc = d, Req = req };
        private static P B(string n, string d, bool req = false) => new P { Name = n, Type = "boolean", Desc = d, Req = req };
        private static P O(string n, string d, bool req = false) => new P { Name = n, Type = "object", Desc = d, Req = req };
        private static P A(string n, string d, string items = "string", bool req = false) => new P { Name = n, Type = "array", Desc = d, Items = items, Req = req };

        private static readonly P Map = S("map", "Map name. Defaults to the active map (or the first map in the project).");
        private static readonly P Layer = S("layer", "Layer or standalone table name as shown in the Contents pane.", true);

        private static ToolDef T(string name, string title, string desc, bool ro, bool destructive, params P[] ps)
        {
            var props = new JsonObject();
            var req = new JsonArray();
            foreach (var p in ps)
            {
                var o = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
                if (p.Enum != null) o["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)JsonValue.Create(e)).ToArray());
                if (p.Type == "array") o["items"] = p.Items == "object" ? new JsonObject { ["type"] = "object" } : new JsonObject { ["type"] = p.Items };
                props[p.Name] = o;
                if (p.Req) req.Add(p.Name);
            }
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (req.Count > 0) schema["required"] = req;
            return new ToolDef { Name = name, Title = title, Description = desc, Schema = schema, ReadOnly = ro, Destructive = destructive };
        }

        public static readonly List<ToolDef> All = Build();
        public static readonly Dictionary<string, ToolDef> ByName = All.ToDictionary(t => t.Name);

        private static List<ToolDef> Build()
        {
            var l = new List<ToolDef>();

            // ---------- Session ----------
            var t = T("pro_status", "ArcGIS Pro status",
                "Check whether ArcGIS Pro is running and reachable (lists running Pro instances, versions and the installed Pro version). Call this first if unsure.", true, false);
            t.Local = true; l.Add(t);
            t = T("launch_arcgis_pro", "Launch ArcGIS Pro",
                "Start ArcGIS Pro (optionally opening a .aprx project) and wait until the MCP bridge inside Pro is ready. Pro start-up can take 30-120 s.", false, false,
                S("project_path", "Optional full path of a .aprx project to open."),
                I("wait_seconds", "Max seconds to wait for Pro to become ready (default 240)."));
            t.Local = true; t.TimeoutSec = 600; l.Add(t);
            t = T("select_pro_instance", "Select ArcGIS Pro instance",
                "When several ArcGIS Pro windows are running, choose which one receives the commands (by process id from pro_status).", false, false,
                I("pid", "Process id of the ArcGIS Pro instance.", true));
            t.Local = true; l.Add(t);

            // ---------- Project ----------
            l.Add(T("get_application_info", "Get project & UI state",
                "Return ArcGIS Pro version, the open project (path, default gdb/toolbox, dirty/edits flags), maps, layouts, the active map (spatial reference, scale, extent), open panes and active tool.", true, false));
            l.Add(T("open_project", "Open project", "Open an ArcGIS Pro project (.aprx/.ppkx). Unsaved changes in the current project may trigger a Pro prompt.", false, false,
                S("path", "Full path of the .aprx file.", true)));
            l.Add(T("new_project", "Create project", "Create and open a new ArcGIS Pro project.", false, false,
                S("name", "Project name.", true),
                S("folder", "Parent folder (default Documents\\ArcGIS\\Projects)."),
                S("template", "Template: map | blank | catalog | global_scene | local_scene, or a full path to a .aptx template."),
                B("create_folder", "Create a sub-folder for the project (default true).")));
            l.Add(T("save_project", "Save project", "Save the current project (and pending edits), or Save As when 'path' is given.", false, false,
                S("path", "Optional new .aprx path (Save As)."),
                B("overwrite", "Overwrite an existing file for Save As."),
                B("save_edits", "Also save pending feature edits (default true).")));
            l.Add(T("list_project_items", "List project items", "List items of the project (maps, layouts, folder connections, geodatabases, toolboxes, styles, reports ...).", true, false,
                S("type", "Optional text filter on the item type, e.g. 'Map', 'Layout', 'Folder', 'GDB', 'Toolbox', 'Style'."),
                I("limit", "Max items (default 300).")));
            l.Add(T("browse_folder", "Browse catalog path", "List the contents of a folder, geodatabase (.gdb/.sde), feature dataset or toolbox, like the Catalog pane.", true, false,
                S("path", "Folder, .gdb, .sde or toolbox path.", true), I("limit", "Max children (default 300).")));
            l.Add(T("add_project_item", "Add item to project", "Add a folder connection, geodatabase, toolbox, style or other item to the project.", false, false,
                S("path", "Path of the item to add.", true)));

            // ---------- Maps & navigation ----------
            l.Add(T("list_maps", "List maps", "List all maps and scenes in the project with layer counts and spatial references.", true, false));
            l.Add(T("create_map", "Create map/scene", "Create a new 2D map, global scene or local scene and open it in a view.", false, false,
                S("name", "Map name."),
                S("type", "map (default), global_scene or local_scene.", false, "map", "global_scene", "local_scene"),
                S("basemap", "Basemap: Topographic, Streets, Satellite, Hybrid, Gray, DarkGray, Oceans, Terrain, OpenStreetMap, NationalGeographic, *Vector variants, or None."),
                I("wkid", "Optional spatial reference WKID for the map, e.g. 3857, 4326, 32636."),
                B("open", "Open the map in a view (default true).")));
            l.Add(T("open_map", "Open/activate map view", "Open the map in a view, or activate its existing view, so it becomes the active map.", false, false,
                S("name", "Map name (default: first map).")));
            l.Add(T("map_properties", "Change map properties", "Rename a map, change its spatial reference or its basemap.", false, false,
                Map, S("new_name", "New map name."), I("wkid", "New spatial reference WKID."), S("basemap", "New basemap name (see create_map).")));
            l.Add(T("zoom_to", "Navigate map", "Navigate the active map view: zoom to a layer, an extent, a point+scale, a scale, a bookmark, the selection, full extent, in/out or previous extent. Returns the new extent.", false, false,
                S("layer", "Zoom to this layer's extent."),
                B("selected_only", "With 'layer': zoom only to selected features."),
                O("extent", "Extent object {xmin, ymin, xmax, ymax, wkid} (wkid default 4326)."),
                N("x", "Center X / longitude."), N("y", "Center Y / latitude."), I("wkid", "WKID of x/y (default 4326)."),
                N("scale", "Map scale denominator, e.g. 50000."),
                S("bookmark", "Bookmark name."),
                S("target", "Special target when no other option given.", false, "full", "selection", "in", "out", "previous")));
            l.Add(T("bookmarks", "Bookmarks", "List, create (from the current view) or delete spatial bookmarks.", false, false,
                S("action", "list (default) | create | delete", false, "list", "create", "delete"), S("name", "Bookmark name for create/delete."), Map));
            t = T("capture_map_view", "Screenshot of map", "Render the active map view to an image and RETURN IT so you can see the map (use after changing symbology, zoom, layers...). Optionally also save it (png/jpg/pdf/tif/svg).", true, false,
                I("width", "Pixel width (default 1280)."), I("height", "Pixel height (default 800)."), I("dpi", "Resolution (default 96)."),
                S("output_path", "Optional file path to also save the export."), B("return_image", "Return the image to the AI (default true)."));
            t.TimeoutSec = 180; l.Add(t);
            l.Add(T("undo_redo", "Undo / redo", "Undo or redo operations on the active map (edits, layer changes...).", false, false,
                S("action", "undo (default) or redo", false, "undo", "redo"), I("count", "How many steps (default 1).")));

            // ---------- Layers ----------
            l.Add(T("list_layers", "List layers", "List layers (with type, visibility, selection count, definition query, data source) and standalone tables of a map.", true, false,
                Map, B("include_source", "Include data source info (default true).")));
            l.Add(T("add_layer", "Add data", "Add data to a map: feature class, shapefile, raster, .lyrx layer file, CSV/table, or a web service URL (feature/map/image/tile/WMS/WMTS). Standalone tables are added as tables.", false, false,
                S("data_path", "Path or URL, e.g. C:\\data\\city.gdb\\roads, C:\\data\\dem.tif, https://services.arcgis.com/.../FeatureServer/0", true),
                Map, S("name", "Optional layer name."), I("index", "Position in the Contents (0 = top)."), B("visible", "Visible (default true).")));
            l.Add(T("remove_layer", "Remove layer", "Remove a layer or standalone table from the map (data is not deleted).", false, true, Layer, Map));
            l.Add(T("set_layer_properties", "Layer properties", "Change layer properties: name, visibility, transparency/opacity, visible scale range, order, definition query (SQL filter), selectable/editable, label visibility.", false, false,
                Layer, Map, S("new_name", "New name."), B("visible", "Visibility."), N("transparency", "0-100 (percent)."), N("opacity", "0-100 (percent, alternative to transparency)."),
                N("min_scale", "Min visible scale (0 = none)."), N("max_scale", "Max visible scale (0 = none)."), B("expanded", "Expand in Contents."), I("index", "Move to this position (0 = top)."),
                S("definition_query", "SQL where clause that filters what is drawn, e.g. POP > 10000. Empty string removes it."),
                B("selectable", "Selectable."), B("editable", "Editable."), B("labels", "Turn labels on/off.")));
            l.Add(T("describe_layer", "Describe layer", "Full description of a layer/table: fields (name, alias, type, domain), feature count, geometry type, spatial reference, extent, renderer, source path.", true, false, Layer, Map));
            l.Add(T("get_layer_cim", "Get layer CIM JSON", "Return the full CIM definition (JSON) of a layer: symbology, labeling, popups, everything. Modify it and send back with set_layer_cim for any advanced change.", true, false,
                Layer, Map, I("max_chars", "Truncate output (default 200000).")));
            l.Add(T("set_layer_cim", "Set layer CIM JSON", "Replace a layer's CIM definition with modified JSON from get_layer_cim (advanced symbology/labeling/popup control).", false, false,
                Layer, S("cim_json", "Complete CIM JSON of the layer.", true), Map));
            l.Add(T("open_attribute_table", "Open attribute table", "Open the attribute table pane of a layer/table in the ArcGIS Pro UI.", false, false,
                Layer, Map, B("selected_only", "Show only selected records.")));

            // ---------- Data / selection ----------
            l.Add(T("query_features", "Query attributes", "Read records of a layer/table with an optional SQL where clause (honours the layer definition query). Returns attributes and optionally geometry (WKT or Esri JSON).", true, false,
                Layer, Map, S("where", "SQL where clause, e.g. NAME = 'Cairo' AND POP > 1000000."),
                A("fields", "Fields to return (default all)."), S("order_by", "e.g. POP DESC"), A("object_ids", "Only these ObjectIDs.", "integer"),
                I("max_records", "Max records returned (default 50, max 5000)."), B("return_geometry", "Include geometry."),
                S("geometry_format", "wkt (default) or json", false, "wkt", "json"), I("out_wkid", "Project geometry to this WKID."),
                B("selected_only", "Only the currently selected records.")));
            l.Add(T("summarize_field", "Field statistics", "Statistics of a field: count, nulls, distinct count, min/max/mean/sum (numeric) and the most frequent values.", true, false,
                Layer, S("field", "Field name.", true), S("where", "Optional SQL filter."), I("top_values", "How many frequent values (default 25)."), Map));
            l.Add(T("select_features", "Select by attributes/geometry", "Select features/rows by SQL where clause, ObjectIDs and/or an intersecting geometry. Selection appears in the map.", false, false,
                Layer, Map, S("where", "SQL where clause."), A("object_ids", "ObjectIDs.", "integer"),
                O("geometry", "Esri JSON geometry to intersect ({x,y} | {paths} | {rings} | envelope) - a WKT string is also accepted."), I("wkid", "WKID of the geometry (default: layer SR)."),
                S("mode", "new (default) | add | remove | subset | xor", false, "new", "add", "remove", "subset", "xor")));
            l.Add(T("select_by_location", "Select by location", "Select features of 'layer' by their spatial relationship to features of 'selecting_layer' (uses the Select Layer By Location tool).", false, false,
                Layer, S("selecting_layer", "Layer whose features are used for the spatial test.", true),
                S("relationship", "INTERSECT (default), WITHIN_A_DISTANCE, CONTAINS, COMPLETELY_CONTAINS, WITHIN, COMPLETELY_WITHIN, ARE_IDENTICAL_TO, BOUNDARY_TOUCHES, HAVE_THEIR_CENTER_IN, SHARE_A_LINE_SEGMENT_WITH, CROSSED_BY_THE_OUTLINE_OF ..."),
                S("search_distance", "e.g. '500 Meters' (for WITHIN_A_DISTANCE)."),
                S("mode", "new (default) | add | remove | subset", false, "new", "add", "remove", "subset"),
                B("invert", "Invert the spatial relationship."), Map));
            l.Add(T("clear_selection", "Clear selection", "Clear the selection of one layer or the whole map.", false, false, S("layer", "Layer name (default: all layers)."), Map));
            l.Add(T("get_selection", "Get selection", "Return selection counts and ObjectIDs per layer.", true, false, S("layer", "Layer name (default: all layers with a selection)."), I("max_ids", "Max ObjectIDs per layer (default 1000)."), Map));

            // ---------- Editing ----------
            l.Add(T("add_features", "Create features", "Create new features/rows (undoable edit operation). Then call save_edits.", false, false,
                Layer, A("features", "Array of {\"geometry\": <Esri JSON or WKT>, \"attributes\": {field: value}}. Point shorthand: {\"x\":31.2,\"y\":30.0}.", "object", true),
                I("wkid", "WKID of the input geometries (default: layer SR; use 4326 for lon/lat)."), B("select_new", "Select the new features."), Map));
            l.Add(T("update_features", "Update features", "Update attribute values (and optionally geometry) of features selected by ObjectIDs, where clause or current selection. Then call save_edits.", false, true,
                Layer, O("attributes", "Object of {field: newValue}."), O("geometry", "Optional new geometry (Esri JSON; WKT string also accepted)."), I("wkid", "WKID of the geometry."),
                A("object_ids", "Target ObjectIDs.", "integer"), S("where", "Target SQL where clause."), B("selected_only", "Target the current selection."), Map));
            l.Add(T("delete_features", "Delete features", "Delete features/rows by ObjectIDs, where clause or current selection (undoable until saved).", false, true,
                Layer, A("object_ids", "ObjectIDs.", "integer"), S("where", "SQL where clause."), B("selected_only", "Delete the selected features."), Map));
            l.Add(T("save_edits", "Save / discard edits", "Save all pending edits to the data (or discard them).", false, true, B("discard", "Discard instead of saving.")));

            // ---------- Symbology & labels ----------
            l.Add(T("set_symbology", "Set symbology", "Change how a feature layer is drawn: single symbol, unique values, graduated colors (choropleth), graduated symbols (proportional) or heat map.", false, false,
                Layer, S("renderer", "simple (default) | unique_values | graduated_colors | graduated_symbols | heatmap", false, "simple", "unique_values", "graduated_colors", "graduated_symbols", "heatmap"),
                S("field", "Field for unique/graduated/heatmap weight."), A("fields", "Up to 3 fields for unique values."),
                S("color", "Fill/marker/line color: name (red), hex (#FF8800) or rgb(255,136,0)."), S("outline_color", "Outline color."), N("outline_width", "Outline width (pt)."),
                N("size", "Marker size / line width (pt)."), N("width", "Line width (pt)."),
                S("marker", "Point marker: circle, square, triangle, diamond, cross, star, x"), S("line_style", "solid, dash, dot, dashdot"), S("fill_style", "solid or hollow"),
                S("color_ramp", "ArcGIS color ramp name, e.g. 'Yellow to Red', 'Blues (Continuous)', 'Spectral (Continuous)', 'Basic Random', or two colors 'white-darkblue'."),
                I("classes", "Number of classes (default 5)."), S("method", "natural_breaks (default), equal_interval, quantile, standard_deviation, geometrical_interval"),
                S("normalization_field", "Normalize graduated colors by this field."), N("min_size", "Graduated symbols min size."), N("max_size", "Graduated symbols max size."),
                I("radius", "Heat map radius (pixels)."), N("transparency", "Layer transparency 0-100."), Map));
            l.Add(T("set_labels", "Labels", "Turn labels on/off and set the label expression (Arcade by default, e.g. $feature.NAME) and text style.", false, false,
                Layer, B("enabled", "Labels on (default true)."), S("field", "Label with this field (shortcut for an Arcade expression)."),
                S("expression", "Label expression, e.g. $feature.NAME + ' (' + $feature.POP + ')'"),
                S("language", "arcade (default) | python | vbscript | jscript", false, "arcade", "python", "vbscript", "jscript"),
                N("font_size", "Font size (pt)."), S("font", "Font family, e.g. Arial, Tahoma."), S("font_style", "Regular, Bold, Italic"),
                S("color", "Text color."), S("halo_color", "Halo color."), N("halo_size", "Halo size."), Map));

            // ---------- Layouts ----------
            l.Add(T("list_layouts", "List layouts", "List layouts with page size and elements.", true, false));
            l.Add(T("create_layout", "Create layout", "Create a print layout with a map frame and optional title, legend, north arrow, scale bar and subtitle, then open it.", false, false,
                S("name", "Layout name."), Map, N("width", "Page width (default 11 in / A4 landscape)."), N("height", "Page height."),
                S("units", "inches (default) | cm | mm | points", false, "inches", "cm", "mm", "points"),
                S("title", "Title text."), N("title_size", "Title font size."), S("subtitle", "Small text (author, date, source)."),
                B("legend", "Add legend (default true)."), B("north_arrow", "Add north arrow (default true)."), B("scale_bar", "Add scale bar (default true)."), B("open", "Open the layout (default true).")));
            l.Add(T("open_layout", "Open layout", "Open/activate a layout view.", false, false, S("name", "Layout name.")));
            l.Add(T("add_layout_text", "Add text to layout", "Add a text element to a layout at page coordinates.", false, false,
                S("text", "Text.", true), S("layout", "Layout name (default active/first)."), N("x", "X in page units."), N("y", "Y in page units."),
                N("font_size", "Font size."), S("font", "Font family."), S("font_style", "Regular/Bold/Italic."), S("color", "Color."), S("element_name", "Element name.")));
            l.Add(T("add_layout_picture", "Add picture to layout", "Add an image (logo, photo) to a layout.", false, false,
                S("path", "Image file path.", true), S("layout", "Layout name."), N("xmin", "Left."), N("ymin", "Bottom."), N("xmax", "Right."), N("ymax", "Top."), S("element_name", "Element name.")));
            t = T("export_layout", "Export layout", "Export a layout to PDF, PNG, JPG, TIFF, SVG, EPS, EMF, GIF, BMP. Without output_path it returns a PNG preview image so you can see the layout.", false, false,
                S("layout", "Layout name (default active/first)."), S("output_path", "Output file; the extension selects the format."), I("dpi", "Resolution (default 300 for files)."),
                B("return_image", "Return the image to the AI (png/jpg)."));
            t.TimeoutSec = 600; l.Add(t);

            // ---------- Geoprocessing ----------
            t = T("run_geoprocessing", "Run geoprocessing tool",
                "Run ANY of the 1500+ ArcGIS Pro geoprocessing tools (analysis, management, conversion, spatial analyst, network analyst, 3D, image analyst, ...). " +
                "Give the tool name ('analysis.Buffer', 'management.Clip', 'Buffer_analysis' ...) and the parameters in the tool's order (use get_geoprocessing_tool_info to learn them). " +
                "Layer names of the active map can be used as inputs. Returns messages and outputs; outputs are added to the map by default.", false, true,
                S("tool", "Tool name, e.g. analysis.Buffer, management.CopyFeatures, conversion.ExportFeatures, sa.Slope", true),
                A("parameters", "Ordered parameter values as strings. Use '' to skip optional ones. Multi-value: 'a;b;c'."),
                O("environments", "Environment settings, e.g. {\"workspace\":\"C:/data/a.gdb\", \"outputCoordinateSystem\":\"4326\", \"overwriteOutput\":true, \"extent\":\"MAXOF\"}."),
                B("add_outputs_to_map", "Add outputs to the active map (default true)."));
            t.TimeoutSec = 3600; l.Add(t);
            l.Add(T("list_geoprocessing_tools", "Find geoprocessing tools", "Search the available geoprocessing tools by wildcard (e.g. '*buffer*', '*_sa', 'Clip*').", true, false,
                S("filter", "Wildcard filter."), I("limit", "Max tools (default 400).")));
            l.Add(T("get_geoprocessing_tool_info", "Geoprocessing tool help", "Usage, parameter names, order, data types, required/optional flags and allowed values of a geoprocessing tool.", true, false,
                S("tool", "Tool name, e.g. analysis.Buffer or Buffer_analysis", true)));

            // ---------- Python ----------
            t = T("run_python", "Run Python/ArcPy inside Pro",
                "Execute Python code INSIDE the running ArcGIS Pro (same as the Pro Python window). arcpy is imported and 'aprx' = arcpy.mp.ArcGISProject('CURRENT'). " +
                "This gives full control over everything arcpy can do: arcpy.mp (maps, layers, layouts, symbology, map series, reports), arcpy.da cursors, Spatial Analyst, Network Analyst, data management, pandas/numpy, etc. " +
                "Variables persist between calls. Use print() for output; the value of the last expression (or a variable named 'result') is returned as JSON.", false, true,
                S("code", "Python source code.", true),
                B("reset_session", "Clear variables from previous calls."),
                S("return_image_path", "Optional path of an image produced by the code to return to the AI."));
            t.TimeoutSec = 3600; l.Add(t);

            // ---------- UI commands ----------
            l.Add(T("search_commands", "Search Pro UI commands",
                "Search every ribbon button, tool, menu and pane of the installed ArcGIS Pro by caption/tooltip/id (e.g. 'attribute table', 'measure', 'export map', 'georeference'). Returns DAML ids for execute_command.", true, false,
                S("query", "Words to search for.", true), I("limit", "Max results (default 40).")));
            l.Add(T("execute_command", "Click a Pro UI command",
                "Execute any ArcGIS Pro ribbon button/command, activate a map tool or open a dock pane by its DAML id (find ids with search_commands), e.g. esri_mapping_newMapButton, esri_core_saveProjectButton, esri_mapping_selectByAttributeButton.", false, true,
                S("command_id", "DAML id.", true), S("parameter", "Optional command parameter.")));

            return l;
        }

        public const string Instructions =
@"You are connected to a live ArcGIS Pro desktop session (versions 3.0-3.7) through the ArcGIS Pro MCP server.
Workflow tips:
- Start with pro_status / get_application_info. If Pro is not running call launch_arcgis_pro.
- Inspect before acting: list_maps, list_layers, describe_layer (fields!), query_features, summarize_field.
- Use capture_map_view after visual changes so you can SEE the result; export_layout without a path previews a layout.
- Analysis: run_geoprocessing can run any geoprocessing tool; check parameters with get_geoprocessing_tool_info.
- Anything else (map series, reports, symbology details, arcpy.da, raster algebra...) can be done with run_python, which executes arcpy inside Pro with aprx = arcpy.mp.ArcGISProject('CURRENT').
- Any ribbon button can be pressed with search_commands + execute_command.
- Edits are undoable (undo_redo) until save_edits; save the project with save_project when the user wants changes kept.
- Ask the user before destructive actions (deleting data, overwriting outputs, discarding edits).";
    }
}
