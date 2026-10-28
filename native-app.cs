using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[DataContract]
public sealed class NativeSettings
{
    [DataMember(Name="folder")] public string Folder { get; set; }
    [DataMember(Name="concurrency")] public int Concurrency { get; set; }
    [DataMember(Name="perHost")] public int PerHost { get; set; }
    [DataMember(Name="connectionsPerFile")] public int ConnectionsPerFile { get; set; }
    [DataMember(Name="speedLimitKiB")] public int SpeedLimitKiB { get; set; }
    [DataMember(Name="snailModeKiB")] public int SnailModeKiB { get; set; }
    [DataMember(Name="resumeAtLogin")] public bool ResumeAtLogin { get; set; }
}

[DataContract]
public sealed class NativeDownload
{
    [DataMember(Name="id")] public string Id { get; set; }
    [DataMember(Name="url")] public string Url { get; set; }
    [DataMember(Name="filename")] public string Filename { get; set; }
    [DataMember(Name="host")] public string Host { get; set; }
    [DataMember(Name="folder")] public string Folder { get; set; }
    [DataMember(Name="filePath")] public string FilePath { get; set; }
    [DataMember(Name="size")] public long Size { get; set; }
    [DataMember(Name="bytes")] public long Bytes { get; set; }
    [DataMember(Name="acceptsRanges")] public bool AcceptsRanges { get; set; }
    [DataMember(Name="status")] public string Status { get; set; }
    [DataMember(Name="error")] public string Error { get; set; }
    [DataMember(Name="addedAt")] public string AddedAt { get; set; }
    [DataMember(Name="etag")] public string ETag { get; set; }
    [DataMember(Name="lastModified")] public string LastModified { get; set; }
    [DataMember(Name="segmentedMode")] public bool SegmentedMode { get; set; }
    [DataMember(Name="completedRanges")] public List<long[]> CompletedRanges { get; set; }
    [IgnoreDataMember] public long CurrentSpeed;
    [IgnoreDataMember] public long SpeedSampleBytes;
    [IgnoreDataMember] public long SpeedLastSampleTimestamp;
}

[DataContract]
public sealed class NativeState
{
    [DataMember(Name="settings")] public NativeSettings Settings { get; set; }
    [DataMember(Name="items")] public List<NativeDownload> Items { get; set; }
}

public sealed class GrabResult
{
    public bool Selected { get; set; }
    public string Url { get; set; }
    public string Filename { get; set; }
    public string Host { get; set; }
    public long Size { get; set; }
    public bool Ranges { get; set; }
    public string Status { get; set; }
    public string Error { get; set; }
}

internal static class NativeProgram
{
    private const int SwRestore = 9;
    private static readonly uint ActivateMessage = RegisterWindowMessage("Fetchflow.ActivateExistingInstance");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    internal static uint ExistingInstanceMessage { get { return ActivateMessage; } }

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        string userId = WindowsIdentity.GetCurrent().User.Value;
        using (Mutex singleInstance = new Mutex(true, "Local\\Fetchflow." + userId, out createdNew))
        {
            if (!createdNew)
            {
                ActivateExistingInstance();
                return;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new NativeMainForm());
            }
            finally
            {
                singleInstance.ReleaseMutex();
            }
        }
    }

    private static void ActivateExistingInstance()
    {
        IntPtr window = FindWindow(null, "Fetchflow");
        if (window == IntPtr.Zero) return;
        SendMessage(window, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        ShowWindowAsync(window, SwRestore);
        SetForegroundWindow(window);
    }
}

public sealed class NativeMainForm : Form
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string appData = String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FETCHFLOW_DATA_DIR"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fetchflow")
        : Path.GetFullPath(Environment.GetEnvironmentVariable("FETCHFLOW_DATA_DIR"));
    private readonly string stateFile;
    private readonly string logFile;
    private readonly object saveLock = new object();
    private readonly object logLock = new object();
    private readonly object schedulerLock = new object();
    private readonly List<NativeDownload> downloads = new List<NativeDownload>();
    private readonly List<GrabResult> results = new List<GrabResult>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> transfers = new ConcurrentDictionary<string, CancellationTokenSource>();
    private readonly HashSet<string> running = new HashSet<string>();
    private readonly HashSet<string> selectedIds = new HashSet<string>();
    private readonly Dictionary<string, int> runningHosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private NativeSettings settings;
    private bool queueArmed;
    private bool closing;
    private bool closeRequested;
    private string contextDownloadId;

    private TabControl tabs;
    private DataGridView downloadGrid;
    private DataGridView grabGrid;
    private TextBox linksBox;
    private Label downloadSummary;
    private Label speedSummary;
    private Label selectedSummary;
    private Button selectAllButton;
    private Button startButton;
    private Button pauseAllButton;
    private Button snailButton;
    private Button removeSelectedButton;
    private Button deleteSelectedButton;
    private Button checkLinksButton;
    private Button checkInternetButton;
    private Button addSelectedButton;
    private RichTextBox logsBox;
    private Label internetStatusLabel;
    private TextBox folderBox;
    private NumericUpDown concurrencyBox;
    private NumericUpDown perHostBox;
    private NumericUpDown connectionsBox;
    private NumericUpDown speedLimitBox;
    private Label settingsMessage;
    private ContextMenuStrip downloadMenu;
    private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    private NotifyIcon trayIcon;
    private CheckBox resumeAtLoginBox;
    private bool exitRequested;
    private string stateLoadProblem;
    private bool stateRecoveredFromBackup;

    public NativeMainForm()
    {
        Text = "Fetchflow";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 650);
        Size = new Size(1180, 780);
        BackColor = Color.FromArgb(14, 20, 28);
        ForeColor = Color.FromArgb(220, 230, 235);
        Font = new Font("Segoe UI", 9F);
        stateFile = Path.Combine(appData, "state.json");
        logFile = Path.Combine(appData, "fetchflow.log");
        LoadState();
        BuildInterface();
        LoadLogHistory();
        WriteLog("INFO", "Fetchflow started. Settings and download queue: " + stateFile);
        if (stateLoadProblem != null)
        {
            string warning = stateLoadProblem;
            BeginInvoke((Action)delegate { MessageBox.Show(this, warning + " See the Logs tab for details. Your existing state files were kept.", "Fetchflow data recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
        }
        RenderDownloads();
        UpdateSnailButton();
        InitializeTrayIcon();
        refreshTimer.Interval = 800;
        refreshTimer.Tick += delegate { UpdateLiveRows(); };
        refreshTimer.Start();
        FormClosing += OnClosing;
        if (settings.ResumeAtLogin && Environment.GetCommandLineArgs().Any(arg => String.Equals(arg, "--resume-queue", StringComparison.OrdinalIgnoreCase)))
        {
            BeginInvoke((Action)delegate { StartQueue(); HideToTray(); });
        }
    }

    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == NativeProgram.ExistingInstanceMessage)
        {
            RestoreFromTray();
            return;
        }
        base.WndProc(ref message);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new HttpClientHandler();
        handler.AllowAutoRedirect = true;
        handler.AutomaticDecompression = DecompressionMethods.None;
        HttpClient client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Fetchflow/2.0");
        return client;
    }

    private void InitializeTrayIcon()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Open Fetchflow", null, delegate { RestoreFromTray(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Fetchflow", null, delegate { exitRequested = true; Close(); });
        trayIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Fetchflow is running", ContextMenuStrip = menu, Visible = false };
        trayIcon.DoubleClick += delegate { RestoreFromTray(); };
    }

    private void HideToTray()
    {
        if (trayIcon != null) trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (trayIcon != null) trayIcon.Visible = false;
    }

    private static void SetStartupRegistration(bool enabled)
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(runPath))
        {
            if (key == null) throw new InvalidOperationException("Could not update the Windows sign-in startup setting.");
            if (enabled) key.SetValue("Fetchflow", "\"" + Application.ExecutablePath + "\" --resume-queue", RegistryValueKind.String);
            else key.DeleteValue("Fetchflow", false);
        }
    }

    private void LoadState()
    {
        settings = new NativeSettings { Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Concurrency = 3, PerHost = 2, ConnectionsPerFile = 4, SpeedLimitKiB = 0, SnailModeKiB = 0 };
        Directory.CreateDirectory(appData);
        NativeState saved = null;
        string backupFile = stateFile + ".bak";
        if (File.Exists(stateFile))
        {
            try { saved = ReadStateFile(stateFile); }
            catch (Exception error) { WriteLog("ERROR", "Could not read saved settings/queue: " + error.Message); }
        }
        if (saved == null && File.Exists(backupFile))
        {
            try
            {
                saved = ReadStateFile(backupFile);
                stateRecoveredFromBackup = true;
                WriteLog("WARN", "Recovered settings and queue from the last-good backup: " + backupFile);
            }
            catch (Exception error) { WriteLog("ERROR", "Could not read the backup state file: " + error.Message); }
        }
        if (saved != null)
        {
            if (saved.Settings != null) settings = MergeSettings(settings, saved.Settings);
            if (saved.Items != null) downloads.AddRange(saved.Items.Where(i => i != null && !String.IsNullOrWhiteSpace(i.Id)));
        }
        else if (File.Exists(stateFile) || File.Exists(backupFile))
        {
            stateLoadProblem = "Fetchflow found saved data but could not read it. It started with default settings; the original files were preserved.";
            WriteLog("ERROR", stateLoadProblem);
        }
        foreach (NativeDownload item in downloads)
        {
            if (item.Status == "downloading") item.Status = "paused";
            if (item.Status == "paused") item.Error = "Paused when Fetchflow closed.";
            if (item.CompletedRanges == null) item.CompletedRanges = new List<long[]>();
        }
    }

    private static NativeState ReadStateFile(string path)
    {
        using (FileStream input = File.OpenRead(path))
        {
            NativeState state = (NativeState)new DataContractJsonSerializer(typeof(NativeState)).ReadObject(input);
            if (state == null) throw new InvalidDataException("The state file is empty.");
            return state;
        }
    }

    private static NativeSettings MergeSettings(NativeSettings defaults, NativeSettings loaded)
    {
        if (!String.IsNullOrWhiteSpace(loaded.Folder)) defaults.Folder = loaded.Folder;
        if (loaded.Concurrency > 0) defaults.Concurrency = Clamp(loaded.Concurrency, 1, 16);
        if (loaded.PerHost > 0) defaults.PerHost = Clamp(loaded.PerHost, 1, 16);
        if (loaded.ConnectionsPerFile > 0) defaults.ConnectionsPerFile = Clamp(loaded.ConnectionsPerFile, 1, 16);
        defaults.SpeedLimitKiB = Math.Max(0, loaded.SpeedLimitKiB);
        defaults.SnailModeKiB = loaded.SnailModeKiB == 10 || loaded.SnailModeKiB == 50 ? loaded.SnailModeKiB : 0;
        defaults.ResumeAtLogin = loaded.ResumeAtLogin;
        return defaults;
    }

    private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }

    private void SaveState()
    {
        if (InvokeRequired)
        {
            if (IsHandleCreated && !IsDisposed) Invoke((Action)SaveState);
            return;
        }
        lock (saveLock)
        {
            Directory.CreateDirectory(appData);
            NativeState state = new NativeState
            {
                Settings = new NativeSettings { Folder = settings.Folder, Concurrency = settings.Concurrency, PerHost = settings.PerHost, ConnectionsPerFile = settings.ConnectionsPerFile, SpeedLimitKiB = settings.SpeedLimitKiB, SnailModeKiB = settings.SnailModeKiB, ResumeAtLogin = settings.ResumeAtLogin },
                Items = downloads.Select(item =>
                {
                    lock (item)
                    {
                        return new NativeDownload { Id = item.Id, Url = item.Url, Filename = item.Filename, Host = item.Host, Folder = item.Folder, FilePath = item.FilePath, Size = item.Size, Bytes = item.Bytes, AcceptsRanges = item.AcceptsRanges, Status = item.Status, Error = item.Error, AddedAt = item.AddedAt, ETag = item.ETag, LastModified = item.LastModified, SegmentedMode = item.SegmentedMode, CompletedRanges = (item.CompletedRanges ?? new List<long[]>()).Select(r => new long[] { r[0], r[1] }).ToList() };
                    }
                }).ToList()
            };
            string temp = stateFile + ".tmp";
            using (FileStream output = File.Create(temp)) new DataContractJsonSerializer(typeof(NativeState)).WriteObject(output, state);
            if (File.Exists(stateFile))
            {
                if (!stateRecoveredFromBackup) File.Copy(stateFile, stateFile + ".bak", true);
                File.Replace(temp, stateFile, null);
                stateRecoveredFromBackup = false;
            }
            else File.Move(temp, stateFile);
        }
    }

    private void BuildInterface()
    {
        TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Padding = new Padding(18, 12, 18, 10) };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        Panel header = new Panel { Dock = DockStyle.Fill };
        Label logo = new Label { Text = "FETCHFLOW", Font = new Font("Segoe UI Semibold", 17F), ForeColor = Color.FromArgb(194, 239, 215), AutoSize = true, Location = new Point(2, 7) };
        Label local = new Label { Text = "WINDOWS APP · PRIVATE BY DEFAULT", Font = new Font("Consolas", 8F), ForeColor = Color.FromArgb(121, 141, 153), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Resize += delegate { local.Location = new Point(header.Width - local.Width - 4, 17); };
        header.Controls.Add(logo); header.Controls.Add(local);
        shell.Controls.Add(header, 0, 0);

        tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F) };
        TabPage downloadsPage = new TabPage("Downloads") { BackColor = Color.FromArgb(17, 25, 34), ForeColor = ForeColor };
        TabPage grabPage = new TabPage("LinkGrabber") { BackColor = Color.FromArgb(17, 25, 34), ForeColor = ForeColor };
        TabPage settingsPage = new TabPage("Settings") { BackColor = Color.FromArgb(17, 25, 34), ForeColor = ForeColor };
        TabPage logsPage = new TabPage("Logs") { BackColor = Color.FromArgb(17, 25, 34), ForeColor = ForeColor };
        tabs.TabPages.Add(downloadsPage); tabs.TabPages.Add(grabPage); tabs.TabPages.Add(settingsPage); tabs.TabPages.Add(logsPage);
        shell.Controls.Add(tabs, 0, 1);
        BuildDownloadsPage(downloadsPage);
        BuildGrabberPage(grabPage);
        BuildSettingsPage(settingsPage);
        BuildLogsPage(logsPage);
        BuildContextMenu();
    }

    private void BuildLogsPage(TabPage page)
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);

        FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        Label location = new Label { Text = "Logs saved in %LOCALAPPDATA%\\Fetchflow", AutoSize = true, ForeColor = Color.FromArgb(150, 169, 180), Padding = new Padding(3, 10, 8, 0) };
        checkInternetButton = MakeButton("Check internet", async delegate { await CheckInternetAccess(); }, true);
        internetStatusLabel = new Label { Text = "Internet status not checked", AutoSize = true, ForeColor = Color.FromArgb(150, 169, 180), Padding = new Padding(8, 10, 8, 0) };
        Button copy = MakeButton("Copy logs", delegate { if (logsBox != null && logsBox.TextLength > 0) Clipboard.SetText(logsBox.Text); }, false);
        Button clear = MakeButton("Clear logs", delegate { ClearLogs(); }, false);
        toolbar.Controls.Add(checkInternetButton); toolbar.Controls.Add(internetStatusLabel); toolbar.Controls.Add(copy); toolbar.Controls.Add(clear); toolbar.Controls.Add(location);
        layout.Controls.Add(toolbar, 0, 0);

        logsBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BackColor = Color.FromArgb(12, 18, 24), ForeColor = Color.FromArgb(205, 218, 225), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F), WordWrap = false };
        layout.Controls.Add(logsBox, 0, 1);
    }

    private void LoadLogHistory()
    {
        try
        {
            if (!File.Exists(logFile)) return;
            string[] lines = File.ReadAllLines(logFile);
            int first = Math.Max(0, lines.Length - 1000);
            for (int i = first; i < lines.Length; i++) AppendLogLine(lines[i]);
        }
        catch { }
    }

    private void WriteLog(string level, string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message;
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(appData);
                File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke((Action)delegate { AppendLogLine(line); });
        else AppendLogLine(line);
    }

    private void AppendLogLine(string line)
    {
        if (logsBox == null || logsBox.IsDisposed) return;
        logsBox.AppendText(line + Environment.NewLine);
        logsBox.SelectionStart = logsBox.TextLength;
        logsBox.ScrollToCaret();
    }

    private void ClearLogs()
    {
        if (logsBox != null) logsBox.Clear();
        lock (logLock)
        {
            try { File.WriteAllText(logFile, String.Empty, Encoding.UTF8); }
            catch (Exception error) { WriteLog("ERROR", "Could not clear the log file: " + error.Message); }
        }
    }

    private async Task CheckInternetAccess()
    {
        Uri checkUri = new Uri("https://www.microsoft.com/");
        checkInternetButton.Enabled = false;
        internetStatusLabel.Text = "Checking HTTPS access…";
        Stopwatch watch = Stopwatch.StartNew();
        bool timedOut = false;
        string failure = null;
        WriteLog("INFO", "Checking general internet access over HTTPS.");
        try
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, checkUri))
            using (HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                internetStatusLabel.Text = "HTTPS reachable · HTTP " + (int)response.StatusCode;
                WriteLog("INFO", "Internet check succeeded: " + checkUri.Host + " returned HTTP " + (int)response.StatusCode + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " seconds.");
            }
        }
        catch (OperationCanceledException) { timedOut = true; }
        catch (Exception error) { failure = "Internet check failed (" + error.GetType().Name + "): " + error.Message; }

        if (timedOut)
        {
            string detail = await DiagnoseHost(checkUri);
            internetStatusLabel.Text = "HTTPS check timed out";
            WriteLog("WARN", "Internet check timed out after " + watch.Elapsed.TotalSeconds.ToString("0") + " seconds. " + detail);
        }
        else if (failure != null)
        {
            internetStatusLabel.Text = "HTTPS check failed";
            WriteLog("ERROR", failure);
        }
        checkInternetButton.Enabled = true;
    }

    private Button MakeButton(string text, EventHandler click, bool primary)
    {
        Button button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 2, 10, 2), BackColor = primary ? Color.FromArgb(46, 110, 77) : Color.FromArgb(31, 44, 56), ForeColor = primary ? Color.White : Color.FromArgb(208, 220, 227), Margin = new Padding(4) };
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(74, 145, 108) : Color.FromArgb(54, 69, 82);
        button.Click += click;
        return button;
    }

    private void BuildDownloadsPage(TabPage page)
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.Controls.Add(layout);

        FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight };
        startButton = MakeButton("▶  Start queue", delegate { StartQueue(); }, true);
        pauseAllButton = MakeButton("Ⅱ  Pause all", delegate { PauseAll(); }, false);
        snailButton = MakeButton("🐌 Snail: Off", delegate { CycleSnail(); }, false);
        actions.Controls.Add(startButton); actions.Controls.Add(pauseAllButton); actions.Controls.Add(snailButton);
        selectedSummary = new Label { Text = "0 selected", ForeColor = Color.FromArgb(139, 158, 170), AutoSize = true, Padding = new Padding(10, 12, 4, 0) };
        actions.Controls.Add(selectedSummary);
        removeSelectedButton = MakeButton("Remove selected", delegate { RemoveSelected(false); }, false);
        deleteSelectedButton = MakeButton("Delete files…", delegate { RemoveSelected(true); }, false);
        actions.Controls.Add(removeSelectedButton); actions.Controls.Add(deleteSelectedButton);
        layout.Controls.Add(actions, 0, 0);

        FlowLayoutPanel summaryPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        downloadSummary = new Label { Text = "0 downloads", ForeColor = Color.FromArgb(142, 160, 171), AutoSize = true, Padding = new Padding(2, 8, 18, 0) };
        speedSummary = new Label { Text = "0 B/s", ForeColor = Color.FromArgb(151, 212, 177), AutoSize = true, Padding = new Padding(2, 8, 18, 0) };
        selectAllButton = new Button { Text = "Select all", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = Color.FromArgb(176, 192, 201), FlatAppearance = { BorderSize = 0 } };
        selectAllButton.Click += delegate { bool select = selectedIds.Count != downloads.Count; selectedIds.Clear(); if (select) foreach (NativeDownload item in downloads) selectedIds.Add(item.Id); RenderDownloads(); };
        summaryPanel.Controls.Add(downloadSummary); summaryPanel.Controls.Add(speedSummary); summaryPanel.Controls.Add(selectAllButton);
        layout.Controls.Add(summaryPanel, 0, 1);

        downloadGrid = CreateGrid();
        downloadGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "✓", Width = 35, DataPropertyName = "Selected" });
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Filename", HeaderText = "FILE NAME", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "SIZE", Width = 100 });
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "SOURCE", Width = 140 });
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "STATUS", Width = 115 });
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Progress", HeaderText = "PROGRESS", Width = 180 });
        downloadGrid.CellMouseDown += DownloadGridCellMouseDown;
        downloadGrid.CurrentCellDirtyStateChanged += delegate { if (downloadGrid.IsCurrentCellDirty) downloadGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        downloadGrid.CellValueChanged += DownloadGridCellValueChanged;
        layout.Controls.Add(downloadGrid, 0, 2);
        Label note = new Label { Text = "Right-click a download for pause, resume, refresh, remove, and delete actions.", AutoSize = true, ForeColor = Color.FromArgb(108, 127, 139), Padding = new Padding(3, 8, 0, 0) };
        layout.Controls.Add(note, 0, 3);
    }

    private DataGridView CreateGrid()
    {
        DataGridView grid = new DataGridView();
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.FromArgb(17, 25, 34);
        grid.GridColor = Color.FromArgb(39, 53, 65);
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(27, 39, 50);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(149, 166, 177);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Consolas", 8F, FontStyle.Bold);
        grid.ColumnHeadersHeight = 34;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(19, 28, 37);
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(218, 228, 233);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 62, 53);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.RowTemplate.Height = 42;
        return grid;
    }

    private void BuildGrabberPage(TabPage page)
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);
        Label instruction = new Label { Text = "Paste one direct HTTP or HTTPS file link per line", ForeColor = Color.FromArgb(174, 192, 200), AutoSize = true, Padding = new Padding(2, 8, 0, 0) };
        layout.Controls.Add(instruction, 0, 0);
        linksBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BackColor = Color.FromArgb(12, 18, 24), ForeColor = Color.FromArgb(215, 226, 231), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F) };
        layout.Controls.Add(linksBox, 0, 1);
        FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        checkLinksButton = MakeButton("⌕  Check links", async delegate { await CheckLinks(); }, true);
        addSelectedButton = MakeButton("＋ Add selected to queue", delegate { AddSelectedLinks(); }, false);
        addSelectedButton.Enabled = false;
        Button clear = MakeButton("Clear", delegate { linksBox.Clear(); results.Clear(); RenderGrabber(); }, false);
        bar.Controls.Add(checkLinksButton); bar.Controls.Add(addSelectedButton); bar.Controls.Add(clear);
        layout.Controls.Add(bar, 0, 2);
        grabGrid = CreateGrid();
        grabGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "✓", Width = 35 });
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Filename", HeaderText = "FILE NAME", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "HOST", Width = 150 });
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "SIZE", Width = 100 });
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Resume", HeaderText = "RESUME", Width = 90 });
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "STATUS", Width = 120 });
        grabGrid.CellValueChanged += GrabGridCellValueChanged;
        grabGrid.CurrentCellDirtyStateChanged += delegate { if (grabGrid.IsCurrentCellDirty) grabGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        layout.Controls.Add(grabGrid, 0, 3);
    }

    private void BuildSettingsPage(TabPage page)
    {
        TableLayoutPanel form = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 8, Padding = new Padding(20), Width = 740, AutoSize = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        page.Controls.Add(form);
        AddSettingLabel(form, 0, "Download folder");
        FlowLayoutPanel folderPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        folderBox = new TextBox { Width = 420, Text = settings.Folder, BackColor = Color.FromArgb(12, 18, 24), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
        Button browse = MakeButton("Browse…", delegate { using (FolderBrowserDialog dlg = new FolderBrowserDialog { SelectedPath = folderBox.Text }) if (dlg.ShowDialog(this) == DialogResult.OK) folderBox.Text = dlg.SelectedPath; }, false);
        folderPanel.Controls.Add(folderBox); folderPanel.Controls.Add(browse); form.Controls.Add(folderPanel, 1, 0);
        AddSettingLabel(form, 1, "Simultaneous downloads"); concurrencyBox = AddNumber(form, 1, settings.Concurrency, 1, 16);
        AddSettingLabel(form, 2, "Per-host limit"); perHostBox = AddNumber(form, 2, settings.PerHost, 1, 16);
        AddSettingLabel(form, 3, "Connections per file"); connectionsBox = AddNumber(form, 3, settings.ConnectionsPerFile, 1, 16);
        AddSettingLabel(form, 4, "Regular speed cap (KB/s; 0 = unlimited)"); speedLimitBox = AddNumber(form, 4, settings.SpeedLimitKiB, 0, 1000000);
        resumeAtLoginBox = new CheckBox { Text = "Start with Windows and resume queued downloads at sign-in", Checked = settings.ResumeAtLogin, AutoSize = true, ForeColor = Color.FromArgb(188, 202, 210), Padding = new Padding(0, 8, 0, 8) };
        form.Controls.Add(resumeAtLoginBox, 0, 5); form.SetColumnSpan(resumeAtLoginBox, 2);
        settingsMessage = new Label { Text = "Settings are saved on this device.", AutoSize = true, ForeColor = Color.FromArgb(144, 165, 176), Padding = new Padding(0, 10, 0, 0) };
        form.Controls.Add(settingsMessage, 0, 6);
        Button save = MakeButton("Save preferences", delegate { SaveSettingsFromForm(); }, true);
        form.Controls.Add(save, 1, 6);
        Label about = new Label { Text = "Fetchflow is a Windows desktop app. Downloads connect directly from this device.", ForeColor = Color.FromArgb(120, 143, 154), AutoSize = true, Padding = new Padding(0, 16, 0, 0) };
        form.Controls.Add(about, 0, 7); form.SetColumnSpan(about, 2);
    }

    private void AddSettingLabel(TableLayoutPanel form, int row, string text)
    {
        Label label = new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(188, 202, 210), Padding = new Padding(0, 12, 0, 0) };
        form.Controls.Add(label, 0, row);
    }

    private NumericUpDown AddNumber(TableLayoutPanel form, int row, int value, int min, int max)
    {
        NumericUpDown input = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 110, BackColor = Color.FromArgb(12, 18, 24), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, ThousandsSeparator = true };
        form.Controls.Add(input, 1, row);
        return input;
    }

    private void BuildContextMenu()
    {
        downloadMenu = new ContextMenuStrip();
        downloadMenu.Items.Add("Pause", null, delegate { RunContextAction("pause"); });
        downloadMenu.Items.Add("Resume", null, delegate { RunContextAction("resume"); });
        downloadMenu.Items.Add("Refresh link and resume…", null, async delegate { await RefreshLink(contextDownloadId); });
        downloadMenu.Items.Add(new ToolStripSeparator());
        downloadMenu.Items.Add("Remove from list (keep file)", null, delegate { RunContextAction("remove"); });
        downloadMenu.Items.Add("Delete download and file…", null, delegate { RunContextAction("delete"); });
        downloadGrid.ContextMenuStrip = downloadMenu;
        downloadMenu.Opening += delegate(object sender, CancelEventArgs e)
        {
            NativeDownload item = downloads.FirstOrDefault(d => d.Id == contextDownloadId);
            if (item == null) { e.Cancel = true; return; }
            downloadMenu.Items[0].Enabled = item.Status == "downloading" || item.Status == "queued";
            downloadMenu.Items[1].Enabled = item.Status == "paused" || item.Status == "error";
        };
    }

    private void DownloadGridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.Button != MouseButtons.Right) return;
        downloadGrid.ClearSelection();
        downloadGrid.Rows[e.RowIndex].Selected = true;
        NativeDownload item = downloadGrid.Rows[e.RowIndex].Tag as NativeDownload;
        contextDownloadId = item == null ? null : item.Id;
    }

    private void DownloadGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
        NativeDownload item = downloadGrid.Rows[e.RowIndex].Tag as NativeDownload;
        if (item == null) return;
        if (Convert.ToBoolean(downloadGrid.Rows[e.RowIndex].Cells[0].Value)) selectedIds.Add(item.Id); else selectedIds.Remove(item.Id);
        UpdateSelectionUi();
    }

    private void GrabGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
        results[e.RowIndex].Selected = Convert.ToBoolean(grabGrid.Rows[e.RowIndex].Cells[0].Value);
        addSelectedButton.Enabled = results.Any(r => r.Selected && String.IsNullOrEmpty(r.Error));
    }

    private void RenderDownloads()
    {
        if (downloadGrid == null) return;
        downloadGrid.Rows.Clear();
        foreach (NativeDownload item in downloads)
        {
            int row = downloadGrid.Rows.Add(selectedIds.Contains(item.Id), item.Filename, FormatBytes(item.Size), item.Host, StatusText(item), ProgressText(item));
            downloadGrid.Rows[row].Tag = item;
            downloadGrid.Rows[row].Cells[1].ToolTipText = item.Error ?? item.Url;
        }
        downloadSummary.Text = downloads.Count + " download" + (downloads.Count == 1 ? "" : "s");
        UpdateSelectionUi();
        UpdateLiveRows();
    }

    private void UpdateLiveRows()
    {
        if (IsDisposed || downloadGrid == null) return;
        long totalSpeed = 0;
        for (int i = 0; i < downloadGrid.Rows.Count; i++)
        {
            NativeDownload item = downloadGrid.Rows[i].Tag as NativeDownload;
            if (item == null) continue;
            downloadGrid.Rows[i].Cells[2].Value = FormatBytes(item.Size);
            downloadGrid.Rows[i].Cells[4].Value = StatusText(item);
            downloadGrid.Rows[i].Cells[5].Value = ProgressText(item);
            totalSpeed += Math.Max(0, SampleCurrentSpeed(item));
        }
        speedSummary.Text = FormatBytes(totalSpeed) + "/s";
        UpdateSelectionUi();
    }

    private static void RecordSpeedBytes(NativeDownload item, int bytes)
    {
        Interlocked.Add(ref item.SpeedSampleBytes, bytes);
    }

    private static long SampleCurrentSpeed(NativeDownload item)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref item.SpeedLastSampleTimestamp, now);
        if (previous == 0) return Interlocked.Read(ref item.CurrentSpeed);
        long elapsedTicks = now - previous;
        long transferred = Interlocked.Exchange(ref item.SpeedSampleBytes, 0);
        if (elapsedTicks <= 0) return Interlocked.Read(ref item.CurrentSpeed);
        long bytesPerSecond = (long)(transferred * (double)Stopwatch.Frequency / elapsedTicks);
        Interlocked.Exchange(ref item.CurrentSpeed, Math.Max(0, bytesPerSecond));
        return Interlocked.Read(ref item.CurrentSpeed);
    }

    private void UpdateSelectionUi()
    {
        if (selectedSummary == null) return;
        selectedSummary.Text = selectedIds.Count + " selected";
        removeSelectedButton.Enabled = selectedIds.Count > 0;
        deleteSelectedButton.Enabled = selectedIds.Any(id => downloads.Any(d => d.Id == id && !String.IsNullOrWhiteSpace(d.FilePath)));
        selectAllButton.Text = selectedIds.Count == downloads.Count && downloads.Count > 0 ? "Deselect all" : "Select all";
    }

    private void RenderGrabber()
    {
        if (grabGrid == null) return;
        grabGrid.Rows.Clear();
        foreach (GrabResult result in results)
        {
            int row = grabGrid.Rows.Add(result.Selected && String.IsNullOrEmpty(result.Error), result.Filename, result.Host, result.Size > 0 ? FormatBytes(result.Size) : "Unknown", result.Ranges ? "Supported" : "Unknown", String.IsNullOrEmpty(result.Error) ? "Online" : result.Error);
            grabGrid.Rows[row].Tag = result;
        }
        addSelectedButton.Enabled = results.Any(r => r.Selected && String.IsNullOrEmpty(r.Error));
    }

    private void UpdateSnailButton()
    {
        if (snailButton == null) return;
        snailButton.Text = settings.SnailModeKiB == 0 ? "🐌 Snail: Off" : "🐌 Snail: " + settings.SnailModeKiB + " KB/s";
        snailButton.BackColor = settings.SnailModeKiB == 0 ? Color.FromArgb(31, 44, 56) : Color.FromArgb(35, 62, 46);
    }

    private void CycleSnail()
    {
        settings.SnailModeKiB = settings.SnailModeKiB == 0 ? 10 : settings.SnailModeKiB == 10 ? 50 : 0;
        UpdateSnailButton(); SaveState();
    }

    private void SaveSettingsFromForm()
    {
        try
        {
            string folder = folderBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) throw new InvalidOperationException("Choose an absolute download folder.");
            Directory.CreateDirectory(folder);
            settings.Folder = folder;
            settings.Concurrency = (int)concurrencyBox.Value;
            settings.PerHost = (int)perHostBox.Value;
            settings.ConnectionsPerFile = (int)connectionsBox.Value;
            settings.SpeedLimitKiB = (int)speedLimitBox.Value;
            SetStartupRegistration(resumeAtLoginBox.Checked);
            settings.ResumeAtLogin = resumeAtLoginBox.Checked;
            SaveState();
            settingsMessage.Text = settings.ResumeAtLogin ? "Preferences saved. Fetchflow will resume queued downloads when you sign in." : "Preferences saved on this device.";
        }
        catch (Exception error) { settingsMessage.Text = error.Message; }
    }

    private void StartQueue()
    {
        queueArmed = true;
        foreach (NativeDownload item in downloads) if (item.Status == "paused") { item.Status = "queued"; item.Error = ""; }
        SaveState();
        PumpQueue();
        RenderDownloads();
    }

    private void PauseAll()
    {
        queueArmed = false;
        foreach (NativeDownload item in downloads)
        {
            if (item.Status == "queued") item.Status = "paused";
            CancellationTokenSource token;
            if (transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        }
        SaveState(); RenderDownloads();
    }

    private void PumpQueue()
    {
        List<NativeDownload> start = new List<NativeDownload>();
        lock (schedulerLock)
        {
            if (!queueArmed || closing) return;
            while (running.Count < settings.Concurrency)
            {
                NativeDownload next = downloads.FirstOrDefault(d => d.Status == "queued" && (!runningHosts.ContainsKey(d.Host) || runningHosts[d.Host] < settings.PerHost));
                if (next == null) break;
                next.Status = "downloading"; next.Error = "";
                running.Add(next.Id); AddRunningHost(next.Host, 1);
                CancellationTokenSource token = new CancellationTokenSource();
                transfers[next.Id] = token;
                start.Add(next);
            }
        }
        SaveState();
        foreach (NativeDownload item in start) Task.Run(() => Transfer(item, transfers[item.Id].Token)).ContinueWith(task =>
        {
            if (!IsDisposed) BeginInvoke((Action)(() => CompleteTransfer(item, task)));
        });
        RenderDownloads();
    }

    private void CompleteTransfer(NativeDownload item, Task task)
    {
        lock (schedulerLock) { running.Remove(item.Id); AddRunningHost(item.Host, -1); }
        CancellationTokenSource token; if (transfers.TryRemove(item.Id, out token)) token.Dispose();
        if (task.IsCanceled || task.Exception != null)
        {
            if (item.Status != "paused") { item.Status = "error"; item.Error = task.Exception == null ? "Download canceled." : task.Exception.GetBaseException().Message; }
        }
        else { item.Status = "finished"; item.Error = ""; item.Bytes = item.Size; item.CompletedRanges = new List<long[]>(); item.SegmentedMode = false; }
        SaveState(); RenderDownloads();
        PumpQueue();
    }

    private async Task Transfer(NativeDownload item, CancellationToken cancel)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(item.Folder)) item.Folder = settings.Folder;
            if (String.IsNullOrWhiteSpace(item.FilePath)) item.FilePath = ChoosePath(item.Folder, item.Filename);
            string part = item.FilePath + ".fetchflow-part";
            if (await TransferSegments(item, part, cancel))
            {
                FinalizeFile(item, part);
                return;
            }
            await TransferSingle(item, part, cancel);
            FinalizeFile(item, part);
        }
        catch (OperationCanceledException) { item.Status = "paused"; item.Error = ""; throw; }
        catch (Exception error) { item.Status = "error"; item.Error = error.Message; throw; }
        finally { SaveState(); }
    }

    private async Task<bool> TransferSegments(NativeDownload item, string part, CancellationToken cancel)
    {
        int connections = Clamp(settings.ConnectionsPerFile, 1, 16);
        if ((connections < 2 && !item.SegmentedMode && (item.CompletedRanges == null || item.CompletedRanges.Count == 0)) || !item.AcceptsRanges || item.Size <= 0) return false;
        List<long[]> ranges = item.CompletedRanges ?? new List<long[]>();
        if (ranges.Count == 0 && !item.SegmentedMode && File.Exists(part))
        {
            long existing = Math.Min(item.Size, new FileInfo(part).Length);
            if (existing > 0) ranges.Add(new long[] { 0, existing - 1 });
        }
        using (HttpResponseMessage probe = await GetRange(item.Url, 0, 0, null, cancel))
        {
            ContentRangeHeaderValue contentRange = probe.Content.Headers.ContentRange;
            if (probe.StatusCode != HttpStatusCode.PartialContent || contentRange == null || contentRange.From != 0 || contentRange.To != 0 || contentRange.Length != item.Size)
            {
                if (item.SegmentedMode || ranges.Count > 0) throw new IOException("This server no longer supports safe ranged resume. The partial file was kept.");
                return false;
            }
            string tag = probe.Headers.ETag == null ? "" : probe.Headers.ETag.ToString();
            string modified = probe.Content.Headers.LastModified.HasValue ? probe.Content.Headers.LastModified.Value.ToString("R") : "";
            if (!String.IsNullOrEmpty(item.ETag) && !String.IsNullOrEmpty(tag) && item.ETag != tag) throw new IOException("The server content changed; the partial file was kept.");
            if (!String.IsNullOrEmpty(item.LastModified) && !String.IsNullOrEmpty(modified) && item.LastModified != modified) throw new IOException("The server content changed; the partial file was kept.");
            item.ETag = String.IsNullOrEmpty(tag) ? item.ETag : tag;
            item.LastModified = String.IsNullOrEmpty(modified) ? item.LastModified : modified;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(part));
        using (FileStream seed = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) seed.SetLength(item.Size);
        item.SegmentedMode = true;
        item.CompletedRanges = MergeRanges(ranges);
        List<long[]> todo = MissingRanges(item.Size, connections, item.CompletedRanges);
        if (todo.Count == 0) return true;
        long[] active = new long[connections];
        SemaphoreSlim slots = new SemaphoreSlim(connections);
        List<Task> jobs = new List<Task>();
        foreach (long[] range in todo)
        {
            await slots.WaitAsync(cancel);
            int slot = jobs.Count % connections;
            jobs.Add(Task.Run(async delegate
            {
                try { await DownloadRange(item, part, range[0], range[1], slot, active, cancel); }
                finally { slots.Release(); }
            }, cancel));
        }
        await Task.WhenAll(jobs);
        item.Bytes = CountRanges(item.CompletedRanges);
        item.AcceptsRanges = true;
        SaveState();
        return true;
    }

    private async Task DownloadRange(NativeDownload item, string part, long start, long end, int slot, long[] active, CancellationToken cancel)
    {
        using (HttpResponseMessage response = await GetRange(item.Url, start, end, item, cancel))
        {
            ContentRangeHeaderValue actual = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || actual == null || actual.From != start || actual.To != end || actual.Length != item.Size) throw new IOException("The server did not return the requested file range.");
            string tag = response.Headers.ETag == null ? "" : response.Headers.ETag.ToString();
            if (!String.IsNullOrEmpty(item.ETag) && !String.IsNullOrEmpty(tag) && item.ETag != tag) throw new IOException("The server returned changed content for a file range.");
            using (Stream input = await response.Content.ReadAsStreamAsync())
            using (FileStream output = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                output.Position = start;
                byte[] buffer = new byte[64 * 1024]; long position = start; Stopwatch watch = Stopwatch.StartNew(); long throttledBytes = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, 0, buffer.Length, cancel);
                    if (read == 0) break;
                    await output.WriteAsync(buffer, 0, read, cancel);
                    position += read; active[slot] = position - start; throttledBytes += read;
                    lock (item) item.Bytes = CountRanges(item.CompletedRanges) + active.Sum();
                    RecordSpeedBytes(item, read);
                    Throttle(watch, throttledBytes, Math.Max(1, Math.Min(settings.ConnectionsPerFile, running.Count)), cancel);
                }
                await output.FlushAsync(cancel);
                if (position != end + 1) throw new IOException("The server closed a file range before all requested bytes arrived.");
            }
        }
        active[slot] = 0;
        lock (item)
        {
            item.CompletedRanges = MergeRanges((item.CompletedRanges ?? new List<long[]>()).Concat(new[] { new long[] { start, end } }).ToList());
            item.Bytes = CountRanges(item.CompletedRanges) + active.Sum();
        }
        SaveState();
    }

    private void Throttle(Stopwatch watch, long bytes, int todoConnections, CancellationToken cancel)
    {
        int cap = settings.SnailModeKiB > 0 ? settings.SnailModeKiB : settings.SpeedLimitKiB;
        if (cap <= 0) return;
        long globalLimit = cap * 1024L;
        long perWorker = Math.Max(1, globalLimit / Math.Max(1, running.Count) / Math.Max(1, todoConnections));
        long expected = bytes * 1000L / perWorker;
        int delay = (int)Math.Min(250, expected - watch.ElapsedMilliseconds);
        if (delay > 0) Task.Delay(delay, cancel).Wait(cancel);
    }

    private async Task TransferSingle(NativeDownload item, string part, CancellationToken cancel)
    {
        long offset = 0;
        if (File.Exists(part)) offset = new FileInfo(part).Length;
        if (item.Size > 0) offset = Math.Min(offset, item.Size);
        HttpResponseMessage response = await GetRange(item.Url, offset, null, item, cancel);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose(); offset = 0;
            response = await GetRange(item.Url, null, null, null, cancel);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new IOException("Server responded " + (int)response.StatusCode + " " + response.ReasonPhrase + ".");
            if (offset > 0 && (response.Content.Headers.ContentRange == null || response.Content.Headers.ContentRange.From != offset)) throw new IOException("The server did not resume at the saved byte offset.");
            if (response.Content.Headers.ContentRange != null && response.Content.Headers.ContentRange.Length.HasValue) item.Size = response.Content.Headers.ContentRange.Length.Value;
            else if (response.Content.Headers.ContentLength.HasValue && offset == 0) item.Size = response.Content.Headers.ContentLength.Value;
            if (response.Headers.ETag != null) item.ETag = response.Headers.ETag.ToString();
            if (response.Content.Headers.LastModified.HasValue) item.LastModified = response.Content.Headers.LastModified.Value.ToString("R");
            using (Stream input = await response.Content.ReadAsStreamAsync())
            using (FileStream output = new FileStream(part, offset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                output.Position = offset; byte[] buffer = new byte[64 * 1024]; Stopwatch watch = Stopwatch.StartNew(); long bytes = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, 0, buffer.Length, cancel);
                    if (read == 0) break;
                    await output.WriteAsync(buffer, 0, read, cancel);
                    bytes += read; item.Bytes = offset + bytes; RecordSpeedBytes(item, read);
                    Throttle(watch, bytes, 1, cancel);
                }
                await output.FlushAsync(cancel);
            }
        }
    }

    private async Task<HttpResponseMessage> GetRange(string url, long? start, long? end, NativeDownload validator, CancellationToken cancel)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ValidateUri(url));
        if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (validator != null)
        {
            if (!String.IsNullOrWhiteSpace(validator.ETag)) { EntityTagHeaderValue tag; if (EntityTagHeaderValue.TryParse(validator.ETag, out tag)) request.Headers.IfRange = new RangeConditionHeaderValue(tag); }
            else if (!String.IsNullOrWhiteSpace(validator.LastModified)) { DateTimeOffset modified; if (DateTimeOffset.TryParse(validator.LastModified, out modified)) request.Headers.IfRange = new RangeConditionHeaderValue(modified); }
        }
        try { return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel); }
        catch { request.Dispose(); throw; }
    }

    private async Task CheckLinks()
    {
        List<string> urls = linksBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Take(81).ToList();
        if (urls.Count == 0) { MessageBox.Show(this, "Paste one or more direct file links first.", "LinkGrabber", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (urls.Count > 80) { MessageBox.Show(this, "Check up to 80 links at a time.", "LinkGrabber", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        checkLinksButton.Enabled = false; checkLinksButton.Text = "Checking…";
        results.Clear();
        RenderGrabber();
        WriteLog("INFO", "Starting link check for " + urls.Count + " link(s). Up to 8 links are checked at once; each request times out after 20 seconds.");
        try
        {
            using (SemaphoreSlim limit = new SemaphoreSlim(8))
            {
                int completed = 0;
                List<Task<GrabResult>> tasks = urls.Select(async url =>
                {
                    await limit.WaitAsync();
                    try { return await InspectLink(url); }
                    finally
                    {
                        limit.Release();
                        int finished = Interlocked.Increment(ref completed);
                        if (checkLinksButton != null && !checkLinksButton.IsDisposed) checkLinksButton.Text = "Checking " + finished + "/" + urls.Count + "…";
                    }
                }).ToList();
                results.AddRange(await Task.WhenAll(tasks));
            }
            RenderGrabber();
            int succeeded = results.Count(r => r.Selected && String.IsNullOrEmpty(r.Error));
            int failed = results.Count - succeeded;
            WriteLog("INFO", "Link check finished: " + succeeded + " available, " + failed + " unavailable or timed out.");
        }
        catch (Exception error) { WriteLog("ERROR", "Link check failed: " + error.GetType().Name + ": " + error.Message); }
        finally { checkLinksButton.Enabled = true; checkLinksButton.Text = "⌕  Check links"; }
    }

    private async Task<GrabResult> InspectLink(string value)
    {
        GrabResult result = new GrabResult { Url = value };
        CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Stopwatch watch = Stopwatch.StartNew();
        Uri uri = null;
        string stage = "URL validation";
        bool timedOut = false;
        try
        {
            uri = ValidateUri(value); result.Host = uri.Host;
            string httpStatus = String.Empty;
            bool needsProbe = true;
            WriteLog("INFO", "Checking " + uri.Host + ": sending HEAD request.");
            using (CancellationTokenSource headTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                headTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    stage = "HEAD response";
                    using (HttpRequestMessage headRequest = new HttpRequestMessage(HttpMethod.Head, uri))
                    using (HttpResponseMessage head = await Http.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, headTimeout.Token))
                    {
                        WriteLog("INFO", uri.Host + ": HEAD returned HTTP " + (int)head.StatusCode + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " seconds.");
                        if (!head.IsSuccessStatusCode) httpStatus = (int)head.StatusCode + " " + head.ReasonPhrase;
                        result.Size = head.Content.Headers.ContentLength ?? 0;
                        result.Filename = ResponseFilename(head, uri);
                        result.Ranges = head.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                        needsProbe = String.IsNullOrWhiteSpace(result.Filename) || String.IsNullOrEmpty(Path.GetExtension(result.Filename)) || !head.IsSuccessStatusCode;
                        if (!needsProbe) result.Selected = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested) throw;
                    needsProbe = true;
                    WriteLog("WARN", uri.Host + ": HEAD did not respond within 8 seconds; trying a range GET instead.");
                }
            }

            if (needsProbe)
            {
                stage = "range GET fallback";
                WriteLog("INFO", uri.Host + ": sending a one-byte range GET fallback.");
                using (HttpResponseMessage probe = await GetRange(value, 0, 0, null, timeout.Token))
                {
                    WriteLog("INFO", uri.Host + ": range GET returned HTTP " + (int)probe.StatusCode + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " seconds.");
                    if (!probe.IsSuccessStatusCode && probe.StatusCode != HttpStatusCode.PartialContent) httpStatus = (int)probe.StatusCode + " " + probe.ReasonPhrase;
                    Uri responseUri = probe.RequestMessage == null || probe.RequestMessage.RequestUri == null ? uri : probe.RequestMessage.RequestUri;
                    string probedName = ResponseFilename(probe, responseUri);
                    if (!String.IsNullOrWhiteSpace(probedName)) result.Filename = probedName;
                    if (probe.Content.Headers.ContentRange != null && probe.Content.Headers.ContentRange.Length.HasValue) result.Size = probe.Content.Headers.ContentRange.Length.Value;
                    result.Ranges = result.Ranges || probe.StatusCode == HttpStatusCode.PartialContent || probe.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                    result.Selected = probe.IsSuccessStatusCode || probe.StatusCode == HttpStatusCode.PartialContent;
                }
            }
            if (String.IsNullOrWhiteSpace(result.Filename)) result.Filename = FileNameFromUri(uri);
            if (!result.Selected) result.Error = "Unavailable" + (String.IsNullOrEmpty(httpStatus) ? String.Empty : " (HTTP " + httpStatus + ")");
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            result.Error = "Timed out during " + stage + " after " + watch.Elapsed.TotalSeconds.ToString("0") + " seconds.";
        }
        catch (Exception error)
        {
            result.Error = stage + " failed (" + error.GetType().Name + "): " + error.Message;
            if (!String.IsNullOrWhiteSpace(value))
            {
                int urlIndex = result.Error.IndexOf(value, StringComparison.OrdinalIgnoreCase);
                if (urlIndex >= 0) result.Error = result.Error.Substring(0, urlIndex) + "link" + result.Error.Substring(urlIndex + value.Length);
            }
        }
        finally { timeout.Dispose(); }
        if (timedOut && uri != null)
        {
            string diagnostics = await DiagnoseHost(uri);
            if (!String.IsNullOrEmpty(diagnostics)) result.Error += " " + diagnostics;
        }
        WriteLog(String.IsNullOrEmpty(result.Error) ? "INFO" : "WARN", "Check " + (String.IsNullOrWhiteSpace(result.Host) ? "link" : result.Host) + ": " + (String.IsNullOrEmpty(result.Error) ? "available" : result.Error));
        return result;
    }

    private async Task<string> DiagnoseHost(Uri uri)
    {
        IPAddress[] addresses;
        try
        {
            Task<IPAddress[]> lookup = Task.Run(() => Dns.GetHostAddresses(uri.Host));
            if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup) return "DNS lookup is still pending after 3 seconds.";
            addresses = await lookup;
            if (addresses == null || addresses.Length == 0) return "DNS returned no IP addresses.";
            WriteLog("INFO", uri.Host + ": DNS resolved to " + addresses.Length + " address(es).");
        }
        catch (Exception error) { return "DNS lookup failed: " + error.GetType().Name + " - " + error.Message; }

        using (TcpClient client = new TcpClient())
        {
            try
            {
                Task connect = Task.Factory.FromAsync(client.BeginConnect(addresses, uri.Port, null, null), client.EndConnect);
                if (await Task.WhenAny(connect, Task.Delay(3000)) != connect)
                {
                    client.Close();
                    return "DNS resolved, but TCP port " + uri.Port + " did not connect within 3 seconds.";
                }
                await connect;
                WriteLog("INFO", uri.Host + ": TCP port " + uri.Port + " is reachable.");
                return "DNS and TCP port " + uri.Port + " are reachable; the delay is likely in TLS or the HTTP response, or the host is filtering the request.";
            }
            catch (Exception error) { return "DNS resolved, but TCP port " + uri.Port + " failed: " + error.GetType().Name + " - " + error.Message; }
        }
    }

    private void AddSelectedLinks()
    {
        foreach (GrabResult result in results.Where(r => r.Selected && String.IsNullOrEmpty(r.Error)))
        {
            if (downloads.Any(d => d.Url == result.Url)) continue;
            downloads.Insert(0, new NativeDownload { Id = Guid.NewGuid().ToString("N"), Url = result.Url, Filename = SafeFileName(result.Filename), Host = result.Host, Folder = "", FilePath = "", Size = result.Size, Bytes = 0, AcceptsRanges = result.Ranges, Status = "queued", Error = "", AddedAt = DateTime.UtcNow.ToString("o"), CompletedRanges = new List<long[]>() });
        }
        SaveState(); RenderDownloads(); tabs.SelectedIndex = 0;
    }

    private void RunContextAction(string action)
    {
        NativeDownload item = downloads.FirstOrDefault(d => d.Id == contextDownloadId);
        if (item == null) return;
        if (action == "pause") Pause(item);
        else if (action == "resume") Resume(item);
        else if (action == "remove") RemoveItems(new[] { item }, false);
        else if (action == "delete") RemoveItems(new[] { item }, true);
    }

    private void Pause(NativeDownload item)
    {
        CancellationTokenSource token;
        if (transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        else if (item.Status == "queued") item.Status = "paused";
        SaveState(); RenderDownloads();
    }

    private void Resume(NativeDownload item)
    {
        if (item.Status != "paused" && item.Status != "error") return;
        item.Status = "queued"; item.Error = ""; SaveState();
        if (queueArmed) PumpQueue();
        else StartOne(item);
        RenderDownloads();
    }

    private void StartOne(NativeDownload item)
    {
        lock (schedulerLock)
        {
            if (running.Count >= settings.Concurrency || (runningHosts.ContainsKey(item.Host) && runningHosts[item.Host] >= settings.PerHost)) return;
            item.Status = "downloading"; running.Add(item.Id); AddRunningHost(item.Host, 1);
            transfers[item.Id] = new CancellationTokenSource();
        }
        CancellationToken token = transfers[item.Id].Token;
        Task.Run(() => Transfer(item, token)).ContinueWith(task => { if (!IsDisposed) BeginInvoke((Action)(() => CompleteTransfer(item, task))); });
    }

    private async Task ValidateFreshLink(NativeDownload item, string fresh)
    {
        Uri uri = ValidateUri(fresh);
        using (HttpResponseMessage response = await GetRange(uri.ToString(), 0, 0, null, CancellationToken.None))
        {
            ContentRangeHeaderValue range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range == null) throw new IOException("The refreshed link must support HTTP byte ranges.");
            if (item.Size > 0 && range.Length.HasValue && range.Length.Value != item.Size) throw new IOException("The refreshed link appears to point to a different-sized file.");
            string tag = response.Headers.ETag == null ? "" : response.Headers.ETag.ToString();
            if (!String.IsNullOrEmpty(item.ETag) && !String.IsNullOrEmpty(tag) && item.ETag != tag) throw new IOException("The refreshed link points to changed content.");
        }
    }

    private async Task RefreshLink(string id)
    {
        NativeDownload item = downloads.FirstOrDefault(d => d.Id == id);
        if (item == null) return;
        string fresh = Prompt("Paste a fresh direct link for this same file:", "Refresh download link", item.Url);
        if (String.IsNullOrWhiteSpace(fresh)) return;
        try
        {
            await ValidateFreshLink(item, fresh);
            CancellationTokenSource token;
            if (transfers.TryGetValue(item.Id, out token))
            {
                item.Status = "paused"; token.Cancel();
                while (transfers.ContainsKey(item.Id)) await Task.Delay(80);
            }
            item.Url = fresh.Trim(); item.Host = ValidateUri(item.Url).Host; item.Status = "queued"; item.Error = "";
            SaveState();
            if (queueArmed) PumpQueue(); else StartOne(item);
            RenderDownloads();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Refresh link", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void RemoveSelected(bool deleteFiles)
    {
        NativeDownload[] selected = downloads.Where(d => selectedIds.Contains(d.Id)).ToArray();
        RemoveItems(selected, deleteFiles);
    }

    private async void RemoveItems(IEnumerable<NativeDownload> entries, bool deleteFiles)
    {
        NativeDownload[] selected = entries.ToArray();
        if (selected.Length == 0) return;
        if (deleteFiles && MessageBox.Show(this, "Permanently delete the selected download file(s) and partial file(s)? This cannot be undone.", "Delete downloads", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (NativeDownload item in selected)
        {
            CancellationTokenSource token;
            if (transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        }
        while (selected.Any(item => transfers.ContainsKey(item.Id))) await Task.Delay(80);
        foreach (NativeDownload item in selected)
        {
            if (deleteFiles && !String.IsNullOrWhiteSpace(item.FilePath))
            {
                string final = Path.GetFullPath(item.FilePath);
                string folder = Path.GetFullPath(String.IsNullOrWhiteSpace(item.Folder) ? Path.GetDirectoryName(final) : item.Folder);
                if (!String.Equals(Path.GetDirectoryName(final), folder, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "The saved file path is outside its download folder; it was not deleted.", "Delete download", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                TryDelete(final); TryDelete(final + ".fetchflow-part");
            }
            downloads.Remove(item); selectedIds.Remove(item.Id);
        }
        SaveState(); RenderDownloads();
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private void SaveStateSafely() { try { SaveState(); } catch { } }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (closing) return;
        if (closeRequested) { e.Cancel = true; return; }
        e.Cancel = true;
        closeRequested = true; closing = true; queueArmed = false; refreshTimer.Stop();
        foreach (NativeDownload item in downloads)
        {
            CancellationTokenSource token;
            if (transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        }
        Task.Run(async delegate
        {
            while (!transfers.IsEmpty) await Task.Delay(100);
            SaveStateSafely();
            if (!IsDisposed) BeginInvoke((Action)(() => { closing = true; exitRequested = true; if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); } Close(); }));
        });
    }

    private void AddRunningHost(string host, int delta)
    {
        string key = host ?? "";
        int next = (runningHosts.ContainsKey(key) ? runningHosts[key] : 0) + delta;
        if (next <= 0) runningHosts.Remove(key); else runningHosts[key] = next;
    }

    private void FinalizeFile(NativeDownload item, string part)
    {
        if (!File.Exists(part)) throw new FileNotFoundException("The partial download file is missing.", part);
        string final = item.FilePath;
        if (File.Exists(final)) final = ChoosePath(item.Folder, item.Filename);
        try { File.Move(part, final); }
        catch (IOException) { File.Copy(part, final, false); File.Delete(part); }
        item.FilePath = final; item.Filename = Path.GetFileName(final); item.Bytes = item.Size > 0 ? item.Size : new FileInfo(final).Length;
    }

    private static string ChoosePath(string folder, string filename)
    {
        Directory.CreateDirectory(folder);
        string clean = SafeFileName(filename);
        string candidate = Path.Combine(folder, clean);
        string ext = Path.GetExtension(clean); string stem = Path.GetFileNameWithoutExtension(clean); int n = 1;
        while (File.Exists(candidate) || File.Exists(candidate + ".fetchflow-part")) candidate = Path.Combine(folder, stem + " (" + n++ + ")" + ext);
        return candidate;
    }

    private static string SafeFileName(string name)
    {
        name = Path.GetFileName((name ?? "download").Replace('/', '_').Replace('\\', '_'));
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        return String.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    private static Uri ValidateUri(string value)
    {
        Uri uri;
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !String.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Only complete HTTP and HTTPS direct links are supported.");
        return uri;
    }

    private static string ResponseFilename(HttpResponseMessage response, Uri uri)
    {
        string name = null;
        if (response.Content.Headers.ContentDisposition != null)
        {
            name = response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName;
            if (name != null) name = name.Trim().Trim('"');
        }
        if (String.IsNullOrWhiteSpace(name)) name = FileNameFromUri(uri);
        try { name = Uri.UnescapeDataString(name); } catch { }
        return SafeFileName(name);
    }

    private static string FileNameFromUri(Uri uri)
    {
        string candidate = uri.Segments.LastOrDefault();
        try { candidate = Uri.UnescapeDataString(candidate ?? "download"); } catch { }
        return SafeFileName(candidate);
    }

    private static List<long[]> MergeRanges(List<long[]> ranges)
    {
        List<long[]> result = new List<long[]>();
        foreach (long[] range in ranges.Where(r => r != null && r.Length >= 2 && r[0] >= 0 && r[1] >= r[0]).OrderBy(r => r[0]))
        {
            long[] last = result.LastOrDefault();
            if (last != null && range[0] <= last[1] + 1) last[1] = Math.Max(last[1], range[1]);
            else result.Add(new long[] { range[0], range[1] });
        }
        return result;
    }

    private static long CountRanges(List<long[]> ranges) { return MergeRanges(ranges ?? new List<long[]>()).Sum(r => r[1] - r[0] + 1); }

    private static List<long[]> MissingRanges(long size, int count, List<long[]> completed)
    {
        List<long[]> todo = new List<long[]>(); long width = (size + count - 1) / count;
        for (long start = 0; start < size; start += width)
        {
            long end = Math.Min(size - 1, start + width - 1); long cursor = start;
            foreach (long[] done in completed)
            {
                if (done[1] < cursor || done[0] > end) continue;
                if (done[0] > cursor) todo.Add(new long[] { cursor, Math.Min(end, done[0] - 1) });
                cursor = Math.Max(cursor, done[1] + 1);
                if (cursor > end) break;
            }
            if (cursor <= end) todo.Add(new long[] { cursor, end });
        }
        return todo;
    }

    private static string StatusText(NativeDownload item)
    {
        if (!String.IsNullOrWhiteSpace(item.Error) && item.Status == "error") return "Failed: " + item.Error;
        return item.Status == "downloading" ? "Downloading" : item.Status == "finished" ? "Completed" : item.Status == "paused" ? "Paused" : item.Status == "error" ? "Failed" : "Queued";
    }

    private static string ProgressText(NativeDownload item)
    {
        return item.Size > 0 ? FormatBytes(item.Bytes) + " / " + FormatBytes(item.Size) + "   " + Math.Min(100, item.Bytes * 100 / item.Size) + "%" : FormatBytes(item.Bytes);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1000) return Math.Max(0, bytes) + " B";
        string[] units = { "KB", "MB", "GB", "TB" }; double n = bytes / 1024.0; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return n.ToString(n >= 100 ? "0" : "0.0") + " " + units[i];
    }

    private string Prompt(string text, string title, string initial)
    {
        using (Form prompt = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(600, 130), MinimizeBox = false, MaximizeBox = false, BackColor = BackColor, ForeColor = ForeColor })
        {
            Label label = new Label { Text = text, Left = 14, Top = 13, AutoSize = true };
            TextBox input = new TextBox { Left = 14, Top = 40, Width = 565, Text = initial, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            Button ok = new Button { Text = "Verify and resume", DialogResult = DialogResult.OK, Left = 365, Top = 80, Width = 125 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 500, Top = 80, Width = 80 };
            prompt.Controls.Add(label); prompt.Controls.Add(input); prompt.Controls.Add(ok); prompt.Controls.Add(cancel); prompt.AcceptButton = ok; prompt.CancelButton = cancel;
            return prompt.ShowDialog(this) == DialogResult.OK ? input.Text : null;
        }
    }
}
