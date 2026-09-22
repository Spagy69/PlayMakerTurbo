using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PlayMakerTurboInstaller
{
    internal class MainForm : Form
    {
        private readonly TextBox pathBox = new TextBox();
        private readonly Label statusLabel = new Label();
        private readonly TextBox logBox = new TextBox();
        private readonly Button installButton = new Button();
        private readonly Button uninstallButton = new Button();

        public MainForm()
        {
            Text = "PlayMaker Turbo for My Winter Car (beta)";
            Size = new Size(720, 510);
            MinimumSize = new Size(560, 430);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            Label betaLabel = new Label
            {
                Text = "BETA. This patches game files and may break the game or your save. Back up your save before playing. No warranty, use at your own risk.",
                Location = new Point(12, 10),
                Size = new Size(668, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(255, 243, 205),
                ForeColor = Color.FromArgb(102, 77, 3),
                Padding = new Padding(6, 3, 6, 3),
            };

            Label pathLabel = new Label
            {
                Text = "Game folder",
                AutoSize = true,
                Location = new Point(12, 59),
            };

            pathBox.Location = new Point(12, 78);
            pathBox.Width = 560;
            pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pathBox.TextChanged += (s, e) => RefreshStatus();

            Button browseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(580, 76),
                Width = 100,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            browseButton.Click += Browse;

            statusLabel.Location = new Point(12, 110);
            statusLabel.AutoSize = true;

            installButton.Text = "Install";
            installButton.Location = new Point(12, 136);
            installButton.Size = new Size(140, 32);
            installButton.Click += (s, e) =>
            {
                if (ConfirmBeta())
                    Run(Installer.Install, "Installed. Start the game.");
            };

            uninstallButton.Text = "Restore original game";
            uninstallButton.Location = new Point(162, 136);
            uninstallButton.Size = new Size(160, 32);
            uninstallButton.Click += (s, e) => Run(Installer.Uninstall, "The game is back to its original state.");

            logBox.Location = new Point(12, 180);
            logBox.Size = new Size(668, 270);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;

            Controls.Add(betaLabel);
            Controls.Add(pathLabel);
            Controls.Add(pathBox);
            Controls.Add(browseButton);
            Controls.Add(statusLabel);
            Controls.Add(installButton);
            Controls.Add(uninstallButton);
            Controls.Add(logBox);

            string found = GameLocator.FindManaged();
            if (found != null)
            {
                pathBox.Text = found;
                Log("Game found through Steam.");
            }
            else
            {
                Log("Could not find the game. Use Browse and pick the My Winter Car folder.");
            }
            RefreshStatus();
        }

        private bool ConfirmBeta()
        {
            return MessageBox.Show(this, Installer.BetaNotice + Environment.NewLine + Environment.NewLine + "Install anyway?",
                "Beta software", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void Browse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Pick the folder that holds mywintercar.exe";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string managed = GameLocator.ToManaged(dialog.SelectedPath);
                if (managed == null)
                {
                    Log("No PlayMaker.dll under " + dialog.SelectedPath + ". Pick the folder that holds mywintercar.exe.");
                    return;
                }
                pathBox.Text = managed;
            }
        }

        private void Run(Action<string, Action<string>> action, string doneMessage)
        {
            string managed = pathBox.Text.Trim();
            if (!Directory.Exists(managed) || !File.Exists(Path.Combine(managed, "PlayMaker.dll")))
            {
                Log("Pick the game folder first.");
                return;
            }
            if (Installer.GameRunning())
            {
                Log("Close the game first, its files are locked.");
                return;
            }

            try
            {
                action(managed, Log);
                Log(doneMessage);
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            string managed = pathBox.Text.Trim();
            bool valid = Directory.Exists(managed) && File.Exists(Path.Combine(managed, "PlayMaker.dll"));
            statusLabel.Text = valid ? "Status: " + Installer.Status(managed) : "Status: no game folder selected";
            installButton.Enabled = valid;
            uninstallButton.Enabled = valid && File.Exists(Path.Combine(managed, "PlayMaker.dll.orig"));
        }

        private void Log(string message)
        {
            logBox.AppendText(message + Environment.NewLine);
        }
    }
}
