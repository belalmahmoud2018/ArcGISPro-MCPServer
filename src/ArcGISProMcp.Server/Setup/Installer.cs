using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArcGISProMcp.Server.Bridge;
using Microsoft.Win32;

namespace ArcGISProMcp.Server.Setup
{
    internal static class Installer
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ArcGISProMCP";

        public static string AddinDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ArcGIS", "AddIns", "ArcGISPro", AppInfo.AddinGuid);
        public static string AddinPath => Path.Combine(AddinDir, AppInfo.AddinFileName);

        public static bool IsAddinInstalled() => File.Exists(AddinPath);
        public static bool IsInstalled() => File.Exists(AppInfo.InstalledExe);

        public static bool HasEmbeddedAddin() =>
            typeof(Installer).Assembly.GetManifestResourceNames().Contains(AppInfo.AddinFileName);

        /// <summary>Full install: copy exe, deploy add-in, configure AI clients, register uninstaller.</summary>
        public static void Install(IEnumerable<AiClient> clients, Action<string> log)
        {
            log("== Installing " + AppInfo.DisplayName + " " + AppInfo.Version + " ==");
            var (proExe, proVer) = ProLocator.Find();
            if (proExe == null) log("WARNING: ArcGIS Pro was not found. The add-in will still be deployed and will load once Pro 3.x is installed.");
            else
            {
                log($"ArcGIS Pro {proVer} found: {proExe}");
                if (Version.TryParse(proVer, out var v) && v.Major < 3) log("WARNING: ArcGIS Pro " + proVer + " is older than 3.0 and is not supported.");
            }

            // 1. Program files
            var exe = CopySelf(log);

            // 2. ArcGIS Pro add-in
            DeployAddin(log);

            // 3. AI clients
            foreach (var c in clients)
            {
                try { c.Configure(exe); log($"Configured {c.Name}  ->  {c.ConfigPath}"); }
                catch (Exception ex) { log($"FAILED to configure {c.Name}: {ex.Message}"); }
            }

            // 4. Uninstall entry + Start menu shortcut
            try { RegisterUninstall(exe); log("Registered in Windows 'Installed apps'."); } catch (Exception ex) { log("Uninstall entry: " + ex.Message); }
            try { CreateShortcut(exe); log("Start menu shortcut created."); } catch (Exception ex) { log("Shortcut: " + ex.Message); }

            if (BridgeClient.IsProRunning()) log("NOTE: ArcGIS Pro is running - restart it to load the MCP Bridge add-in.");
            log("Restart your AI apps (Claude Desktop, Cursor, ...) so they pick up the new MCP server.");
            log("== Done ==");
        }

        public static string CopySelf(Action<string> log)
        {
            var target = AppInfo.InstalledExe;
            var current = AppInfo.CurrentExe;
            Directory.CreateDirectory(AppInfo.InstallDir);
            if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(current, target, true);
                    log("Installed program: " + target);
                }
                catch (IOException)
                {
                    // The installed exe is running (an AI app has it open). Windows allows renaming a
                    // running executable, so move it aside and put the new version in place.
                    var old = target + "." + DateTime.Now.Ticks + ".old";
                    File.Move(target, old);
                    File.Copy(current, target, true);
                    log("Updated program (old copy in use will be cleaned up later): " + target);
                }
            }
            return target;
        }

        /// <summary>Best-effort removal of executables replaced while they were running.</summary>
        public static void CleanupOldExecutables()
        {
            try
            {
                if (!Directory.Exists(AppInfo.InstallDir)) return;
                foreach (var f in Directory.GetFiles(AppInfo.InstallDir, "*.old"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        public static void DeployAddin(Action<string> log)
        {
            using var s = typeof(Installer).Assembly.GetManifestResourceStream(AppInfo.AddinFileName);
            if (s == null) { log("ERROR: this build does not contain the ArcGIS Pro add-in."); return; }
            Directory.CreateDirectory(AddinDir);
            using (var f = File.Create(AddinPath)) s.CopyTo(f);
            log("ArcGIS Pro add-in installed: " + AddinPath);

            // Make Pro re-extract the new version (it caches add-ins in AssemblyCache).
            if (!BridgeClient.IsProRunning())
            {
                try
                {
                    var cache = Path.Combine(AppInfo.Local, "ESRI", "ArcGISPro", "AssemblyCache", AppInfo.AddinGuid);
                    if (Directory.Exists(cache)) Directory.Delete(cache, true);
                }
                catch { }
            }

            // Make sure Pro scans the default add-in folder (it does by default; keep user's setting otherwise).
            log("If ArcGIS Pro restricts add-ins: Project > Add-In Manager > Options > 'Load all Add-Ins without restrictions'.");
        }

        public static void Uninstall(Action<string> log)
        {
            log("== Uninstalling ==");
            foreach (var c in ClientRegistry.All())
            {
                try { if (c.IsConfigured()) { c.Remove(); log("Removed from " + c.Name); } }
                catch (Exception ex) { log($"{c.Name}: {ex.Message}"); }
            }
            try { if (Directory.Exists(AddinDir)) Directory.Delete(AddinDir, true); log("Removed ArcGIS Pro add-in."); }
            catch (Exception ex) { log("Add-in removal: " + ex.Message + " (close ArcGIS Pro and retry)"); }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try
            {
                var lnk = ShortcutPath;
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch { }
            try { var inst = Path.Combine(AppInfo.DataDir, "instances"); if (Directory.Exists(inst)) Directory.Delete(inst, true); } catch { }

            // Delete the program folder after we exit.
            if (Directory.Exists(AppInfo.InstallDir))
            {
                var cmd = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{AppInfo.InstallDir}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
            }
            log("== Uninstalled. Logs remain in " + AppInfo.DataDir + " ==");
        }

        private static void RegisterUninstall(string exe)
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", AppInfo.DisplayName);
            k.SetValue("DisplayVersion", AppInfo.Version);
            k.SetValue("Publisher", "ArcGIS Pro MCP");
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", AppInfo.InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord); } catch { }
        }

        private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ArcGIS Pro MCP Server.lnk");

        private static void CreateShortcut(string exe)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t);
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.Description = AppInfo.DisplayName + " - manager";
            lnk.IconLocation = exe + ",0";
            lnk.Save();
        }
    }
}
