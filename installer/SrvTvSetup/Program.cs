using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SrvTvSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var a = args.Select(x => x.ToLowerInvariant()).ToArray();
            var quiet = a.Contains("/quiet") || a.Contains("/silent");

            if (a.Contains("/uninstall")) return RunUninstall(quiet);

            if (Payload.Find() == null)
            {
                Msg("This file has no Srv TV inside it. Use the full SrvTV-Setup.exe.", MessageBoxIcon.Error);
                return 2;
            }
            if (!Environment.Is64BitOperatingSystem)
            {
                Msg("Srv TV needs 64-bit Windows 10 or 11.", MessageBoxIcon.Error);
                return 3;
            }
            // Test aids: /preview=<png> renders the window to a file without showing it;
            // /dir=<path> with /portable unpacks only (no shortcuts or registry).
            var preview = a.FirstOrDefault(x => x.StartsWith("/preview="));
            if (preview != null)
            {
                var file = args.First(x => x.ToLowerInvariant().StartsWith("/preview=")).Substring(9);
                var f = new SetupForm { PreviewOnly = true, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, 0), ShowInTaskbar = false };
                f.Shown += (s, e) =>
                {
                    Application.DoEvents();
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(file);
                    }
                    f.Close();
                };
                Application.Run(f);
                return 0;
            }
            var dirArg = args.FirstOrDefault(x => x.ToLowerInvariant().StartsWith("/dir="));
            if (quiet)
            {
                try
                {
                    var dir = dirArg != null ? dirArg.Substring(5) : Installer.InstalledDir() ?? Installer.DefaultDir;
                    if (a.Contains("/portable")) Installer.Unpack(dir, (p, s) => { });
                    else Installer.Install(dir, true, (p, s) => { });
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "SrvTvSetup-error.txt"), ex.ToString()); } catch { }
                    return 1;
                }
            }
            Application.Run(new SetupForm());
            return 0;
        }

        private static int RunUninstall(bool quiet)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(Payload.SelfPath));
            var removeData = false;
            if (!quiet)
            {
                if (MessageBox.Show("Remove Srv TV from this PC?", "Uninstall Srv TV",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                removeData = MessageBox.Show(
                    "Also delete your favorites, watch history and settings?\n\nChoose No to keep them for a future reinstall.",
                    "Uninstall Srv TV", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            try
            {
                Installer.Uninstall(dir, removeData);
                if (!quiet) Msg("Srv TV has been removed.", MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                if (!quiet) Msg("Could not finish removing Srv TV:\n" + ex.Message, MessageBoxIcon.Warning);
                return 1;
            }
        }

        public static void Msg(string text, MessageBoxIcon icon) =>
            MessageBox.Show(text, "Srv TV Setup", MessageBoxButtons.OK, icon);
    }

    internal sealed class SetupForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(0x0E, 0x11, 0x18);
        private static readonly Color Surface = Color.FromArgb(0x1A, 0x1F, 0x2B);
        private static readonly Color Stroke = Color.FromArgb(0x2A, 0x31, 0x42);
        private static readonly Color Blue = Color.FromArgb(0x0A, 0x84, 0xFF);
        private static readonly Color Sky = Color.FromArgb(0x5A, 0xC8, 0xFA);
        private static readonly Color Muted = Color.FromArgb(0x9A, 0xA2, 0xB0);

        private readonly TextBox _path = new TextBox();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly CheckBox _launch = new CheckBox();
        private readonly Button _install = new Button();
        private readonly Button _cancel = new Button();
        private readonly Button _browse = new Button();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private bool _busy;
        private bool _done;

        public bool PreviewOnly { get; set; }
        protected override bool ShowWithoutActivation => PreviewOnly;

        public SetupForm()
        {
            Text = "Srv TV Setup";
            Icon = Icon.ExtractAssociatedIcon(Payload.SelfPath);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(560, 400);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);

            var logo = new Panel { Location = new Point(32, 30), Size = new Size(56, 56) };
            logo.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new Rectangle(0, 0, logo.Width - 1, logo.Height - 1), 14))
                using (var br = new LinearGradientBrush(logo.ClientRectangle, Blue, Sky, 45f))
                    e.Graphics.FillPath(br, path);
                TextRenderer.DrawText(e.Graphics, "TV", new Font("Georgia", 17f, FontStyle.Bold),
                    logo.ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            Controls.Add(logo);

            Controls.Add(new Label
            {
                Text = "Srv TV", Font = new Font("Georgia", 22f), AutoSize = true,
                Location = new Point(102, 28), ForeColor = Color.White,
            });
            Controls.Add(new Label
            {
                Text = "Live TV channels for Windows", AutoSize = true, ForeColor = Muted,
                Location = new Point(105, 66),
            });

            Controls.Add(new Label { Text = "Install to", AutoSize = true, ForeColor = Muted, Location = new Point(32, 122) });
            _path.Text = Installer.InstalledDir() ?? Installer.DefaultDir;
            _path.Location = new Point(32, 146);
            _path.Size = new Size(392, 28);
            _path.BackColor = Surface;
            _path.ForeColor = Color.White;
            _path.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_path);

            Style(_browse, "Change...", false);
            _browse.Location = new Point(434, 144);
            _browse.Size = new Size(94, 30);
            _browse.Click += (s, e) => Browse();
            Controls.Add(_browse);

            _desktop.Text = "Put a shortcut on the desktop";
            _desktop.Checked = true;
            _launch.Text = "Open Srv TV when setup finishes";
            _launch.Checked = true;
            _desktop.Location = new Point(32, 192);
            _launch.Location = new Point(32, 222);
            foreach (var c in new[] { _desktop, _launch })
            {
                c.AutoSize = true;
                c.ForeColor = Color.White;
                c.FlatStyle = FlatStyle.Flat;
                Controls.Add(c);
            }

            _bar.Location = new Point(32, 282);
            _bar.Size = new Size(496, 8);
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Visible = false;
            Controls.Add(_bar);

            _status.Location = new Point(32, 298);
            _status.Size = new Size(496, 40);
            _status.ForeColor = Muted;
            _status.Text = Installer.InstalledDir() != null
                ? "Srv TV is already installed. This will update it and keep your favorites."
                : "Needs about 270 MB of space. No admin rights needed.";
            Controls.Add(_status);

            Style(_cancel, "Cancel", false);
            _cancel.Location = new Point(316, 340);
            _cancel.Size = new Size(100, 36);
            _cancel.Click += (s, e) => Close();
            Controls.Add(_cancel);

            Style(_install, Installer.InstalledDir() != null ? "Update" : "Install", true);
            _install.Location = new Point(428, 340);
            _install.Size = new Size(100, 36);
            _install.Click += async (s, e) => await OnInstall();
            Controls.Add(_install);
            AcceptButton = _install;
            CancelButton = _cancel;

            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        private static void Style(Button b, string text, bool primary)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Blue : Stroke;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = primary ? Blue : Surface;
            b.ForeColor = Color.White;
            b.Cursor = Cursors.Hand;
            b.Font = new Font("Segoe UI Semibold", 10f);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose where to install Srv TV (a \"SrvTv\" folder is made inside it)" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var chosen = dlg.SelectedPath;
                _path.Text = string.Equals(Path.GetFileName(chosen.TrimEnd('\\')), "SrvTv", StringComparison.OrdinalIgnoreCase)
                    ? chosen
                    : Path.Combine(chosen, "SrvTv");
            }
        }

        private async Task OnInstall()
        {
            if (_done)
            {
                Close();
                return;
            }
            var dir = _path.Text.Trim();
            if (dir.Length == 0 || !Path.IsPathRooted(dir))
            {
                Program.Msg("Pick a full folder path, for example " + Installer.DefaultDir, MessageBoxIcon.Warning);
                return;
            }
            if (Installer.RunningCopies(dir).Length > 0 &&
                MessageBox.Show(this, "Srv TV is open. Close it and continue?", "Srv TV Setup",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            _busy = true;
            foreach (Control c in new Control[] { _install, _cancel, _browse, _path, _desktop, _launch }) c.Enabled = false;
            _bar.Visible = true;
            var desktop = _desktop.Checked;
            try
            {
                await Task.Run(() => Installer.Install(dir, desktop, (pct, msg) =>
                    BeginInvoke(new Action(() => { _bar.Value = Math.Min(100, pct); _status.Text = msg; }))));
                _done = true;
                _status.ForeColor = Sky;
                _status.Text = "Srv TV is installed. Find it in the Start menu.";
                _install.Text = "Finish";
                _install.Enabled = true;
                if (_launch.Checked)
                {
                    try { Process.Start(new ProcessStartInfo(Path.Combine(dir, Installer.ExeName)) { WorkingDirectory = dir }); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.FromArgb(0xFF, 0x8A, 0x80);
                _status.Text = "Setup failed: " + ex.Message;
                foreach (Control c in new Control[] { _install, _cancel, _browse, _path, _desktop, _launch }) c.Enabled = true;
            }
            finally { _busy = false; }
        }
    }
}
