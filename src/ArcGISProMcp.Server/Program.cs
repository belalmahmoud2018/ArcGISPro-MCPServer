using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcGISProMcp.Server.Bridge;
using ArcGISProMcp.Server.Mcp;
using ArcGISProMcp.Server.Setup;

namespace ArcGISProMcp.Server
{
    internal static class Program
    {
        /*
         * ArcGISProMCP.exe                     -> installer / manager window
         * ArcGISProMCP.exe --stdio             -> MCP server on stdin/stdout (what AI clients launch)
         * ArcGISProMCP.exe --http [--port 3939] [--host 127.0.0.1] [--token XYZ]   -> MCP over HTTP (ChatGPT etc.)
         * ArcGISProMCP.exe --install [--clients all|detected|id1,id2] [--quiet]
         * ArcGISProMCP.exe --uninstall [--quiet]
         * ArcGISProMCP.exe --status | --print-config | --list-tools | --version
         */
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
            bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            string Val(string a, string def = null)
            {
                var i = Array.FindIndex(args, x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
            }

            try
            {
                if (Has("--stdio") || (args.Length == 0 && Native.StdinIsRedirected()))
                {
                    Installer.CleanupOldExecutables();
                    new StdioTransport().RunAsync().GetAwaiter().GetResult();
                    return 0;
                }

                if (Has("--http"))
                {
                    Console(args);
                    var opt = new HttpOptions
                    {
                        Port = int.TryParse(Val("--port"), out var p) ? p : 3939,
                        Host = Val("--host", "127.0.0.1"),
                        Token = Val("--token")
                    };
                    if (opt.Host != "127.0.0.1" && opt.Host != "localhost" && string.IsNullOrEmpty(opt.Token))
                    {
                        System.Console.Error.WriteLine("--token is required when listening on a non-loopback address.");
                        return 2;
                    }
                    Log.Sink = l => System.Console.WriteLine(l);
                    var http = new HttpTransport(opt);
                    http.StartAsync().GetAwaiter().GetResult();
                    System.Console.WriteLine($"MCP Streamable HTTP: {http.BaseUrl}/mcp{(opt.Token != null ? "/" + opt.Token : "")}   (Ctrl+C to stop)");
                    var done = new ManualResetEventSlim();
                    System.Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                    done.Wait();
                    http.StopAsync().GetAwaiter().GetResult();
                    return 0;
                }

                if (Has("--install"))
                {
                    Console(args);
                    var sel = (Val("--clients", "detected") ?? "detected").ToLowerInvariant();
                    var all = ClientRegistry.All();
                    var chosen = sel == "all" ? all
                        : sel == "none" ? all.Take(0).ToList()
                        : sel == "detected" ? all.Where(c => c.IsDetected).ToList()
                        : all.Where(c => sel.Split(',').Contains(c.Id)).ToList();
                    Installer.Install(chosen, l => System.Console.WriteLine(l));
                    return 0;
                }

                if (Has("--uninstall"))
                {
                    if (!Has("--quiet"))
                    {
                        Application.EnableVisualStyles();
                        if (MessageBox.Show("Uninstall ArcGIS Pro MCP Server (program, ArcGIS Pro add-in and AI-agent entries)?",
                                AppInfo.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                    }
                    var log = new System.Text.StringBuilder();
                    Installer.Uninstall(l => log.AppendLine(l));
                    if (!Has("--quiet")) MessageBox.Show(log.ToString(), AppInfo.DisplayName);
                    return 0;
                }

                if (Has("--status") || Has("--print-config") || Has("--list-tools") || Has("--version"))
                {
                    Console(args);
                    if (Has("--version")) System.Console.WriteLine(AppInfo.Version);
                    if (Has("--print-config")) System.Console.WriteLine(ClientRegistry.GenericJson(Installer.IsInstalled() ? AppInfo.InstalledExe : AppInfo.CurrentExe));
                    if (Has("--list-tools")) foreach (var t in ToolCatalog.All) System.Console.WriteLine($"{t.Name,-30} {t.Title}");
                    if (Has("--status"))
                    {
                        var (exe, ver) = ProLocator.Find();
                        System.Console.WriteLine($"ArcGIS Pro: {(exe == null ? "not found" : ver + "  " + exe)}");
                        System.Console.WriteLine($"Add-in installed: {Installer.IsAddinInstalled()}  ({Installer.AddinPath})");
                        System.Console.WriteLine($"Server installed: {Installer.IsInstalled()}  ({AppInfo.InstalledExe})");
                        foreach (var i in BridgeClient.Discover()) System.Console.WriteLine($"Connected Pro instance: pid {i.Pid}, version {i.ProVersion}");
                        foreach (var c in ClientRegistry.All())
                            System.Console.WriteLine($"  {(c.IsConfigured() ? "[x]" : c.IsDetected ? "[ ]" : " - ")} {c.Name}  {c.ConfigPath}");
                    }
                    return 0;
                }

                if (Has("--screenshot-ui"))
                {
                    // Renders the manager window to a PNG (used for the user guide).
                    Application.EnableVisualStyles();
                    using var f = new SetupForm { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000), ShowInTaskbar = false };
                    f.Show(); Application.DoEvents();
                    using var bmp = new System.Drawing.Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(Val("--screenshot-ui"), System.Drawing.Imaging.ImageFormat.Png);
                    return 0;
                }

                // Default: GUI installer / manager.
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Fatal", ex);
                try { System.Console.Error.WriteLine(ex.Message); } catch { }
                return 1;
            }
        }

        /// <summary>WinExe has no console: attach to the parent terminal for CLI commands.</summary>
        private static void Console(string[] args)
        {
            if (Native.StdoutIsRedirected()) return;   // output already goes to a pipe/file
            if (Native.AttachConsole(-1))
            {
                var h = Native.OpenConsoleOut();
                if (h == null || h.IsInvalid) return;
                var stdout = new System.IO.StreamWriter(new System.IO.FileStream(h, System.IO.FileAccess.Write)) { AutoFlush = true };
                System.Console.SetOut(stdout);
                System.Console.SetError(stdout);
                System.Console.WriteLine();
            }
        }
    }
}
