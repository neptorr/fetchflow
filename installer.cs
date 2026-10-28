using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class SetupProgram
{
    private const string AppName = "Fetchflow";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fetchflow";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string InstallFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingName, string newName, int flags);

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0 && String.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase)) Uninstall();
        else Application.Run(new SetupForm());
    }

    private static void Install(bool launch)
    {
        Directory.CreateDirectory(InstallFolder);
        string appPath = Path.Combine(InstallFolder, "Fetchflow.exe");
        string setupPath = Path.Combine(InstallFolder, "FetchflowSetup.exe");
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Fetchflow.Payload.exe"))
        {
            if (payload == null) throw new InvalidOperationException("The Fetchflow app is missing from this installer.");
            using (FileStream output = File.Create(appPath)) payload.CopyTo(output);
        }
        string sourceSetup = Application.ExecutablePath;
        if (!String.Equals(Path.GetFullPath(sourceSetup), Path.GetFullPath(setupPath), StringComparison.OrdinalIgnoreCase)) File.Copy(sourceSetup, setupPath, true);

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string appMenu = Path.Combine(programs, AppName);
        Directory.CreateDirectory(appMenu);
        CreateShortcut(Path.Combine(appMenu, AppName + ".lnk"), appPath, InstallFolder);

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            if (key == null) throw new InvalidOperationException("Could not register the uninstaller.");
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "Fetchflow");
            key.SetValue("InstallLocation", InstallFolder);
            key.SetValue("DisplayIcon", appPath);
            key.SetValue("UninstallString", "\"" + setupPath + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (launch) Process.Start(appPath);
    }

    private static void Uninstall()
    {
        DialogResult answer = MessageBox.Show("Remove Fetchflow from this device? Your download queue and preferences will be kept.", "Uninstall Fetchflow", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        foreach (Process process in Process.GetProcessesByName("Fetchflow"))
        {
            try
            {
                if (!process.HasExited)
                {
                    MessageBox.Show("Exit Fetchflow from the notification area, then run uninstall again.", "Fetchflow is open", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            finally { process.Dispose(); }
        }
        try
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(StartupKey, true)) if (run != null) run.DeleteValue(AppName, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
            string shortcut = Path.Combine(menu, AppName + ".lnk");
            if (File.Exists(shortcut)) File.Delete(shortcut);
            if (Directory.Exists(menu) && Directory.GetFileSystemEntries(menu).Length == 0) Directory.Delete(menu);
            string app = Path.Combine(InstallFolder, "Fetchflow.exe");
            string setup = Path.Combine(InstallFolder, "FetchflowSetup.exe");
            if (File.Exists(app)) File.Delete(app);
            MoveFileEx(setup, null, 4);
            MoveFileEx(InstallFolder, null, 4);
            MessageBox.Show("Fetchflow was removed. Your queue and preferences were kept.", "Uninstall Fetchflow", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Uninstall Fetchflow", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void CreateShortcut(string path, string target, string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("Windows shortcut support is unavailable.");
        object shell = Activator.CreateInstance(shellType);
        object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        Type linkType = link.GetType();
        linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
        linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDirectory });
        linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Fetchflow download manager" });
        linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        Marshal.FinalReleaseComObject(link);
        Marshal.FinalReleaseComObject(shell);
    }

    private sealed class SetupForm : Form
    {
        private readonly CheckBox launchBox;
        private readonly Button installButton;

        public SetupForm()
        {
            Text = "Fetchflow Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(510, 245);
            Font = new System.Drawing.Font("Segoe UI", 9F);
            BackColor = System.Drawing.Color.FromArgb(18, 26, 35);
            ForeColor = System.Drawing.Color.FromArgb(224, 232, 237);

            Label title = new Label { Text = "Install Fetchflow", Font = new System.Drawing.Font("Segoe UI Semibold", 17F), AutoSize = true, Location = new System.Drawing.Point(28, 24), ForeColor = System.Drawing.Color.FromArgb(197, 237, 216) };
            Label intro = new Label { Text = "A direct-download manager for Windows. Your queue and preferences stay in your user profile.", AutoSize = false, Size = new System.Drawing.Size(450, 42), Location = new System.Drawing.Point(30, 70) };
            Label destination = new Label { Text = "Install for this Windows account:", AutoSize = true, Location = new System.Drawing.Point(30, 124) };
            Label folder = new Label { Text = InstallFolder, AutoSize = false, Size = new System.Drawing.Size(450, 24), Location = new System.Drawing.Point(30, 148), ForeColor = System.Drawing.Color.FromArgb(161, 181, 192) };
            launchBox = new CheckBox { Text = "Open Fetchflow after installation", Checked = true, AutoSize = true, Location = new System.Drawing.Point(30, 185), ForeColor = ForeColor };
            installButton = new Button { Text = "Install", Size = new System.Drawing.Size(112, 34), Location = new System.Drawing.Point(368, 183), FlatStyle = FlatStyle.Flat, BackColor = System.Drawing.Color.FromArgb(156, 220, 184), ForeColor = System.Drawing.Color.FromArgb(16, 32, 25) };
            installButton.Click += InstallClicked;
            Controls.AddRange(new Control[] { title, intro, destination, folder, launchBox, installButton });
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            installButton.Enabled = false;
            try
            {
                Install(launchBox.Checked);
                MessageBox.Show("Fetchflow is installed. You can also launch it from the Start menu.", "Fetchflow Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception error)
            {
                installButton.Enabled = true;
                MessageBox.Show(error.Message, "Fetchflow Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
