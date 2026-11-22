using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    [DataMember(Name="retryCount")] public int RetryCount { get; set; }
    [DataMember(Name="retryAt")] public string RetryAt { get; set; }
    [DataMember(Name="youtube")] public bool IsYoutube { get; set; }
    [DataMember(Name="mediaMode")] public string MediaMode { get; set; }
    [DataMember(Name="mediaSelector")] public string MediaSelector { get; set; }
    [DataMember(Name="mediaExtension")] public string MediaExtension { get; set; }
    [DataMember(Name="mediaAudioQuality")] public string MediaAudioQuality { get; set; }
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
    public bool IsYoutube { get; set; }
    public List<YtFormatChoice> Formats { get; set; }
    public string SelectedFormatKey { get; set; }
}

[DataContract]
public sealed class YtFormatChoice
{
    [DataMember(Name="key")] public string Key { get; set; }
    [DataMember(Name="label")] public string Label { get; set; }
    [DataMember(Name="selector")] public string Selector { get; set; }
    [DataMember(Name="mode")] public string Mode { get; set; }
    [DataMember(Name="extension")] public string Extension { get; set; }
    [DataMember(Name="requiresFfmpeg")] public bool RequiresFfmpeg { get; set; }
    [DataMember(Name="audioQuality")] public string AudioQuality { get; set; }
}

[DataContract]
public sealed class YtVideoInfo
{
    [DataMember(Name="title")] public string Title { get; set; }
    [DataMember(Name="formats")] public List<YtVideoFormat> Formats { get; set; }
}

[DataContract]
public sealed class YtVideoFormat
{
    [DataMember(Name="format_id")] public string Id { get; set; }
    [DataMember(Name="ext")] public string Extension { get; set; }
    [DataMember(Name="height")] public int? Height { get; set; }
    [DataMember(Name="abr")] public double? AudioBitrate { get; set; }
    [DataMember(Name="vcodec")] public string VideoCodec { get; set; }
    [DataMember(Name="acodec")] public string AudioCodec { get; set; }
    [IgnoreDataMember] public int HeightValue { get { return Height.GetValueOrDefault(); } }
    [IgnoreDataMember] public double AudioBitrateValue { get { return AudioBitrate.GetValueOrDefault(); } }
}

[DataContract]
internal sealed class NativeBridgeMessage
{
    [DataMember(Name="kind")] public string Kind { get; set; }
    [DataMember(Name="url")] public string Url { get; set; }
    [DataMember(Name="name")] public string Name { get; set; }
    [DataMember(Name="autoCheck")] public bool AutoCheck { get; set; }
    [DataMember(Name="selector")] public string Selector { get; set; }
    [DataMember(Name="mode")] public string Mode { get; set; }
    [DataMember(Name="extension")] public string Extension { get; set; }
    [DataMember(Name="audioQuality")] public string AudioQuality { get; set; }
}

[DataContract]
internal sealed class NativeBridgeReply
{
    [DataMember(Name="ok")] public bool Ok { get; set; }
    [DataMember(Name="error", EmitDefaultValue=false)] public string Error { get; set; }
    [DataMember(Name="title", EmitDefaultValue=false)] public string Title { get; set; }
    [DataMember(Name="formats", EmitDefaultValue=false)] public List<YtFormatChoice> Formats { get; set; }
}

internal static class NativeProgram
{
    private const int SwRestore = 9;
    private const uint WmCopyData = 0x004A;
    private const string NativeExtensionId = "joabmphifckcidahjfgmhkonpnmnjbhm";
    private static readonly uint ActivateMessage = RegisterWindowMessage("Fetchflow.ActivateExistingInstance");

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyDataPacket
    {
        public IntPtr Data;
        public int ByteCount;
        public IntPtr Payload;
    }

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
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Skip(1).Any(arg => arg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)))
        {
            RunNativeMessagingHost(arguments);
            return;
        }
        string browserRequest = arguments.Skip(1).FirstOrDefault(arg => arg.StartsWith("fetchflow://", StringComparison.OrdinalIgnoreCase));
        bool createdNew;
        string userId = WindowsIdentity.GetCurrent().User.Value;
        using (Mutex singleInstance = new Mutex(true, "Local\\Fetchflow." + userId, out createdNew))
        {
            if (!createdNew)
            {
                ActivateExistingInstance(browserRequest);
                return;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new NativeMainForm(browserRequest));
            }
            finally
            {
                singleInstance.ReleaseMutex();
            }
        }
    }

    private static void RunNativeMessagingHost(string[] arguments)
    {
        NativeBridgeReply reply = new NativeBridgeReply { Ok = false, Error = "The Fetchflow browser connection was rejected." };
        string expectedOrigin = "chrome-extension://" + NativeExtensionId + "/";
        string origin = arguments.Skip(1).FirstOrDefault(arg => arg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (!String.Equals(origin, expectedOrigin, StringComparison.Ordinal)) throw new UnauthorizedAccessException("This extension is not allowed to connect to Fetchflow.");
            Stream input = Console.OpenStandardInput();
            byte[] header = new byte[4];
            if (!ReadNativeBytes(input, header, header.Length)) throw new EndOfStreamException("Chrome did not send a complete request.");
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("The browser request is too large or invalid.");
            byte[] payload = new byte[length];
            if (!ReadNativeBytes(input, payload, length)) throw new EndOfStreamException("The browser request was incomplete.");
            NativeBridgeMessage message;
            using (MemoryStream buffer = new MemoryStream(payload))
                message = (NativeBridgeMessage)new DataContractJsonSerializer(typeof(NativeBridgeMessage)).ReadObject(buffer);
            reply = OpenBrowserRequest(message);
        }
        catch (Exception error) { reply.Error = error.Message; }
        try
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(NativeBridgeReply)).WriteObject(buffer, reply);
                byte[] payload = buffer.ToArray();
                byte[] header = BitConverter.GetBytes(payload.Length);
                Stream output = Console.OpenStandardOutput();
                output.Write(header, 0, header.Length);
                output.Write(payload, 0, payload.Length);
                output.Flush();
            }
        }
        catch { }
    }

    private static bool ReadNativeBytes(Stream input, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = input.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private static NativeBridgeReply OpenBrowserRequest(NativeBridgeMessage message)
    {
        if (message == null || String.IsNullOrWhiteSpace(message.Url) || message.Url.Length > 14000)
            return new NativeBridgeReply { Ok = false, Error = "The browser did not provide a valid download URL." };
        Uri url;
        if (!Uri.TryCreate(message.Url, UriKind.Absolute, out url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return new NativeBridgeReply { Ok = false, Error = "Only HTTP and HTTPS links can be sent to Fetchflow." };
        bool download = String.Equals(message.Kind, "download", StringComparison.Ordinal);
        bool link = String.Equals(message.Kind, "link", StringComparison.Ordinal);
        bool videoFormats = String.Equals(message.Kind, "video-formats", StringComparison.Ordinal);
        bool youtubeDownload = String.Equals(message.Kind, "youtube-download", StringComparison.Ordinal);
        if (!download && !link && !videoFormats && !youtubeDownload)
            return new NativeBridgeReply { Ok = false, Error = "The browser request type is not supported." };
        if ((videoFormats || youtubeDownload) && !IsYoutubeUrl(url.AbsoluteUri))
            return new NativeBridgeReply { Ok = false, Error = "YouTube format requests must use a YouTube video URL." };
        if (videoFormats)
        {
            string pipeName = "FetchflowFormats" + Guid.NewGuid().ToString("N");
            string formatRequest = "fetchflow://formats?url=" + Uri.EscapeDataString(url.AbsoluteUri) + "&pipe=" + pipeName;
            using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
            {
                Process.Start(new ProcessStartInfo { FileName = formatRequest, UseShellExecute = true });
                Task connect = Task.Run(() => pipe.WaitForConnection());
                if (!connect.Wait(TimeSpan.FromMinutes(5))) return new NativeBridgeReply { Ok = false, Error = "Fetchflow did not return YouTube format details in time." };
                using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8))
                {
                    string encoded = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(encoded)) return new NativeBridgeReply { Ok = false, Error = "Fetchflow returned no YouTube format details." };
                    byte[] json = Convert.FromBase64String(encoded);
                    using (MemoryStream buffer = new MemoryStream(json))
                        return (NativeBridgeReply)new DataContractJsonSerializer(typeof(NativeBridgeReply)).ReadObject(buffer);
                }
            }
        }
        if (youtubeDownload)
        {
            if (String.IsNullOrWhiteSpace(message.Selector) || !Regex.IsMatch(message.Selector, "^[A-Za-z0-9._,+-]{1,120}$"))
                return new NativeBridgeReply { Ok = false, Error = "The selected YouTube format is invalid." };
            if (message.Mode != "video" && message.Mode != "audio" && message.Mode != "mp3")
                return new NativeBridgeReply { Ok = false, Error = "The selected YouTube format type is invalid." };
            string extension = Regex.IsMatch(message.Extension ?? "", "^[A-Za-z0-9]{1,8}$") ? message.Extension.ToLowerInvariant() : "mp4";
            string youtubeHandoff = Guid.NewGuid().ToString("N");
            string youtubeDownloadRequest = "fetchflow://youtube-download?url=" + Uri.EscapeDataString(url.AbsoluteUri) +
                "&title=" + Uri.EscapeDataString((message.Name ?? "YouTube video").Substring(0, Math.Min((message.Name ?? "YouTube video").Length, 240))) +
                "&selector=" + Uri.EscapeDataString(message.Selector) + "&mode=" + message.Mode + "&ext=" + extension + "&handoff=" + youtubeHandoff;
            if (!String.IsNullOrWhiteSpace(message.AudioQuality)) youtubeDownloadRequest += "&audio=" + Uri.EscapeDataString(message.AudioQuality.Substring(0, Math.Min(message.AudioQuality.Length, 16)));
            using (NamedPipeServerStream pipe = new NamedPipeServerStream("FetchflowBrowser" + youtubeHandoff, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
            {
                Process.Start(new ProcessStartInfo { FileName = youtubeDownloadRequest, UseShellExecute = true });
                Task connect = Task.Run(() => pipe.WaitForConnection());
                if (!connect.Wait(TimeSpan.FromSeconds(30))) return new NativeBridgeReply { Ok = false, Error = "Fetchflow did not receive the selected YouTube quality." };
                using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8))
                    return String.Equals(reader.ReadLine(), "started", StringComparison.Ordinal) ? new NativeBridgeReply { Ok = true } : new NativeBridgeReply { Ok = false, Error = "Fetchflow could not start the selected YouTube quality." };
            }
        }
        string request = "fetchflow://" + (download ? "download" : "add") + "?url=" + Uri.EscapeDataString(url.AbsoluteUri);
        if (download && !String.IsNullOrWhiteSpace(message.Name)) request += "&name=" + Uri.EscapeDataString(message.Name.Substring(0, Math.Min(message.Name.Length, 240)));
        if (link && message.AutoCheck) request += "&check=1";
        if (!download)
        {
            Process.Start(new ProcessStartInfo { FileName = request, UseShellExecute = true });
            return new NativeBridgeReply { Ok = true };
        }
        string handoff = Guid.NewGuid().ToString("N");
        request += "&handoff=" + handoff;
        using (NamedPipeServerStream pipe = new NamedPipeServerStream("FetchflowBrowser" + handoff, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
        {
            Process.Start(new ProcessStartInfo { FileName = request, UseShellExecute = true });
            Task connect = Task.Run(() => pipe.WaitForConnection());
            if (!connect.Wait(TimeSpan.FromMinutes(10))) return new NativeBridgeReply { Ok = false, Error = "Fetchflow did not receive the Chrome download prompt." };
            using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8))
            {
                string decision = reader.ReadLine();
                if (String.Equals(decision, "started", StringComparison.Ordinal)) return new NativeBridgeReply { Ok = true };
                return new NativeBridgeReply { Ok = false, Error = "Download canceled in Fetchflow." };
            }
        }
    }

    private static bool IsYoutubeUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtu.be" || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
    }

    private static void ActivateExistingInstance(string browserRequest)
    {
        IntPtr window = FindWindow(null, "Fetchflow Download Manager");
        if (window == IntPtr.Zero) return;
        if (!String.IsNullOrWhiteSpace(browserRequest))
        {
            IntPtr payload = Marshal.StringToHGlobalUni(browserRequest);
            IntPtr packetPointer = IntPtr.Zero;
            try
            {
                CopyDataPacket packet = new CopyDataPacket { Data = IntPtr.Zero, ByteCount = Encoding.Unicode.GetByteCount(browserRequest + "\0"), Payload = payload };
                packetPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CopyDataPacket)));
                Marshal.StructureToPtr(packet, packetPointer, false);
                SendMessage(window, WmCopyData, IntPtr.Zero, packetPointer);
            }
            finally
            {
                if (packetPointer != IntPtr.Zero) Marshal.FreeHGlobal(packetPointer);
                Marshal.FreeHGlobal(payload);
            }
        }
        Uri requestUri;
        bool backgroundFormats = Uri.TryCreate(browserRequest, UriKind.Absolute, out requestUri) && String.Equals(requestUri.Host, "formats", StringComparison.OrdinalIgnoreCase);
        if (backgroundFormats) return;
        SendMessage(window, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        ShowWindowAsync(window, SwRestore);
        SetForegroundWindow(window);
    }
}

public sealed class NativeMainForm : Form
{
    private const int MaxAutomaticRetries = 3;
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
    private string focusedDownloadId;
    private int focusedDownloadColumn = 1;

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
    private bool backgroundFormatOnly;
    private string stateLoadProblem;
    private bool stateRecoveredFromBackup;

    public NativeMainForm(string initialBrowserRequest)
    {
        Uri initialUri;
        backgroundFormatOnly = Uri.TryCreate(initialBrowserRequest, UriKind.Absolute, out initialUri) && String.Equals(initialUri.Host, "formats", StringComparison.OrdinalIgnoreCase);
        Text = "Fetchflow Download Manager";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application;
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
        if (backgroundFormatOnly) { Opacity = 0; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; }
        if (!String.IsNullOrWhiteSpace(initialBrowserRequest))
            BeginInvoke((Action)delegate { ReceiveBrowserRequest(initialBrowserRequest); });
        if (settings.ResumeAtLogin && Environment.GetCommandLineArgs().Any(arg => String.Equals(arg, "--resume-queue", StringComparison.OrdinalIgnoreCase)))
        {
            BeginInvoke((Action)delegate { StartQueue(); HideToTray(); });
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x004A && message.LParam != IntPtr.Zero)
        {
            try
            {
                NativeProgram.CopyDataPacket packet = (NativeProgram.CopyDataPacket)Marshal.PtrToStructure(message.LParam, typeof(NativeProgram.CopyDataPacket));
                if (packet.ByteCount >= 2 && packet.ByteCount <= 32768 && packet.ByteCount % 2 == 0 && packet.Payload != IntPtr.Zero)
                {
                    string requestText = Marshal.PtrToStringUni(packet.Payload, packet.ByteCount / 2 - 1);
                    BeginInvoke((Action)delegate { ReceiveBrowserRequest(requestText); });
                    message.Result = new IntPtr(1);
                    return;
                }
            }
            catch (Exception error) { WriteLog("WARN", "Could not read a browser link: " + error.Message); }
        }
        if ((uint)message.Msg == NativeProgram.ExistingInstanceMessage)
        {
            RestoreFromTray();
            return;
        }
        base.WndProc(ref message);
    }

    private void ReceiveBrowserRequest(string requestText)
    {
        Uri request;
        if (!Uri.TryCreate(requestText, UriKind.Absolute, out request) || !String.Equals(request.Scheme, "fetchflow", StringComparison.OrdinalIgnoreCase)) return;
        Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in (request.Query ?? "").TrimStart('?').Split('&'))
        {
            int separator = field.IndexOf('=');
            if (separator <= 0) continue;
            try { parameters[Uri.UnescapeDataString(field.Substring(0, separator))] = Uri.UnescapeDataString(field.Substring(separator + 1).Replace("+", "%20")); }
            catch { }
        }
        if (String.Equals(request.Host, "download", StringComparison.OrdinalIgnoreCase))
        {
            string directUrl;
            if (parameters.TryGetValue("url", out directUrl)) ReceiveBrowserDownload(directUrl, parameters.ContainsKey("name") ? parameters["name"] : "", parameters.ContainsKey("handoff") ? parameters["handoff"] : "");
            return;
        }
        if (String.Equals(request.Host, "formats", StringComparison.OrdinalIgnoreCase))
        {
            string videoUrl, pipeName;
            if (parameters.TryGetValue("url", out videoUrl) && parameters.TryGetValue("pipe", out pipeName) && Regex.IsMatch(pipeName, "^FetchflowFormats[a-fA-F0-9]{32}$") && IsYoutubeUrl(videoUrl))
                BeginInvoke((Action)delegate { FetchBrowserYoutubeFormats(videoUrl, pipeName); });
            else if (parameters.TryGetValue("pipe", out pipeName)) BeginInvoke((Action)delegate { SendBrowserFormatsResponse(pipeName, new NativeBridgeReply { Ok = false, Error = "The YouTube format request was invalid." }); });
            return;
        }
        if (String.Equals(request.Host, "youtube-download", StringComparison.OrdinalIgnoreCase))
        {
            string videoUrl, title, selector, mode, extension, handoff;
            if (parameters.TryGetValue("url", out videoUrl) && parameters.TryGetValue("title", out title) && parameters.TryGetValue("selector", out selector) && parameters.TryGetValue("mode", out mode) && parameters.TryGetValue("ext", out extension) && parameters.TryGetValue("handoff", out handoff))
                ReceiveYoutubeBrowserDownload(videoUrl, title, selector, mode, extension, parameters.ContainsKey("audio") ? parameters["audio"] : "", handoff);
            return;
        }
        if (!String.Equals(request.Host, "add", StringComparison.OrdinalIgnoreCase)) return;
        List<string> incoming = new List<string>();
        string addUrl;
        string browserVideoUrl = null;
        if (parameters.TryGetValue("url", out addUrl))
        {
            Uri target;
            if (Uri.TryCreate(addUrl, UriKind.Absolute, out target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)) { incoming.Add(target.AbsoluteUri); browserVideoUrl = target.AbsoluteUri; }
        }
        bool autoCheckVideo = browserVideoUrl != null && parameters.ContainsKey("check") && String.Equals(parameters["check"], "1", StringComparison.Ordinal);
        if (incoming.Count == 0 && !autoCheckVideo) return;
        HashSet<string> existing = new HashSet<string>((linksBox.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (string url in incoming.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(url) || existing.Count >= 80) continue;
            linksBox.AppendText((linksBox.TextLength == 0 || linksBox.Text.EndsWith("\n", StringComparison.Ordinal) ? "" : Environment.NewLine) + url);
            existing.Add(url);
            added++;
        }
        tabs.SelectedIndex = 1;
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        linksBox.Focus();
        WriteLog("INFO", "Received " + added + " browser link(s) in LinkGrabber.");
        if (autoCheckVideo) BeginInvoke((Action)delegate { CheckBrowserVideoLink(browserVideoUrl); });
    }

    private async void FetchBrowserYoutubeFormats(string url, string pipeName)
    {
        NativeBridgeReply reply;
        try
        {
            string ytDlp = await EnsureYoutubeExtractor();
            GrabResult result = await InspectYoutube(url, ytDlp);
            reply = new NativeBridgeReply { Ok = result.Formats != null && result.Formats.Count > 0, Error = result.Error, Title = result.Filename, Formats = result.Formats };
        }
        catch (Exception error) { reply = new NativeBridgeReply { Ok = false, Error = "Could not fetch YouTube qualities: " + error.Message }; }
        SendBrowserFormatsResponse(pipeName, reply);
        if (backgroundFormatOnly) BeginInvoke((Action)delegate { exitRequested = true; Close(); });
    }

    private void SendBrowserFormatsResponse(string pipeName, NativeBridgeReply reply)
    {
        if (String.IsNullOrWhiteSpace(pipeName) || !Regex.IsMatch(pipeName, "^FetchflowFormats[a-fA-F0-9]{32}$")) return;
        try
        {
            byte[] json;
            using (MemoryStream buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(NativeBridgeReply)).WriteObject(buffer, reply);
                json = buffer.ToArray();
            }
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
            {
                pipe.Connect(10000);
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false))) { writer.WriteLine(Convert.ToBase64String(json)); writer.Flush(); }
            }
        }
        catch (Exception error) { WriteLog("WARN", "Could not return YouTube formats to Chrome: " + error.Message); }
    }

    private void ReceiveYoutubeBrowserDownload(string value, string title, string selector, string mode, string extension, string audioQuality, string handoff)
    {
        Uri url;
        if (!Uri.TryCreate(value, UriKind.Absolute, out url) || !IsYoutubeUrl(value) || !Regex.IsMatch(selector ?? "", "^[A-Za-z0-9._,+-]{1,120}$") || (mode != "video" && mode != "audio" && mode != "mp3")) { NotifyBrowserHandoff(handoff, "cancel"); return; }
        string ext = SafeExtension(extension);
        if (downloads.Any(d => d.IsYoutube && d.Url == url.AbsoluteUri && d.MediaSelector == selector && d.MediaMode == mode))
        {
            RestoreFromTray();
            NotifyBrowserHandoff(handoff, "started");
            return;
        }
        NativeDownload item = new NativeDownload { Id = Guid.NewGuid().ToString("N"), Url = url.AbsoluteUri, Filename = SafeFileName((String.IsNullOrWhiteSpace(title) ? "YouTube video" : title) + "." + ext), Host = "YouTube", Folder = settings.Folder, FilePath = "", Size = 0, Bytes = 0, AcceptsRanges = false, Status = "queued", Error = "", AddedAt = DateTime.UtcNow.ToString("o"), CompletedRanges = new List<long[]>(), IsYoutube = true, MediaMode = mode, MediaSelector = selector, MediaExtension = ext, MediaAudioQuality = audioQuality ?? "" };
        downloads.Insert(0, item); selectedIds.Clear(); selectedIds.Add(item.Id); queueArmed = true;
        SaveState(); RenderDownloads(); tabs.SelectedIndex = 0; RestoreFromTray();
        if (StartOne(item, true)) { WriteLog("INFO", "Started YouTube download from Chrome: " + item.Filename + " using " + selector + "."); NotifyBrowserHandoff(handoff, "started"); }
        else { item.Status = "queued"; WriteLog("WARN", "YouTube download was queued but did not start immediately."); NotifyBrowserHandoff(handoff, "cancel"); }
        SaveState(); RenderDownloads();
    }

    private async void CheckBrowserVideoLink(string url)
    {
        string previousText = linksBox.Text;
        try
        {
            linksBox.Text = url;
            WriteLog("INFO", "Checking video formats requested from the Chrome video button.");
            await CheckLinks();
        }
        finally { linksBox.Text = previousText; }
    }

    private async void ReceiveBrowserDownload(string value, string suggestedName, string handoff)
    {
        Uri url;
        if (!Uri.TryCreate(value, UriKind.Absolute, out url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)) { NotifyBrowserHandoff(handoff, "cancel"); return; }
        string filename = await ResolveBrowserFilename(url, suggestedName);
        if (String.IsNullOrWhiteSpace(filename)) filename = "download";
        WriteLog("INFO", "Chrome filename resolved to " + filename + (String.IsNullOrWhiteSpace(suggestedName) ? " (browser did not provide a name)." : " (browser suggested " + SafeFileName(Path.GetFileName(suggestedName)) + ")."));
        string downloadsFolder = GetDownloadsFolder();
        string initialPath = ChoosePath(downloadsFolder, filename);
        NativeDownload item = new NativeDownload { Id = Guid.NewGuid().ToString("N"), Url = url.AbsoluteUri, Filename = Path.GetFileName(initialPath), Host = url.Host, Folder = Path.GetDirectoryName(initialPath), FilePath = initialPath, Size = 0, Bytes = 0, AcceptsRanges = false, Status = "queued", Error = "", AddedAt = DateTime.UtcNow.ToString("o"), CompletedRanges = new List<long[]>() };
        downloads.Insert(0, item);
        selectedIds.Clear(); selectedIds.Add(item.Id);
        queueArmed = true;
        SaveState(); RenderDownloads();
        if (!StartOne(item, true))
        {
            downloads.Remove(item); selectedIds.Remove(item.Id); SaveState(); RenderDownloads();
            NotifyBrowserHandoff(handoff, "cancel");
            WriteLog("WARN", "Could not start Chrome download in Fetchflow: " + item.Filename);
            return;
        }
        WriteLog("INFO", "Chrome download is now running in Fetchflow: " + item.Filename + " from " + item.Host + ".");
        RestoreFromTray();
        NotifyBrowserHandoff(handoff, "started");
        using (Form prompt = new Form { Text = "Chrome download received", ClientSize = new Size(650, 232), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.Manual, MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(17, 25, 34), ForeColor = ForeColor, Font = Font, Icon = Icon })
        {
            Label heading = new Label { Text = "Download is running in Fetchflow", Font = new Font("Segoe UI Semibold", 13F), AutoSize = true, Location = new Point(20, 18), ForeColor = Color.FromArgb(225, 237, 242) };
            Label detail = new Label { Text = filename + "  ·  " + url.Host, AutoSize = false, AutoEllipsis = true, Location = new Point(22, 53), Size = new Size(600, 24), ForeColor = Color.FromArgb(150, 171, 181) };
            Label status = new Label { Text = "Choose where to save it, keep downloading, or cancel the Fetchflow download.", AutoSize = false, Location = new Point(22, 78), Size = new Size(600, 20), ForeColor = Color.FromArgb(150, 171, 181) };
            Label pathLabel = new Label { Text = "Save file to", AutoSize = true, Location = new Point(22, 91), ForeColor = Color.FromArgb(190, 207, 215) };
            pathLabel.Location = new Point(22, 105);
            TextBox pathBox = new TextBox { Text = initialPath, Location = new Point(22, 130), Size = new Size(500, 28), BackColor = Color.FromArgb(10, 17, 23), ForeColor = Color.FromArgb(224, 235, 240), BorderStyle = BorderStyle.FixedSingle };
            Button browse = MakeButton("Browse…", delegate
            {
                using (SaveFileDialog dialog = new SaveFileDialog { Title = "Choose download path", InitialDirectory = Directory.Exists(Path.GetDirectoryName(pathBox.Text)) ? Path.GetDirectoryName(pathBox.Text) : downloadsFolder, FileName = Path.GetFileName(pathBox.Text), Filter = "All files (*.*)|*.*", CheckPathExists = true, OverwritePrompt = false, AddExtension = false })
                    if (dialog.ShowDialog(prompt) == DialogResult.OK) pathBox.Text = dialog.FileName;
            }, false);
            browse.Location = new Point(532, 127);
            Button cancel = MakeButton("Cancel", delegate { prompt.DialogResult = DialogResult.Cancel; prompt.Close(); }, false);
            cancel.Location = new Point(432, 180);
            Button downloadNow = MakeButton("Keep downloading", delegate { prompt.DialogResult = DialogResult.OK; prompt.Close(); }, true);
            downloadNow.Location = new Point(528, 180);
            prompt.Controls.AddRange(new Control[] { heading, detail, status, pathLabel, pathBox, browse, cancel, downloadNow });
            prompt.AcceptButton = downloadNow;
            prompt.CancelButton = cancel;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int left = this.Left + (this.Width - prompt.Width) / 2;
            int top = this.Top + (this.Height - prompt.Height) / 2;
            prompt.Location = new Point(Math.Max(area.Left, Math.Min(left, area.Right - prompt.Width)), Math.Max(area.Top, Math.Min(top, area.Bottom - prompt.Height)));
            if (prompt.ShowDialog(this) != DialogResult.OK)
            {
                WriteLog("INFO", "Browser download canceled in Fetchflow: " + filename);
                await CancelBrowserDownload(item);
                return;
            }
            string targetPath;
            try
            {
                targetPath = Path.GetFullPath(pathBox.Text.Trim());
                if (String.IsNullOrWhiteSpace(Path.GetFileName(targetPath))) throw new InvalidOperationException("Choose a file name for this download.");
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Download path", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            await ApplyBrowserDownloadPath(item, targetPath);
        }
    }

    private async Task<string> ResolveBrowserFilename(Uri url, string browserName)
    {
        string browserLeaf = SafeFileName(Path.GetFileName(browserName ?? ""));
        bool usefulBrowserName = !String.IsNullOrWhiteSpace(browserName) && !browserLeaf.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) && !browserLeaf.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);
        try
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, url))
            using (HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                ContentDispositionHeaderValue disposition = response.Content.Headers.ContentDisposition;
                if (disposition != null && (!String.IsNullOrWhiteSpace(disposition.FileNameStar) || !String.IsNullOrWhiteSpace(disposition.FileName)))
                    return ResponseFilename(response, response.RequestMessage == null ? url : response.RequestMessage.RequestUri);
                if (usefulBrowserName) return browserLeaf;
                return ResponseFilename(response, response.RequestMessage == null ? url : response.RequestMessage.RequestUri);
            }
        }
        catch { return usefulBrowserName ? browserLeaf : FileNameFromUri(url); }
    }

    private async Task CancelBrowserDownload(NativeDownload item)
    {
        CancellationTokenSource token;
        if (transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        while (transfers.ContainsKey(item.Id)) await Task.Delay(80);
        if (!String.IsNullOrWhiteSpace(item.FilePath))
        {
            TryDelete(item.FilePath);
            TryDelete(item.FilePath + ".fetchflow-part");
            TryDelete(item.FilePath + ".part");
        }
        downloads.Remove(item); selectedIds.Remove(item.Id);
        SaveState(); RenderDownloads(); PumpQueue();
    }

    private async Task ApplyBrowserDownloadPath(NativeDownload item, string targetPath)
    {
        string oldPath = item.FilePath;
        if (String.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { RenderDownloads(); return; }
        bool finished = String.Equals(item.Status, "finished", StringComparison.OrdinalIgnoreCase);
        CancellationTokenSource token;
        if (!finished && transfers.TryGetValue(item.Id, out token)) { item.Status = "paused"; token.Cancel(); }
        while (transfers.ContainsKey(item.Id)) await Task.Delay(80);
        string folder = Path.GetDirectoryName(targetPath);
        Directory.CreateDirectory(folder);
        string name = Path.GetFileName(targetPath);
        if (File.Exists(targetPath) || File.Exists(targetPath + ".fetchflow-part")) targetPath = ChoosePath(folder, name);
        if (finished)
        {
            if (File.Exists(oldPath)) File.Move(oldPath, targetPath);
        }
        else
        {
            string oldPart = oldPath + ".fetchflow-part";
            if (File.Exists(oldPart)) File.Move(oldPart, targetPath + ".fetchflow-part");
        }
        item.FilePath = targetPath; item.Folder = folder; item.Filename = Path.GetFileName(targetPath);
        if (!finished)
        {
            item.Status = "queued"; item.Error = ""; SaveState();
            if (!StartOne(item, true)) item.Status = "queued";
        }
        SaveState(); RenderDownloads();
    }

    private void NotifyBrowserHandoff(string handoff, string decision)
    {
        if (String.IsNullOrWhiteSpace(handoff) || !Regex.IsMatch(handoff, "^[a-fA-F0-9]{32}$")) return;
        try
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", "FetchflowBrowser" + handoff, PipeDirection.Out))
            {
                pipe.Connect(5000);
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                {
                    writer.WriteLine(decision);
                    writer.Flush();
                }
            }
        }
        catch (Exception error) { WriteLog("WARN", "Could not finish Chrome download handoff: " + error.Message); }
    }

    private static string GetDownloadsFolder()
    {
        Guid downloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref downloadsId, 0, IntPtr.Zero, out pathPointer) == 0 && pathPointer != IntPtr.Zero)
            {
                string path = Marshal.PtrToStringUni(pathPointer);
                if (!String.IsNullOrWhiteSpace(path)) return path;
            }
        }
        catch { }
        finally { if (pathPointer != IntPtr.Zero) CoTaskMemFree(pathPointer); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

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
        menu.Items.Add("Open Fetchflow Download Manager", null, delegate { RestoreFromTray(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Fetchflow Download Manager", null, delegate { exitRequested = true; Close(); });
        trayIcon = new NotifyIcon { Icon = Icon, Text = "Fetchflow Download Manager", ContextMenuStrip = menu, Visible = false };
        trayIcon.DoubleClick += delegate { RestoreFromTray(); };
    }

    private void HideToTray()
    {
        WriteLog("INFO", "Window hidden to the notification area; active downloads continue.");
        if (trayIcon != null) trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (trayIcon != null) trayIcon.Visible = false;
        WriteLog("INFO", "Fetchflow window restored from the notification area.");
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
            if (item.Status == "retrying") { item.Status = "queued"; item.RetryAt = ""; item.Error = "Retry queued; start the queue to continue."; }
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
                        return new NativeDownload { Id = item.Id, Url = item.Url, Filename = item.Filename, Host = item.Host, Folder = item.Folder, FilePath = item.FilePath, Size = item.Size, Bytes = item.Bytes, AcceptsRanges = item.AcceptsRanges, Status = item.Status, Error = item.Error, AddedAt = item.AddedAt, ETag = item.ETag, LastModified = item.LastModified, SegmentedMode = item.SegmentedMode, CompletedRanges = (item.CompletedRanges ?? new List<long[]>()).Select(r => new long[] { r[0], r[1] }).ToList(), RetryCount = item.RetryCount, RetryAt = item.RetryAt, IsYoutube = item.IsYoutube, MediaMode = item.MediaMode, MediaSelector = item.MediaSelector, MediaExtension = item.MediaExtension, MediaAudioQuality = item.MediaAudioQuality };
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
        downloadGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Speed", HeaderText = "SPEED", Width = 95 });
        downloadGrid.CellMouseDown += DownloadGridCellMouseDown;
        downloadGrid.CurrentCellChanged += delegate
        {
            if (downloadGrid.CurrentCell == null || downloadGrid.CurrentRow == null) return;
            NativeDownload focused = downloadGrid.CurrentRow.Tag as NativeDownload;
            if (focused != null)
            {
                focusedDownloadId = focused.Id;
                focusedDownloadColumn = downloadGrid.CurrentCell.ColumnIndex;
            }
        };
        downloadGrid.CurrentCellDirtyStateChanged += delegate { if (downloadGrid.IsCurrentCellDirty) downloadGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        downloadGrid.CellValueChanged += DownloadGridCellValueChanged;
        layout.Controls.Add(downloadGrid, 0, 2);
        Label note = new Label { Text = "Right-click a download to start it now, pause, resume, refresh, remove, or delete it.", AutoSize = true, ForeColor = Color.FromArgb(108, 127, 139), Padding = new Padding(3, 8, 0, 0) };
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
        Label instruction = new Label { Text = "Paste direct file links or public YouTube video links · only download content you own or are authorized to save", ForeColor = Color.FromArgb(174, 192, 200), AutoSize = true, Padding = new Padding(2, 8, 0, 0) };
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
        grabGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quality", HeaderText = "FORMAT / QUALITY", Width = 230 });
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
        downloadMenu.Items.Add("Download now", null, delegate { RunContextAction("download-now"); });
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
            downloadMenu.Items[0].Enabled = item.Status == "queued" || item.Status == "paused" || item.Status == "error";
            downloadMenu.Items[1].Enabled = item.Status == "downloading" || item.Status == "queued";
            downloadMenu.Items[2].Enabled = item.Status == "paused" || item.Status == "error";
            downloadMenu.Items[3].Enabled = !item.IsYoutube && (item.Status == "downloading" || item.Status == "paused" || item.Status == "error");
            downloadMenu.Items[5].Enabled = true;
            downloadMenu.Items[6].Enabled = !String.IsNullOrWhiteSpace(item.FilePath);
        };
    }

    private void DownloadGridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.Button != MouseButtons.Right) return;
        downloadGrid.ClearSelection();
        downloadGrid.Rows[e.RowIndex].Selected = true;
        NativeDownload item = downloadGrid.Rows[e.RowIndex].Tag as NativeDownload;
        contextDownloadId = item == null ? null : item.Id;
        if (item != null)
        {
            focusedDownloadId = item.Id;
            focusedDownloadColumn = Math.Max(0, e.ColumnIndex);
            downloadGrid.CurrentCell = downloadGrid.Rows[e.RowIndex].Cells[focusedDownloadColumn];
        }
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
        if (e.RowIndex < 0 || e.RowIndex >= results.Count) return;
        if (e.ColumnIndex == 0) results[e.RowIndex].Selected = Convert.ToBoolean(grabGrid.Rows[e.RowIndex].Cells[0].Value);
        else if (e.ColumnIndex == 2) results[e.RowIndex].SelectedFormatKey = Convert.ToString(grabGrid.Rows[e.RowIndex].Cells[2].Value);
        addSelectedButton.Enabled = results.Any(r => r.Selected && String.IsNullOrEmpty(r.Error));
    }

    private void RenderDownloads()
    {
        if (downloadGrid == null) return;
        string restoreId = focusedDownloadId;
        int restoreColumn = focusedDownloadColumn;
        if (downloadGrid.CurrentCell != null)
        {
            restoreColumn = downloadGrid.CurrentCell.ColumnIndex;
            NativeDownload current = downloadGrid.CurrentRow == null ? null : downloadGrid.CurrentRow.Tag as NativeDownload;
            if (current != null) restoreId = current.Id;
        }
        downloadGrid.Rows.Clear();
        foreach (NativeDownload item in downloads)
        {
            int row = downloadGrid.Rows.Add(selectedIds.Contains(item.Id), item.Filename, FormatBytes(item.Size), item.Host, StatusText(item), ProgressText(item), "0 B/s");
            downloadGrid.Rows[row].Tag = item;
            downloadGrid.Rows[row].Cells[1].ToolTipText = item.Error ?? item.Url;
        }
        downloadGrid.ClearSelection();
        int focusRow = downloads.FindIndex(item => item.Id == restoreId);
        if (focusRow < 0 && downloads.Count > 0) focusRow = 0;
        if (focusRow >= 0 && focusRow < downloadGrid.Rows.Count)
        {
            DataGridViewRow row = downloadGrid.Rows[focusRow];
            focusedDownloadId = ((NativeDownload)row.Tag).Id;
            focusedDownloadColumn = Math.Max(0, Math.Min(restoreColumn, row.Cells.Count - 1));
            row.Selected = true;
            downloadGrid.CurrentCell = row.Cells[focusedDownloadColumn];
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
            long fileSpeed = item.Status == "downloading" ? Math.Max(0, item.IsYoutube ? Interlocked.Read(ref item.CurrentSpeed) : SampleCurrentSpeed(item)) : 0;
            downloadGrid.Rows[i].Cells[6].Value = FormatBytes(fileSpeed) + "/s";
            totalSpeed += fileSpeed;
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
            int row = grabGrid.Rows.Add(result.Selected && String.IsNullOrEmpty(result.Error), result.Filename, result.IsYoutube ? "Loading formats…" : "Direct file", result.Host, result.Size > 0 ? FormatBytes(result.Size) : "Unknown", result.Ranges ? "Supported" : "Handled by yt-dlp", String.IsNullOrEmpty(result.Error) ? "Online" : result.Error);
            grabGrid.Rows[row].Tag = result;
            if (result.IsYoutube && result.Formats != null && result.Formats.Count > 0)
            {
                DataGridViewComboBoxCell formatCell = new DataGridViewComboBoxCell { DataSource = result.Formats, DisplayMember = "Label", ValueMember = "Key", FlatStyle = FlatStyle.Flat };
                grabGrid.Rows[row].Cells[2] = formatCell;
                formatCell.Value = result.SelectedFormatKey;
            }
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
            if (item.Status == "queued" || item.Status == "retrying") { item.Status = "paused"; item.RetryAt = ""; item.Error = ""; }
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
                NativeDownload next = downloads.Where(d => d.Status == "queued" && (!runningHosts.ContainsKey(d.Host) || runningHosts[d.Host] < settings.PerHost)).OrderByDescending(d => d.RetryCount > 0).FirstOrDefault();
                if (downloads.Any(d => d.Status == "retrying" && d.RetryCount <= MaxAutomaticRetries)) break;
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
            if (item.Status != "paused" && item.Status != "retrying") { item.Status = "error"; item.Error = task.Exception == null ? "Download canceled." : task.Exception.GetBaseException().Message; }
        }
        else { item.Status = "finished"; item.Error = ""; item.RetryCount = 0; item.RetryAt = ""; item.Bytes = item.Size; item.CompletedRanges = new List<long[]>(); item.SegmentedMode = false; }
        SaveState(); RenderDownloads();
        if (item.Status == "retrying")
        {
            if (item.RetryCount <= MaxAutomaticRetries)
            {
                int delay = RetryDelaySeconds(item.RetryCount);
                WriteLog("WARN", item.Host + ": temporary download failure; retry " + item.RetryCount + " of " + MaxAutomaticRetries + " scheduled in " + delay + " seconds. " + item.Error);
                ScheduleRetry(item, delay);
            }
            else
            {
                item.Status = "error";
                item.RetryAt = "";
                item.Error = "Automatic retries exhausted after " + MaxAutomaticRetries + " retries. " + item.Error;
                WriteLog("ERROR", item.Host + ": automatic retries exhausted; moving to the next queued download.");
                SaveState(); RenderDownloads();
            }
        }
        PumpQueue();
    }

    private async Task Transfer(NativeDownload item, CancellationToken cancel)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(item.Folder)) item.Folder = settings.Folder;
            if (String.IsNullOrWhiteSpace(item.FilePath)) item.FilePath = ChoosePath(item.Folder, item.Filename);
            string part = item.FilePath + ".fetchflow-part";
            if (item.IsYoutube)
            {
                await TransferYoutube(item, cancel);
                return;
            }
            bool hasSavedSegmentState = item.SegmentedMode || (item.CompletedRanges != null && item.CompletedRanges.Count > 0);
            if (await TransferSegments(item, part, cancel))
            {
                FinalizeFile(item, part);
                return;
            }
            if (hasSavedSegmentState)
                throw new IOException("The server no longer supports the saved ranged download. The partial file was kept; Fetchflow will not restart it from the beginning automatically.");
            await TransferSingle(item, part, cancel);
            FinalizeFile(item, part);
        }
        catch (OperationCanceledException error)
        {
            if (cancel.IsCancellationRequested) { item.Status = "paused"; item.Error = ""; }
            else MarkTransferFailure(item, error);
            throw;
        }
        catch (Exception error)
        {
            MarkTransferFailure(item, error);
            throw;
        }
        finally { SaveState(); }
    }

    private static void MarkTransferFailure(NativeDownload item, Exception error)
    {
        if (IsTransientDownloadError(error))
        {
            item.RetryCount++;
            int delay = RetryDelaySeconds(item.RetryCount);
            item.Status = "retrying";
            item.RetryAt = DateTime.UtcNow.AddSeconds(delay).ToString("o");
            item.Error = "Temporary connection/server issue after " + FormatBytes(item.Bytes) + " saved progress. Retry " + item.RetryCount + " in " + delay + " seconds: " + error.Message;
        }
        else { item.Status = "error"; item.Error = error.Message; }
    }

    private static int RetryDelaySeconds(int attempt) { return Math.Min(60, 3 * (int)Math.Pow(2, Math.Min(5, Math.Max(0, attempt - 1)))); }

    private static bool IsTransientDownloadError(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException || current is SocketException || current is TimeoutException || current is EndOfStreamException) return true;
            if (current is TaskCanceledException) return true;
            string message = current.Message ?? "";
            ObjectDisposedException disposed = current as ObjectDisposedException;
            if (disposed != null && (String.Equals(disposed.ObjectName, "System.Net.Security.SslStream", StringComparison.OrdinalIgnoreCase) || message.IndexOf("SslStream", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            Match status = Regex.Match(message, @"(?:HTTP(?:\s+Error)?|responded)\s+(\d{3})", RegexOptions.IgnoreCase);
            if (status.Success)
            {
                int code;
                if (Int32.TryParse(status.Groups[1].Value, out code)) return code == 408 || code == 425 || code == 429 || code >= 500;
            }
            if (current is IOException && (message.IndexOf("closed a file range before all requested bytes arrived", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("connection was closed", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("unexpected end", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
        }
        return false;
    }

    private void ScheduleRetry(NativeDownload item, int seconds)
    {
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try
            {
                if (IsDisposed || !IsHandleCreated || closing) return;
                BeginInvoke((Action)(() =>
                {
                    if (closing || !downloads.Contains(item) || item.Status != "retrying") return;
                    if (!StartOne(item)) ScheduleRetry(item, 1);
                    RenderDownloads();
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    private async Task<bool> TransferSegments(NativeDownload item, string part, CancellationToken cancel)
    {
        int connections = Clamp(settings.ConnectionsPerFile, 1, 16);
        if ((connections < 2 && !item.SegmentedMode && (item.CompletedRanges == null || item.CompletedRanges.Count == 0)) || !item.AcceptsRanges || item.Size <= 0) return false;
        List<long[]> ranges = item.CompletedRanges ?? new List<long[]>();
        bool serverReturnsThroughEnd = false;
        if (ranges.Count == 0 && !item.SegmentedMode && File.Exists(part))
        {
            long existing = Math.Min(item.Size, new FileInfo(part).Length);
            if (existing > 0) ranges.Add(new long[] { 0, existing - 1 });
        }
        using (HttpResponseMessage probe = await GetRange(item.Url, 0, 0, null, cancel))
        {
            ContentRangeHeaderValue contentRange = probe.Content.Headers.ContentRange;
            string rangeText = contentRange == null ? "missing" : contentRange.ToString();
            WriteLog("INFO", item.Host + ": resume probe returned HTTP " + (int)probe.StatusCode + ", Content-Range " + rangeText + ", saved size " + item.Size + ".");
            bool validProbe = probe.StatusCode == HttpStatusCode.PartialContent && contentRange != null && contentRange.From == 0 && contentRange.To >= 0 && (!contentRange.Length.HasValue || contentRange.To < contentRange.Length.Value);
            if (!validProbe)
            {
                if (item.SegmentedMode || ranges.Count > 0) throw new IOException("Could not verify ranged resume: server returned HTTP " + (int)probe.StatusCode + " with Content-Range " + rangeText + "; expected HTTP 206 for bytes 0-0. The partial file was kept.");
                return false;
            }
            if (contentRange.Length.HasValue && item.Size > 0 && contentRange.Length.Value != item.Size)
            {
                if (item.SegmentedMode || ranges.Count > 0) throw new IOException("The server reports a different file size (" + FormatBytes(contentRange.Length.Value) + ") than the saved download (" + FormatBytes(item.Size) + "). The partial file was kept because its contents cannot be safely matched.");
                item.Size = contentRange.Length.Value;
                WriteLog("WARN", item.Host + ": updated the unsaved download size from the server's range response.");
            }
            serverReturnsThroughEnd = item.Size > 0 && contentRange.To >= item.Size - 1;
            if (contentRange.To > 0) WriteLog("INFO", item.Host + ": server returned a wider range than requested; Fetchflow will adapt its range strategy.");
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
        if (serverReturnsThroughEnd)
        {
            // Some hosts ignore the requested end and stream from the requested start through EOF.
            // Read that tail once, instead of opening overlapping streams and disposing them early.
            long firstMissingByte = todo[0][0];
            todo = new List<long[]> { new long[] { firstMissingByte, item.Size - 1 } };
            connections = 1;
            WriteLog("INFO", item.Host + ": server sends ranges through EOF; resuming from byte " + firstMissingByte + " in one stream.");
        }
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
            if (response.StatusCode != HttpStatusCode.PartialContent || actual == null || actual.From != start || actual.To < end || (actual.Length.HasValue && actual.To >= actual.Length.Value))
            {
                string rangeText = actual == null ? "missing" : actual.ToString();
                throw new IOException("The server returned HTTP " + (int)response.StatusCode + " with Content-Range " + rangeText + " instead of the requested bytes " + start + "-" + end + ". The partial file was kept.");
            }
            if (actual.Length.HasValue && item.Size > 0 && actual.Length.Value != item.Size)
                throw new IOException("The server reported a different total file size during resume. The partial file was kept.");
            if (actual.To != end) WriteLog("INFO", item.Host + ": requested bytes " + start + "-" + end + ", server supplied " + actual.From + "-" + actual.To + "; reading only the requested segment.");
            WriteLog("INFO", item.Host + ": opened range stream at byte " + start + " through byte " + actual.To + ".");
            string tag = response.Headers.ETag == null ? "" : response.Headers.ETag.ToString();
            if (!String.IsNullOrEmpty(item.ETag) && !String.IsNullOrEmpty(tag) && item.ETag != tag) throw new IOException("The server returned changed content for a file range.");
            using (Stream input = await response.Content.ReadAsStreamAsync())
            using (FileStream output = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                output.Position = start;
                byte[] buffer = new byte[64 * 1024]; long position = start; long lastStateSavePosition = start; Stopwatch watch = Stopwatch.StartNew(); long throttledBytes = 0;
                while (position <= end)
                {
                    int requested = (int)Math.Min((long)buffer.Length, end + 1 - position);
                    int read = await ReadWithIdleTimeout(input, buffer, 0, requested, cancel);
                    if (read == 0) break;
                    await output.WriteAsync(buffer, 0, read, cancel);
                    position += read; throttledBytes += read;
                    lock (item)
                    {
                        // Record written bytes, not just fully completed segments, so a retry resumes at the gap.
                        item.CompletedRanges = MergeRanges((item.CompletedRanges ?? new List<long[]>()).Concat(new[] { new long[] { start, position - 1 } }).ToList());
                        active[slot] = 0;
                        item.Bytes = CountRanges(item.CompletedRanges) + active.Sum();
                    }
                    if (position - lastStateSavePosition >= 8 * 1024 * 1024)
                    {
                        SaveState();
                        lastStateSavePosition = position;
                        WriteLog("INFO", item.Host + ": saved range progress through byte " + (position - 1) + ".");
                    }
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
            response.Dispose();
            throw new IOException("The server no longer supports ranged resume. The partial file was kept; Fetchflow will not restart from the beginning automatically.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new IOException("Server responded " + (int)response.StatusCode + " " + response.ReasonPhrase + ".");
            if (offset > 0 && (response.Content.Headers.ContentRange == null || response.Content.Headers.ContentRange.From != offset)) throw new IOException("The server did not resume at the saved byte offset.");
            if (response.Content.Headers.ContentRange != null && response.Content.Headers.ContentRange.Length.HasValue)
            {
                long serverSize = response.Content.Headers.ContentRange.Length.Value;
                if (offset > 0 && item.Size > 0 && serverSize != item.Size) throw new IOException("The server reports a different file size than the saved partial download. The partial file was kept.");
                item.Size = serverSize;
            }
            else if (response.Content.Headers.ContentLength.HasValue && offset == 0) item.Size = response.Content.Headers.ContentLength.Value;
            if (response.Headers.ETag != null) item.ETag = response.Headers.ETag.ToString();
            if (response.Content.Headers.LastModified.HasValue) item.LastModified = response.Content.Headers.LastModified.Value.ToString("R");
            using (Stream input = await response.Content.ReadAsStreamAsync())
            using (FileStream output = new FileStream(part, offset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                output.Position = offset; byte[] buffer = new byte[64 * 1024]; Stopwatch watch = Stopwatch.StartNew(); long bytes = 0;
                while (true)
                {
                    int read = await ReadWithIdleTimeout(input, buffer, 0, buffer.Length, cancel);
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
        try
        {
            using (CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                headers.CancelAfter(TimeSpan.FromSeconds(45));
                try { return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
                catch (OperationCanceledException error)
                {
                    if (cancel.IsCancellationRequested) throw;
                    throw new TimeoutException("The server did not send response headers within 45 seconds.", error);
                }
            }
        }
        catch { request.Dispose(); throw; }
    }

    private static async Task<int> ReadWithIdleTimeout(Stream input, byte[] buffer, int offset, int count, CancellationToken cancel)
    {
        using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            idle.CancelAfter(TimeSpan.FromSeconds(45));
            try { return await input.ReadAsync(buffer, offset, count, idle.Token); }
            catch (OperationCanceledException error)
            {
                if (cancel.IsCancellationRequested) throw;
                throw new TimeoutException("The connection delivered no download data for 45 seconds.", error);
            }
        }
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
            string ytDlp = null;
            string youtubeSetupError = null;
            if (urls.Any(IsYoutubeUrl))
            {
                checkLinksButton.Text = "Preparing YouTube support…";
                try { ytDlp = await EnsureYoutubeExtractor(); }
                catch (Exception error) { youtubeSetupError = "Could not set up the official YouTube media helper: " + error.Message; WriteLog("ERROR", youtubeSetupError); }
            }
            using (SemaphoreSlim limit = new SemaphoreSlim(8))
            {
                int completed = 0;
                List<Task<GrabResult>> tasks = urls.Select(async url =>
                {
                    await limit.WaitAsync();
                    try
                    {
                        if (IsYoutubeUrl(url))
                        {
                            if (ytDlp == null) return new GrabResult { Url = url, Host = "youtube.com", Filename = "YouTube video", Error = youtubeSetupError ?? "YouTube support is unavailable." };
                            return await InspectYoutube(url, ytDlp);
                        }
                        return await InspectLink(url);
                    }
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

    private static bool IsYoutubeUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtu.be" || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
    }

    private async Task<string> EnsureYoutubeExtractor()
    {
        string toolFolder = Path.Combine(appData, "tools");
        string target = Path.Combine(toolFolder, "yt-dlp.exe");
        string temp = target + ".download";
        Directory.CreateDirectory(toolFolder);
        const string sumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
        const string binaryUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        string sums = await Http.GetStringAsync(sumsUrl);
        Match match = Regex.Match(sums, @"(?im)^([a-f0-9]{64})\s+\*?yt-dlp\.exe\s*$");
        if (!match.Success) throw new InvalidDataException("The official release checksum list did not contain yt-dlp.exe.");
        string expected = match.Groups[1].Value.ToLowerInvariant();
        if (File.Exists(target) && String.Equals(FileHash(target), expected, StringComparison.OrdinalIgnoreCase)) return target;
        WriteLog("INFO", "Downloading yt-dlp from the official yt-dlp GitHub release and verifying SHA-256.");
        try
        {
            using (HttpResponseMessage response = await Http.GetAsync(binaryUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output);
            }
            if (!String.Equals(FileHash(temp), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded yt-dlp file did not match the official SHA-256 checksum.");
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            WriteLog("INFO", "Official yt-dlp helper is ready.");
            return target;
        }
        catch { TryDelete(temp); throw; }
    }

    private static string FileHash(string path)
    {
        using (SHA256 hash = SHA256.Create())
        using (FileStream input = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    private async Task<GrabResult> InspectYoutube(string value, string ytDlp)
    {
        Uri uri = new Uri(value);
        GrabResult result = new GrabResult { Url = value, Host = uri.Host, IsYoutube = true, Ranges = false };
        string stdout = "";
        string stderr = "";
        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo { FileName = ytDlp, Arguments = "--no-playlist --no-warnings --skip-download --dump-single-json " + QuoteArgument(value), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task wait = Task.Run(() => process.WaitForExit());
            if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(90))) != wait)
            {
                try { process.Kill(); } catch { }
                result.Error = "YouTube format inspection timed out after 90 seconds.";
                WriteLog("WARN", "YouTube inspection timed out for " + uri.Host + ".");
                return result;
            }
            stdout = await output; stderr = await errors;
            if (process.ExitCode != 0) { result.Error = SafeExtractorError(stderr); return result; }
        }
        try
        {
            byte[] json = Encoding.UTF8.GetBytes(stdout);
            using (MemoryStream stream = new MemoryStream(json))
            {
                YtVideoInfo info = (YtVideoInfo)new DataContractJsonSerializer(typeof(YtVideoInfo)).ReadObject(stream);
                result.Filename = SafeFileName(String.IsNullOrWhiteSpace(info.Title) ? "YouTube video" : info.Title);
                result.Formats = BuildYoutubeFormats(info.Formats ?? new List<YtVideoFormat>(), FindFfmpeg() != null);
                result.Selected = result.Formats.Count > 0;
                result.SelectedFormatKey = result.Formats.Count == 0 ? null : result.Formats[0].Key;
                if (!result.Selected) result.Error = "No downloadable formats were reported for this video.";
            }
        }
        catch (Exception error) { result.Error = "Could not read YouTube format details: " + error.Message; }
        WriteLog(String.IsNullOrEmpty(result.Error) ? "INFO" : "WARN", "YouTube formats " + uri.Host + ": " + (String.IsNullOrEmpty(result.Error) ? result.Formats.Count + " choices found." : result.Error));
        return result;
    }

    private static List<YtFormatChoice> BuildYoutubeFormats(List<YtVideoFormat> formats, bool hasFfmpeg)
    {
        List<YtFormatChoice> choices = new List<YtFormatChoice>();
        Func<YtVideoFormat, bool> hasVideo = f => !String.IsNullOrEmpty(f.VideoCodec) && f.VideoCodec != "none";
        Func<YtVideoFormat, bool> hasAudio = f => !String.IsNullOrEmpty(f.AudioCodec) && f.AudioCodec != "none";
        foreach (YtVideoFormat f in formats.Where(f => hasVideo(f) && hasAudio(f) && !String.IsNullOrWhiteSpace(f.Id)).GroupBy(f => (f.HeightValue > 0 ? f.HeightValue.ToString() : "0") + "|" + f.Extension).Select(g => g.OrderByDescending(x => x.AudioBitrateValue).First()).OrderByDescending(f => f.HeightValue).Take(18))
        {
            string ext = SafeExtension(f.Extension);
            string label = "Video · " + (f.HeightValue > 0 ? f.HeightValue + "p" : "Available quality") + " · " + ext;
            choices.Add(new YtFormatChoice { Key = "video|" + f.Id, Label = label, Selector = f.Id, Mode = "video", Extension = ext });
        }
        foreach (YtVideoFormat f in formats.Where(f => hasVideo(f) && !hasAudio(f) && !String.IsNullOrWhiteSpace(f.Id)).GroupBy(f => f.HeightValue).Select(g => g.OrderByDescending(x => x.Id).First()).OrderByDescending(f => f.HeightValue).Take(12))
        {
            string ext = "mp4";
            string mergeLabel = hasFfmpeg ? " · merge audio" : " · merge audio (FFmpeg required)";
            choices.Add(new YtFormatChoice { Key = "video-merge|" + f.Id, Label = "Video · " + (f.HeightValue > 0 ? f.HeightValue + "p" : "High quality") + " · " + ext + mergeLabel, Selector = f.Id + "+bestaudio", Mode = "video", Extension = ext, RequiresFfmpeg = true });
        }
        foreach (YtVideoFormat f in formats.Where(f => !hasVideo(f) && hasAudio(f) && !String.IsNullOrWhiteSpace(f.Id)).GroupBy(f => SafeExtension(f.Extension) + "|" + Math.Round(f.AudioBitrateValue / 16) * 16).Select(g => g.OrderByDescending(x => x.AudioBitrateValue).First()).OrderByDescending(f => f.AudioBitrateValue).Take(12))
        {
            string ext = SafeExtension(f.Extension);
            choices.Add(new YtFormatChoice { Key = "audio|" + f.Id, Label = "Audio · " + ext.ToUpperInvariant() + (f.AudioBitrateValue > 0 ? " · " + Math.Round(f.AudioBitrateValue) + " kbps" : ""), Selector = f.Id, Mode = "audio", Extension = ext });
        }
        foreach (string bitrate in new[] { "320k", "256k", "192k", "128k" }) choices.Add(new YtFormatChoice { Key = "mp3|" + bitrate, Label = "Audio · MP3 · " + bitrate, Selector = "bestaudio", Mode = "mp3", Extension = "mp3", AudioQuality = bitrate, RequiresFfmpeg = true });
        return choices;
    }

    private static string SafeExtension(string extension)
    {
        string value = (extension ?? "").ToLowerInvariant();
        return Regex.IsMatch(value, "^[a-z0-9]{1,8}$") ? value : "mp4";
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private static string SafeExtractorError(string message)
    {
        string safe = Regex.Replace(message ?? "", @"https?://\S+", "[link]", RegexOptions.IgnoreCase);
        string[] lines = safe.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string detail = lines.LastOrDefault(line => line.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase) >= 0) ?? lines.LastOrDefault() ?? "The media helper could not read this video.";
        return Regex.Replace(detail.Trim(), "ERROR:", "", RegexOptions.IgnoreCase).Trim();
    }

    private async Task TransferYoutube(NativeDownload item, CancellationToken cancel)
    {
        string ytDlp = Path.Combine(appData, "tools", "yt-dlp.exe");
        if (!File.Exists(ytDlp)) ytDlp = await EnsureYoutubeExtractor();
        if ((item.MediaMode == "mp3" || item.MediaSelector.Contains("+")) && FindFfmpeg() == null)
            throw new InvalidOperationException("This format needs FFmpeg. Install FFmpeg and restart Fetchflow, or choose an audio format that does not need conversion.");
        if (String.IsNullOrWhiteSpace(item.Folder)) item.Folder = settings.Folder;
        if (String.IsNullOrWhiteSpace(item.FilePath)) item.FilePath = ChoosePath(item.Folder, item.Filename);
        Directory.CreateDirectory(item.Folder);
        string stem = Path.Combine(Path.GetDirectoryName(item.FilePath), Path.GetFileNameWithoutExtension(item.FilePath));
        string outputTemplate = stem + ".%(ext)s";
        string args = "--no-playlist --newline --no-warnings --continue --windows-filenames --format " + QuoteArgument(item.MediaSelector) +
            " --output " + QuoteArgument(outputTemplate) +
            " --progress --progress-delta 0.5 --output-na-placeholder NA" +
            " --progress-template " + QuoteArgument("download:FFPROGRESS:%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress._percent_str)s") +
            " --print " + QuoteArgument("after_move:FFPATH:%(filepath)s");
        if (item.MediaMode == "mp3") args += " --extract-audio --audio-format mp3 --audio-quality " + QuoteArgument(String.IsNullOrWhiteSpace(item.MediaAudioQuality) ? "192k" : item.MediaAudioQuality);
        if (item.MediaMode == "video" && item.MediaSelector.Contains("+")) args += " --merge-output-format " + SafeExtension(item.MediaExtension);
        string ffmpeg = FindFfmpeg();
        if (ffmpeg != null && (item.MediaMode == "mp3" || item.MediaSelector.Contains("+"))) args += " --ffmpeg-location " + QuoteArgument(Path.GetDirectoryName(ffmpeg));
        args += " " + QuoteArgument(item.Url);
        StringBuilder output = new StringBuilder();
        object outputLock = new object();
        string finalPath = null;
        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo { FileName = ytDlp, Arguments = args, WorkingDirectory = item.Folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            DataReceivedEventHandler lineHandler = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("FFPROGRESS:", StringComparison.Ordinal))
                {
                    string[] fields = e.Data.Substring(11).Split('|');
                    double parsed;
                    long downloaded;
                    if (fields.Length >= 1 && Double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        downloaded = Math.Max(0, (long)parsed);
                        item.Bytes = downloaded;
                    }
                    long total;
                    if (fields.Length >= 3 && Int64.TryParse(fields[1], out total) && total > 0) item.Size = total;
                    else if (fields.Length >= 3 && Int64.TryParse(fields[2], out total) && total > 0) item.Size = total;
                    double speed;
                    if (fields.Length >= 4 && Double.TryParse(fields[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out speed)) Interlocked.Exchange(ref item.CurrentSpeed, Math.Max(0, (long)speed));
                    // Some extractors cannot estimate the final size. Use their reported
                    // percentage to provide a useful approximate total in the UI.
                    if (item.Size <= item.Bytes && fields.Length >= 5)
                    {
                        string percentText = fields[4].Replace("%", "").Trim();
                        double percent;
                        if (Double.TryParse(percentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent) && percent > 0 && percent <= 100)
                            item.Size = Math.Max(item.Bytes, (long)(item.Bytes * 100.0 / percent));
                    }
                    return;
                }
                if (e.Data.StartsWith("FFPATH:", StringComparison.Ordinal)) { finalPath = e.Data.Substring(7).Trim().Trim('"'); return; }
                lock (outputLock) output.AppendLine(Regex.Replace(e.Data, @"https?://\S+", "[link]", RegexOptions.IgnoreCase));
            };
            process.OutputDataReceived += lineHandler;
            process.ErrorDataReceived += lineHandler;
            process.Start();
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Task wait = Task.Run(() => process.WaitForExit());
            while (await Task.WhenAny(wait, Task.Delay(250)) != wait)
            {
                if (cancel.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    throw new OperationCanceledException(cancel);
                }
            }
            process.WaitForExit();
            if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
            if (process.ExitCode != 0)
            {
                string detail;
                lock (outputLock) detail = SafeExtractorError(output.ToString());
                Match status = Regex.Match(detail, @"HTTP(?:\s+Error)?\s+(408|425|429|5\d\d)", RegexOptions.IgnoreCase);
                if (status.Success) throw new HttpRequestException(detail);
                if (detail.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("connection reset", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("temporary failure", StringComparison.OrdinalIgnoreCase) >= 0) throw new TimeoutException(detail);
                throw new IOException(String.IsNullOrWhiteSpace(detail) ? "yt-dlp exited with code " + process.ExitCode + "." : detail);
            }
        }
        if (String.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
        {
            string expected = stem + "." + SafeExtension(item.MediaExtension);
            if (File.Exists(expected)) finalPath = expected;
        }
        if (String.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath)) throw new IOException("yt-dlp finished but the selected output file could not be found.");
        item.FilePath = Path.GetFullPath(finalPath);
        item.Filename = Path.GetFileName(item.FilePath);
        item.Size = new FileInfo(item.FilePath).Length;
        item.Bytes = item.Size;
        Interlocked.Exchange(ref item.CurrentSpeed, 0);
    }

    private static string FindFfmpeg()
    {
        try
        {
            string besideApp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(besideApp)) return besideApp;
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }

        string pathValue;
        try { pathValue = Environment.GetEnvironmentVariable("PATH") ?? ""; }
        catch { return null; }
        foreach (string rawFolder in pathValue.Split(Path.PathSeparator))
        {
            string folder = (rawFolder ?? "").Trim().Trim('"');
            if (String.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                string candidate = Path.Combine(folder, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            catch (System.Security.SecurityException) { }
        }
        return null;
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
        int added = 0;
        foreach (GrabResult result in results.Where(r => r.Selected && String.IsNullOrEmpty(r.Error)))
        {
            YtFormatChoice format = result.IsYoutube && result.Formats != null ? result.Formats.FirstOrDefault(f => f.Key == result.SelectedFormatKey) : null;
            if (result.IsYoutube && format == null) continue;
            if (result.IsYoutube && format.RequiresFfmpeg && FindFfmpeg() == null)
            {
                MessageBox.Show(this, "The selected format needs FFmpeg. Install FFmpeg and add ffmpeg.exe to PATH, then restart Fetchflow. Other listed formats can be added without it.", "FFmpeg required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }
            string filename = result.IsYoutube ? SafeFileName(result.Filename + "." + format.Extension) : SafeFileName(result.Filename);
            if (downloads.Any(d => d.Url == result.Url && (!result.IsYoutube || (d.IsYoutube && d.MediaSelector == format.Selector && d.MediaMode == format.Mode)))) continue;
            downloads.Insert(0, new NativeDownload { Id = Guid.NewGuid().ToString("N"), Url = result.Url, Filename = filename, Host = result.IsYoutube ? "YouTube" : result.Host, Folder = "", FilePath = "", Size = result.Size, Bytes = 0, AcceptsRanges = result.Ranges, Status = "queued", Error = "", AddedAt = DateTime.UtcNow.ToString("o"), CompletedRanges = new List<long[]>(), IsYoutube = result.IsYoutube, MediaMode = format == null ? "" : format.Mode, MediaSelector = format == null ? "" : format.Selector, MediaExtension = format == null ? "" : format.Extension, MediaAudioQuality = format == null ? "" : format.AudioQuality });
            added++;
        }
        if (added > 0)
        {
            queueArmed = true;
            WriteLog("INFO", "Added " + added + " selected link(s) and started the download queue.");
        }
        SaveState();
        if (queueArmed) PumpQueue();
        RenderDownloads(); tabs.SelectedIndex = 0;
    }

    private void RunContextAction(string action)
    {
        NativeDownload item = downloads.FirstOrDefault(d => d.Id == contextDownloadId);
        if (item == null) return;
        if (action == "download-now") DownloadNow(item);
        else if (action == "pause") Pause(item);
        else if (action == "resume") Resume(item);
        else if (action == "remove") RemoveItems(new[] { item }, false);
        else if (action == "delete") RemoveItems(new[] { item }, true);
    }

    private void Pause(NativeDownload item)
    {
        CancellationTokenSource token;
        if (transfers.TryGetValue(item.Id, out token)) { queueArmed = true; item.Status = "paused"; token.Cancel(); }
        else if (item.Status == "queued" || item.Status == "retrying") { item.Status = "paused"; item.RetryAt = ""; item.Error = ""; }
        SaveState(); RenderDownloads();
        if (queueArmed) PumpQueue();
    }

    private void Resume(NativeDownload item)
    {
        if (item.Status != "paused" && item.Status != "error") return;
        item.Status = "queued"; item.Error = ""; item.RetryAt = ""; item.RetryCount = 0; queueArmed = true; SaveState();
        PumpQueue();
        RenderDownloads();
    }

    private void DownloadNow(NativeDownload item)
    {
        if (item.Status != "queued" && item.Status != "paused" && item.Status != "error") return;
        item.Status = "queued"; item.Error = ""; item.RetryAt = ""; item.RetryCount = 0;
        queueArmed = true;
        if (StartOne(item, true))
        {
            SaveState();
            RenderDownloads();
        }
    }

    private bool StartOne(NativeDownload item) { return StartOne(item, false); }

    private bool StartOne(NativeDownload item, bool downloadNow)
    {
        lock (schedulerLock)
        {
            if (closing || running.Contains(item.Id)) return false;
            if (!downloadNow && (running.Count >= settings.Concurrency || (runningHosts.ContainsKey(item.Host) && runningHosts[item.Host] >= settings.PerHost))) return false;
            item.Status = "downloading"; running.Add(item.Id); AddRunningHost(item.Host, 1);
            transfers[item.Id] = new CancellationTokenSource();
        }
        CancellationToken token = transfers[item.Id].Token;
        Task.Run(() => Transfer(item, token)).ContinueWith(task => { if (!IsDisposed) BeginInvoke((Action)(() => CompleteTransfer(item, task))); });
        return true;
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
                TryDelete(final); TryDelete(final + ".fetchflow-part"); TryDelete(final + ".part"); TryDelete(final + ".ytdl");
            }
            downloads.Remove(item); selectedIds.Remove(item.Id);
        }
        SaveState(); RenderDownloads();
        PumpQueue();
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
        // Do not call Path.GetFileName on untrusted titles: .NET Framework can
        // throw on characters such as ':' before we have a chance to sanitize them.
        name = (name ?? "download").Replace('/', '_').Replace('\\', '_');
        // Apply Windows rules explicitly too, so names are safe even if this code
        // is built or exercised on a platform with a different invalid-char set.
        const string windowsInvalid = "<>:\"|?*";
        char[] invalidChars = Path.GetInvalidFileNameChars().Concat(windowsInvalid.ToCharArray()).ToArray();
        name = new string(name.Select(ch => Char.IsControl(ch) || invalidChars.Contains(ch) ? '_' : ch).ToArray());
        name = name.Trim().TrimEnd('.', ' ');

        string extension = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ');
        if (String.IsNullOrWhiteSpace(stem)) stem = "download";
        // Keep the full destination comfortably below Windows path component limits.
        int maxStemLength = Math.Max(1, 120 - extension.Length);
        if (stem.Length > maxStemLength) stem = stem.Substring(0, maxStemLength).TrimEnd('.', ' ');
        if (String.IsNullOrWhiteSpace(stem)) stem = "download";

        string deviceName = stem.TrimEnd('.').ToUpperInvariant();
        if (Regex.IsMatch(deviceName, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$")) stem = "_" + stem;
        return stem + extension;
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
        return item.Status == "downloading" ? "Downloading" : item.Status == "retrying" ? "Retrying · " + item.Error : item.Status == "finished" ? "Completed" : item.Status == "paused" ? "Paused" : item.Status == "error" ? "Failed" : "Queued";
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

