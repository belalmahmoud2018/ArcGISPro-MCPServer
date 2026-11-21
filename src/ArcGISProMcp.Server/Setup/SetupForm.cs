using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcGISProMcp.Server.Bridge;
using ArcGISProMcp.Server.Mcp;

namespace ArcGISProMcp.Server.Setup
{
    /// <summary>Installer + manager window (shown when the exe is double-clicked).</summary>
    internal sealed class SetupForm : Form
    {
        private readonly CheckedListBox _clients = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White };
        private readonly Label _proLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 3939, Width = 80 };
        private readonly TextBox _token = new TextBox { Width = 230 };
        private readonly CheckBox _remote = new CheckBox { Text = "Allow LAN / tunnel (0.0.0.0)", AutoSize = true };
        private readonly Button _httpBtn = new Button { Text = "Start HTTP server", AutoSize = true };
        private HttpTransport _http;

        public SetupForm()
        {
            Text = $"{AppInfo.DisplayName} {AppInfo.Version}";
            Width = 900; Height = 720; MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(AppInfo.CurrentExe); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            Controls.Add(root);

            var title = new Label
            {
                Text = "ArcGIS Pro MCP Server  —  control ArcGIS Pro 3.0 – 3.7 from any AI agent",
                AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Color.FromArgb(0, 90, 158), Margin = new Padding(0, 0, 0, 2)
            };
            var titleAr = new Label
            {
                Text = "تحكّم في برنامج ArcGIS Pro من أي وكيل ذكاء اصطناعي",
                RightToLeft = RightToLeft.Yes, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Height = 28,
                Font = new Font("Segoe UI Semibold", 12f), ForeColor = Color.FromArgb(0, 90, 158), Margin = new Padding(0, 0, 0, 8)
            };
            var head = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, AutoSize = true };
            head.Controls.Add(title);
            head.Controls.Add(titleAr);
            head.Controls.Add(_proLabel);
            root.Controls.Add(head);
            root.Controls.Add(new Label { Height = 1, Margin = new Padding(0) });

            var gb = new GroupBox { Text = "AI agents to configure automatically  /  وكلاء الذكاء الاصطناعي", Dock = DockStyle.Fill, Padding = new Padding(8) };
            gb.Controls.Add(_clients);
            root.Controls.Add(gb);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            var install = new Button { Text = "Install / Update  تثبيت", AutoSize = true, BackColor = Color.FromArgb(0, 121, 193), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4) };
            var uninstall = new Button { Text = "Uninstall  إزالة", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            var copy = new Button { Text = "Copy JSON config (other agents)", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            var test = new Button { Text = "Test connection to Pro", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            var logs = new Button { Text = "Open logs", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            var all = new Button { Text = "Select all", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            buttons.Controls.AddRange(new Control[] { install, uninstall, copy, test, all, logs });
            root.Controls.Add(buttons);

            var httpBox = new GroupBox { Text = "HTTP server for ChatGPT / web agents (keep this window open)  —  خادم HTTP لـ ChatGPT", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            var hp = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            hp.Controls.AddRange(new Control[] { new Label { Text = "Port:", AutoSize = true, Margin = new Padding(3, 7, 0, 0) }, _port,
                new Label { Text = "Token:", AutoSize = true, Margin = new Padding(10, 7, 0, 0) }, _token, _remote, _httpBtn });
            httpBox.Controls.Add(hp);
            root.Controls.Add(httpBox);
            root.Controls.Add(_log);

            _token.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

            install.Click += async (s, e) => await RunAsync(() => Installer.Install(_clients.CheckedItems.Cast<AiClient>().ToList(), Append));
            uninstall.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "Remove the MCP server, the ArcGIS Pro add-in and all AI-agent entries?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await RunAsync(() => Installer.Uninstall(Append));
            };
            copy.Click += (s, e) =>
            {
                var json = ClientRegistry.GenericJson(Installer.IsInstalled() ? AppInfo.InstalledExe : AppInfo.CurrentExe);
                Clipboard.SetText(json);
                Append("Copied to clipboard (paste into any MCP client such as Cherry Studio, Chatbox, Continue, Zed, Goose, or DeepSeek-based agents):\r\n" + json);
            };
            test.Click += async (s, e) => await TestAsync();
            all.Click += (s, e) => { for (int i = 0; i < _clients.Items.Count; i++) _clients.SetItemChecked(i, true); };
            logs.Click += (s, e) => Process.Start(new ProcessStartInfo(Path.Combine(AppInfo.DataDir, "logs")) { UseShellExecute = true });
            _httpBtn.Click += async (s, e) => await ToggleHttpAsync();
            FormClosing += async (s, e) => { if (_http != null) await _http.StopAsync(); };

            Load += (s, e) => Refresh2();
        }

        private void Refresh2()
        {
            var (exe, ver) = ProLocator.Find();
            _proLabel.Text = (exe != null ? $"ArcGIS Pro {ver} detected ✔   " : "ArcGIS Pro not found ✖ (install Pro 3.0 – 3.7)   ") +
                             (Installer.IsAddinInstalled() ? "| MCP Bridge add-in installed ✔   " : "| add-in not installed   ") +
                             (Installer.IsInstalled() ? "| server installed ✔" : "| server not installed") +
                             (Installer.HasEmbeddedAddin() ? "" : "   [build has no embedded add-in!]");
            _clients.Items.Clear();
            foreach (var c in ClientRegistry.All())
            {
                var state = c.IsConfigured() ? "configured ✔" : c.IsDetected ? "detected" : "not found";
                var idx = _clients.Items.Add(new ClientItem(c, state));
                _clients.SetItemChecked(idx, c.IsDetected || c.IsConfigured());
            }
            _clients.DisplayMember = "Label";
        }

        private sealed class ClientItem : AiClient
        {
            private readonly AiClient _c;
            public string Label { get; }
            public ClientItem(AiClient c, string state) { _c = c; Id = c.Id; Name = c.Name; Label = $"{c.Name}   [{state}]"; }
            public override string ConfigPath => _c.ConfigPath;
            public override string DetectPath => _c.DetectPath;
            public override bool IsDetected => _c.IsDetected;
            public override bool IsConfigured() => _c.IsConfigured();
            public override void Configure(string exe) => _c.Configure(exe);
            public override void Remove() => _c.Remove();
            public override string ToString() => Label;
        }

        private void Append(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
            _log.AppendText(line + Environment.NewLine);
        }

        private async Task RunAsync(Action a)
        {
            UseWaitCursor = true;
            try { await Task.Run(a); }
            catch (Exception ex) { Append("ERROR: " + ex.Message); }
            finally { UseWaitCursor = false; Refresh2(); }
        }

        private async Task TestAsync()
        {
            var inst = BridgeClient.Discover();
            if (inst.Count == 0)
            {
                Append(BridgeClient.IsProRunning()
                    ? "ArcGIS Pro is running but the add-in is not connected. Restart Pro after installing; check Add-In Manager security options."
                    : "ArcGIS Pro is not running. Start ArcGIS Pro, then test again.");
                return;
            }
            try
            {
                var r = await BridgeClient.CallAsync("get_application_info", new System.Text.Json.Nodes.JsonObject(), TimeSpan.FromSeconds(20), default);
                Append("Connected to ArcGIS Pro ✔\r\n" + r["result"]?.ToJsonString(Json.Indented).Replace("\n", "\r\n"));
            }
            catch (Exception ex) { Append("Connection failed: " + ex.Message); }
        }

        private async Task ToggleHttpAsync()
        {
            if (_http != null)
            {
                await _http.StopAsync(); _http = null;
                _httpBtn.Text = "Start HTTP server"; Append("HTTP server stopped.");
                return;
            }
            var opt = new HttpOptions { Port = (int)_port.Value, Host = _remote.Checked ? "0.0.0.0" : "127.0.0.1", Token = string.IsNullOrWhiteSpace(_token.Text) ? null : _token.Text.Trim() };
            if (opt.Host == "0.0.0.0" && opt.Token == null) { Append("A token is required when allowing remote access."); return; }
            try
            {
                _http = new HttpTransport(opt);
                await _http.StartAsync();
                _httpBtn.Text = "Stop HTTP server";
                var tok = opt.Token == null ? "" : "/" + opt.Token;
                Append($"HTTP MCP server running.\r\n  Streamable HTTP : {_http.BaseUrl}/mcp{tok}\r\n  Legacy SSE      : {_http.BaseUrl}/sse{tok}\r\n" +
                       "ChatGPT (Developer mode > Connectors) needs a public HTTPS URL: run a tunnel, e.g.\r\n" +
                       $"  cloudflared tunnel --url http://localhost:{opt.Port}     or     ngrok http {opt.Port}\r\n" +
                       $"and use  https://<tunnel-host>/mcp{tok}  as the connector URL (authentication: none - the token is in the URL).");
            }
            catch (Exception ex) { _http = null; Append("Could not start HTTP server: " + ex.Message); }
        }
    }
}
