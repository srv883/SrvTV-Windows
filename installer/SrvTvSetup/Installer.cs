using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;

namespace SrvTvSetup
{
    // Install / uninstall steps, no UI. Per-user: everything goes under the
    // user's profile and HKCU, so no admin rights are needed.
    internal static class Installer
    {
        public const string AppName = "Srv TV";
        public const string ExeName = "SrvTv.exe";
        public const string UninstallerName = "Uninstall Srv TV.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SrvTv";

        public static string DefaultDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SrvTv");

        // Favorites, cache and logs live here (not in the install folder).
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrvTv");

        private static string StartMenuLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        private static string DesktopLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k?.GetValue("InstallLocation") as string;
        }

        public static Process[] RunningCopies(string dir)
        {
            var exe = Path.GetFullPath(Path.Combine(dir, ExeName));
            return Process.GetProcessesByName("SrvTv").Where(p =>
            {
                try { return string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToArray();
        }

        public static void CloseRunning(string dir)
        {
            foreach (var p in RunningCopies(dir))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(4000); }
                }
                catch { }
            }
        }

        public static void Install(string dir, bool desktopShortcut, Action<int, string> progress)
        {
            dir = Path.GetFullPath(dir);
            Unpack(dir, progress);

            progress(94, "Adding shortcuts...");
            var exe = Path.Combine(dir, ExeName);
            Payload.WriteStub(Path.Combine(dir, UninstallerName));
            MakeShortcut(StartMenuLink, exe, dir);
            if (desktopShortcut) MakeShortcut(DesktopLink, exe, dir);
            else TryDeleteFile(DesktopLink);

            progress(97, "Registering with Windows...");
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + Path.Combine(dir, UninstallerName) + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(dir, UninstallerName) + "\" /uninstall /quiet");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            progress(100, "Done");
        }

        // Extract the app into dir, replacing an earlier copy there.
        public static void Unpack(string dir, Action<int, string> progress)
        {
            dir = Path.GetFullPath(dir);
            var where = Payload.Find();
            if (where == null) throw new InvalidOperationException("This setup file is incomplete (no app inside). Download it again.");

            // Safety: only ever replace an empty folder or an earlier Srv TV install,
            // never some other folder the user happened to pick.
            if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, ExeName))
                && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new InvalidOperationException("The folder \"" + dir + "\" already has other files in it. Pick an empty folder.");

            var parent = Path.GetDirectoryName(dir);
            Directory.CreateDirectory(parent);
            var fresh = dir + ".new";
            var old = dir + ".old";
            TryDelete(fresh);
            TryDelete(old);

            progress(0, "Unpacking Srv TV...");
            using (var s = Payload.Open(where))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                var files = zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToList();
                var total = Math.Max(1L, files.Sum(e => e.Length));
                long done = 0;
                var lastPct = -1;
                var root = Path.GetFullPath(fresh) + Path.DirectorySeparatorChar;
                foreach (var e in files)
                {
                    var target = Path.GetFullPath(Path.Combine(fresh, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                    done += e.Length;
                    var pct = (int)(done * 90 / total);
                    if (pct != lastPct) { lastPct = pct; progress(pct, "Unpacking Srv TV..."); }
                }
            }

            progress(91, "Replacing the old version...");
            CloseRunning(dir);
            if (Directory.Exists(dir))
            {
                Retry(() => Directory.Move(dir, old));
            }
            Retry(() => Directory.Move(fresh, dir));
            TryDelete(old);
        }

        public static void Uninstall(string dir, bool removeData)
        {
            dir = Path.GetFullPath(dir);
            if (!File.Exists(Path.Combine(dir, ExeName)) && !File.Exists(Path.Combine(dir, UninstallerName)))
                throw new InvalidOperationException("Srv TV is not installed in \"" + dir + "\".");
            CloseRunning(dir);
            TryDeleteFile(StartMenuLink);
            TryDeleteFile(DesktopLink);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (removeData) TryDelete(DataDir);

            // This uninstaller runs from inside the folder, so delete
            // everything else now and let a short-lived cmd finish the job.
            var self = Path.GetFullPath(Payload.SelfPath);
            foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : new string[0])
                if (!string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(f);
            foreach (var d in Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]) TryDelete(d);
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }

        private static void MakeShortcut(string link, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = target;
                sc.WorkingDirectory = workDir;
                sc.IconLocation = target + ",0";
                sc.Description = "Watch live TV channels";
                sc.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        private static long DirSize(string dir)
        {
            try { return new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        private static void Retry(Action a)
        {
            for (var i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) when (i < 10) { System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) when (i < 10) { System.Threading.Thread.Sleep(500); }
            }
        }

        private static void TryDelete(string dir)
        {
            for (var i = 0; i < 5 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch { System.Threading.Thread.Sleep(400); }
            }
        }

        private static void TryDeleteFile(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
