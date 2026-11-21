using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMcpBridge.Bridge;
using static ArcGISProMcpBridge.Bridge.Threading;

namespace ArcGISProMcpBridge.Tools
{
    internal static class ProjectTools
    {
        [Tool("get_application_info")]
        public static async Task<ToolResult> AppInfo(Args a)
        {
            var paneInfo = await Ui(() =>
            {
                var pane = FrameworkApplication.Panes.ActivePane;
                return new JsonObject
                {
                    ["activePaneCaption"] = pane?.Caption,
                    ["activePaneType"] = pane?.GetType().Name,
                    ["openPanes"] = J.Arr(FrameworkApplication.Panes.Cast<ArcGIS.Desktop.Framework.Contracts.Pane>(), p => JsonValue.Create(p.Caption)),
                    ["activeTool"] = FrameworkApplication.ActiveTool?.ID
                };
            });

            var info = await Mct(() =>
            {
                var o = new JsonObject
                {
                    ["arcgisProVersion"] = H.ProVersion,
                    ["bridgeVersion"] = typeof(ProjectTools).Assembly.GetName().Version?.ToString(),
                    ["processId"] = Environment.ProcessId,
                    ["ui"] = paneInfo
                };
                var p = Project.Current;
                if (p == null) { o["project"] = null; return o; }
                o["project"] = new JsonObject
                {
                    ["name"] = p.Name,
                    ["path"] = p.URI,
                    ["homeFolder"] = p.HomeFolderPath,
                    ["defaultGeodatabase"] = p.DefaultGeodatabasePath,
                    ["defaultToolbox"] = p.DefaultToolboxPath,
                    ["isDirty"] = p.IsDirty,
                    ["hasEdits"] = p.HasEdits
                };
                o["maps"] = J.Arr(p.GetItems<MapProjectItem>(), m => JsonValue.Create(m.Name));
                o["layouts"] = J.Arr(p.GetItems<LayoutProjectItem>(), l => JsonValue.Create(l.Name));
                var mv = MapView.Active;
                if (mv != null)
                {
                    o["activeMap"] = new JsonObject
                    {
                        ["name"] = mv.Map.Name,
                        ["mapType"] = mv.Map.MapType.ToString(),
                        ["viewingMode"] = mv.ViewingMode.ToString(),
                        ["spatialReference"] = H.SrJson(mv.Map.SpatialReference),
                        ["scale"] = mv.Camera?.Scale,
                        ["extent"] = H.Extent(mv.Extent),
                        ["layerCount"] = mv.Map.GetLayersAsFlattenedList().Count
                    };
                }
                var lv = LayoutView.Active;
                if (lv != null) o["activeLayout"] = lv.Layout?.Name;
                return o;
            });
            return ToolResult.Of(info);
        }

        [Tool("open_project")]
        public static async Task<ToolResult> OpenProject(Args a)
        {
            var path = a.Req("path");
            if (!File.Exists(path) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("Project file not found: " + path);
            await Ui(async () => { await Project.OpenAsync(path); return true; });
            return ToolResult.Ok("Opened project " + Project.Current?.URI);
        }

        [Tool("new_project")]
        public static async Task<ToolResult> NewProject(Args a)
        {
            var name = a.Req("name");
            var folder = a.Str("folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArcGIS", "Projects");
            var template = (a.Str("template") ?? "map").ToLowerInvariant();
            var settings = new CreateProjectSettings { Name = name, LocationPath = folder, CreateNewProjectFolder = a.Bool("create_folder", true) };
            if (File.Exists(template)) settings.TemplatePath = template;
            else settings.TemplateType = template switch
            {
                "blank" => TemplateType.Untitled,
                "catalog" => TemplateType.Catalog,
                "global_scene" or "globalscene" => TemplateType.GlobalScene,
                "local_scene" or "localscene" => TemplateType.LocalScene,
                _ => TemplateType.Map
            };
            await Ui(async () => { await Project.CreateAsync(settings); return true; });
            return ToolResult.Ok("Created project " + Project.Current?.URI);
        }

        [Tool("save_project")]
        public static async Task<ToolResult> SaveProject(Args a)
        {
            H.RequireProject();
            var saveAs = a.Str("path");
            bool ok;
            if (!string.IsNullOrWhiteSpace(saveAs)) ok = await Ui(() => Project.Current.SaveAsAsync(saveAs, a.Bool("overwrite", false)));
            else ok = await Ui(() => Project.Current.SaveAsync());
            if (a.Bool("save_edits", true) && Project.Current.HasEdits) await Project.Current.SaveEditsAsync();
            return ToolResult.Of(new JsonObject { ["ok"] = ok, ["path"] = Project.Current.URI });
        }

        [Tool("list_project_items")]
        public static async Task<ToolResult> ListItems(Args a)
        {
            H.RequireProject();
            var filter = (a.Str("type") ?? "").ToLowerInvariant();
            var limit = a.Int("limit", 300);
            var res = await Mct(() =>
            {
                var items = Project.Current.GetItems<Item>();
                var arr = new JsonArray();
                foreach (var i in items)
                {
                    var t = i.GetType().Name + "|" + i.TypeID;
                    if (filter.Length > 0 && t.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    arr.Add(new JsonObject { ["name"] = i.Name, ["type"] = i.TypeID, ["class"] = i.GetType().Name, ["path"] = i.Path });
                    if (arr.Count >= limit) break;
                }
                return arr;
            });
            return ToolResult.Of(new JsonObject { ["count"] = res.Count, ["items"] = res });
        }

        [Tool("browse_folder")]
        public static async Task<ToolResult> BrowseFolder(Args a)
        {
            var path = a.Req("path");
            var limit = a.Int("limit", 300);
            var res = await Mct(() =>
            {
                var item = ItemFactory.Instance.Create(path);
                if (item == null) throw new ToolException("Cannot browse " + path);
                var arr = new JsonArray();
                foreach (var c in item.GetItems())
                {
                    arr.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.TypeID, ["path"] = c.Path, ["isContainer"] = c.IsContainer });
                    if (arr.Count >= limit) break;
                }
                return new JsonObject { ["path"] = item.Path, ["type"] = item.TypeID, ["children"] = arr };
            });
            return ToolResult.Of(res);
        }

        [Tool("add_project_item")]
        public static async Task<ToolResult> AddItem(Args a)
        {
            H.RequireProject();
            var path = a.Req("path");
            var name = await Mct(() =>
            {
                var item = ItemFactory.Instance.Create(path, ItemFactory.ItemType.PathItem);
                if (item is IProjectItem pi) { Project.Current.AddItem(pi); return item.Name; }
                throw new ToolException("This item type cannot be added to the project: " + path);
            });
            return ToolResult.Ok($"Added '{name}' to the project.");
        }
    }
}
