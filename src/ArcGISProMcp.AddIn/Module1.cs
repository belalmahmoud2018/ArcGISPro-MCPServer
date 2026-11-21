using System;
using System.Windows;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISProMcpBridge.Bridge;

namespace ArcGISProMcpBridge
{
    /// <summary>
    /// Auto-loaded module: starts the MCP bridge as soon as ArcGIS Pro starts.
    /// </summary>
    internal class Module1 : Module
    {
        private static Module1 _this;
        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("ArcGISProMcpBridge_Module");

        protected override bool Initialize()
        {
            try { BridgeHost.Start(); }
            catch (Exception ex) { Log.Error("Bridge start failed", ex); }
            return true;
        }

        protected override void Uninitialize()
        {
            try { BridgeHost.Stop(); } catch { }
            base.Uninitialize();
        }

        protected override bool CanUnload() => true;
    }

    internal class StatusButton : Button
    {
        protected override void OnClick()
        {
            var msg = BridgeHost.IsRunning
                ? $"MCP Bridge is RUNNING.\n\nPipe: {BridgeHost.PipeName}\nRequests served: {BridgeHost.RequestCount}\nLast request: {BridgeHost.LastRequest}\n\nAI agents connected through ArcGISProMCP.exe can now control this ArcGIS Pro session."
                : "MCP Bridge is NOT running. Click 'Restart Bridge'.";
            ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(msg, "ArcGIS Pro MCP", MessageBoxButton.OK,
                BridgeHost.IsRunning ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    internal class RestartButton : Button
    {
        protected override void OnClick()
        {
            try { BridgeHost.Stop(); } catch { }
            BridgeHost.Start();
            ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show("MCP Bridge restarted.\nPipe: " + BridgeHost.PipeName, "ArcGIS Pro MCP");
        }
    }
}
