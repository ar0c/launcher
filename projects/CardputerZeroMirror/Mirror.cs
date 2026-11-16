using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Cardputer Zero 投屏")]
[assembly: AssemblyProduct("Cardputer Zero 投屏")]
[assembly: AssemblyDescription("通过 SSH 查看和操作 Cardputer Zero 的实体屏幕")]
[assembly: AssemblyVersion("1.0.7.0")]
[assembly: AssemblyFileVersion("1.0.7.0")]

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MirrorWindow());
    }
}

sealed class MirrorWindow : Form
{
    readonly TextBox address = new TextBox { Width = 200, AccessibleName = "Zero 的 IP 地址" };
    readonly Button connect = new Button { Text = "连接", AutoSize = true };
    readonly ComboBox scaling = new ComboBox {
        Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "画面模式"
    };
    readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ScreenView screen = new ScreenView {
        Dock = DockStyle.Fill, TabStop = true, Mode = ScreenScaleMode.TextEnhanced
    };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
    readonly HashSet<int> held = new HashSet<int>();
    readonly Stopwatch keepalive = Stopwatch.StartNew();
    readonly Stopwatch connecting = new Stopwatch();
    readonly byte[] pixels = new byte[ScreenView.FrameBytes];
    MirrorConnection session;
    long shownFrame;

    public MirrorWindow()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cardputer Zero 投屏";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        address.Text = DeviceAddress.Load(DeviceAddress.SettingsFile);
        ClientSize = new Size(960, 586);
        MinimumSize = new Size(535, 322);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), WrapContents = false };
        toolbar.Controls.Add(new Label { Text = "设备地址", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        toolbar.Controls.Add(address);
        toolbar.Controls.Add(connect);
        scaling.Items.AddRange(new object[] { "整数倍", "适应窗口", "文字增强" });
        scaling.SelectedIndex = (int)screen.Mode;
        toolbar.Controls.Add(scaling);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        status.Padding = new Padding(10, 0, 10, 0);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(screen, 0, 1);
        layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);

        connect.Click += delegate { if (session == null) StartConnection(); else Disconnect(); };
        scaling.SelectedIndexChanged += delegate {
            screen.Mode = (ScreenScaleMode)scaling.SelectedIndex;
            UpdateConnectedStatus();
        };
        address.KeyDown += delegate(object sender, KeyEventArgs args) {
            if (args.KeyCode == Keys.Enter && session == null) {
                args.SuppressKeyPress = true;
                StartConnection();
            }
        };
        screen.KeyDown += delegate(object sender, KeyEventArgs args) { Forward(args, true); };
        screen.KeyUp += delegate(object sender, KeyEventArgs args) { Forward(args, false); };
        screen.GotFocus += delegate { UpdateConnectedStatus(); };
        screen.LostFocus += delegate { ReleaseKeys(); UpdateConnectedStatus(); };
        screen.SizeChanged += delegate { UpdateConnectedStatus(); };
        Deactivate += delegate { ReleaseKeys(); };
        FormClosing += delegate {
            timer.Dispose();
            if (session != null) session.Close(true);
        };
        timer.Tick += delegate { RefreshConnection(); };
        Shown += delegate {
            timer.Start();
            if (String.IsNullOrEmpty(address.Text)) {
                status.Text = "输入 Zero 的 IP 地址后连接。";
            } else {
                StartConnection();
            }
        };
        ResumeLayout();
    }

    void StartConnection()
    {
        string host = address.Text.Trim();
        if (!DeviceAddress.IsValid(host)) {
            status.Text = "请输入有效的 IPv4 地址或主机名。";
            return;
        }
        try {
            session = new MirrorConnection(host);
            shownFrame = 0;
            screen.ClearFrame();
            connect.Text = "断开";
            address.Enabled = false;
            status.Text = "正在连接 Zero…";
            connecting.Restart();
            screen.Focus();
        } catch (Exception error) {
            status.Text = error.Message;
        }
    }

    void Disconnect()
    {
        ReleaseKeys();
        session.Close(false);
        ResetConnection("已断开。");
    }

    void ResetConnection(string message)
    {
        held.Clear();
        session = null;
        screen.ClearFrame();
        address.Enabled = true;
        connect.Text = "连接";
        status.Text = message;
    }

    void RefreshConnection()
    {
        if (session == null) return;
        if (session.Finished) {
            ResetConnection(session.Message);
            return;
        }
        if (!session.Ready) {
            if (connecting.ElapsedMilliseconds >= 12000) {
                session.Close(false);
                ResetConnection("连接超时。请确认 Zero 已开机，并检查设备地址。");
            }
            return;
        }
        bool firstFrame = shownFrame == 0;
        if (session.CopyFrame(pixels, ref shownFrame)) {
            screen.ShowFrame(pixels);
            if (firstFrame) {
                DeviceAddress.Remember(DeviceAddress.SettingsFile, address.Text.Trim());
                UpdateConnectedStatus();
            }
        }
        if (keepalive.ElapsedMilliseconds >= 1000) {
            session.Send(0, 1);
            keepalive.Restart();
        }
    }

    void UpdateConnectedStatus()
    {
        if (session == null || !session.Ready) return;
        string inputStatus = screen.Focused ? "键盘已连接" : "点击画面启用按键转发";
        status.Text = screen.ResolutionText + "   ·   " + inputStatus;
    }

    void Forward(KeyEventArgs args, bool down)
    {
        if (session == null || !session.Ready || (args.Alt && args.KeyCode == Keys.F4)) return;
        int code = EvdevKeys.Code(args.KeyCode);
        if (code == 0) return;
        bool changed = down ? held.Add(code) : held.Remove(code);
        if (changed) session.Send(code, down ? 1 : 0);
        args.Handled = true;
    }

    void ReleaseKeys()
    {
        if (held.Count == 0) return;
        held.Clear();
        if (session != null && session.Ready) session.Send(0, 0);
    }
}

static class DeviceAddress
{
    static readonly string[] bundledConnection = ReadBundledConnection();

    public static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CardputerZeroMirror", "device-address.txt");

    public static readonly string HostKeyAlias = BundledValue(1);

    static string[] ReadBundledConnection()
    {
        var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("CardputerZero.connection");
        if (input == null) return new string[0];
        using (var reader = new StreamReader(input)) {
            return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    static string BundledValue(int index)
    {
        if (index >= bundledConnection.Length) return "";
        string value = bundledConnection[index].Trim();
        return IsValid(value) ? value : "";
    }

    public static bool IsValid(string address)
    {
        UriHostNameType kind = Uri.CheckHostName(address);
        return kind == UriHostNameType.IPv4 || kind == UriHostNameType.Dns;
    }

    public static string Load(string path)
    {
        try {
            string address = File.ReadAllText(path).Trim();
            if (IsValid(address)) return address;
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
        return BundledValue(0);
    }

    public static void Remember(string path, string address)
    {
        if (!IsValid(address)) return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, address + Environment.NewLine);
        } catch (IOException) {
            // A settings write failure does not affect the active connection.
        } catch (UnauthorizedAccessException) {
        }
    }
}

enum ScreenScaleMode
{
    Integer,
    FitWindow,
    TextEnhanced
}

sealed class ScreenView : Control
{
    public const int FrameWidth = 320;
    public const int FrameHeight = 170;
    public const int FrameBytes = FrameWidth * FrameHeight * 2;
    const float TextGamma = 0.68f;
    readonly Bitmap image = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppRgb);
    readonly int[] rgb = new int[FrameWidth * FrameHeight];
    readonly ImageAttributes textAttributes = new ImageAttributes();
    readonly SharpFrameScaler textScaler = new SharpFrameScaler(FrameWidth, FrameHeight, TextGamma);
    bool hasFrame;
    ScreenScaleMode mode;

    public ScreenScaleMode Mode
    {
        get { return mode; }
        set {
            if (mode == value) return;
            mode = value;
            Invalidate();
        }
    }

    public string ResolutionText
    {
        get {
            var bounds = ImageBounds(ClientSize, Mode == ScreenScaleMode.FitWindow);
            if (bounds.IsEmpty) return String.Format("{0}×{1}", FrameWidth, FrameHeight);
            double zoom = Math.Max((double)bounds.Width / FrameWidth, (double)bounds.Height / FrameHeight);
            return String.Format("{0}×{1} → {2}×{3}   ·   {4:0.##}×",
                FrameWidth, FrameHeight, bounds.Width, bounds.Height, zoom);
        }
    }

    internal static Rectangle ImageBounds(Size viewport, bool fitViewport = false)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) return Rectangle.Empty;

        int zoom = Math.Min(viewport.Width / FrameWidth, viewport.Height / FrameHeight);
        int width, height;
        if (!fitViewport && zoom > 0) {
            width = FrameWidth * zoom;
            height = FrameHeight * zoom;
        } else if ((long)viewport.Width * FrameHeight <= (long)viewport.Height * FrameWidth) {
            width = viewport.Width;
            height = Math.Max(1, (int)((long)width * FrameHeight / FrameWidth));
        } else {
            height = viewport.Height;
            width = Math.Max(1, (int)((long)height * FrameWidth / FrameHeight));
        }
        return new Rectangle((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    public ScreenView()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(15, 19, 23);
        ForeColor = Color.FromArgb(211, 220, 228);
        AccessibleName = "Zero 屏幕及远程键盘";
        textAttributes.SetWrapMode(WrapMode.TileFlipXY);
        textAttributes.SetGamma(TextGamma, ColorAdjustType.Bitmap);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return EvdevKeys.Code(keyData & Keys.KeyCode) != 0 || base.IsInputKey(keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        e.Handled = true;
        base.OnKeyPress(e);
    }

    public void ClearFrame()
    {
        hasFrame = false;
        Invalidate();
    }

    public void ShowFrame(byte[] pixels)
    {
        bool changed = !hasFrame;
        for (int i = 0; i < rgb.Length; i++) {
            int value = pixels[i * 2] | (pixels[i * 2 + 1] << 8);
            int red = value >> 11;
            int green = (value >> 5) & 63;
            int blue = value & 31;
            int color = ((red << 3 | red >> 2) << 16) | ((green << 2 | green >> 4) << 8) | (blue << 3 | blue >> 2);
            changed |= rgb[i] != color;
            rgb[i] = color;
        }
        if (!changed) return;
        var data = image.LockBits(new Rectangle(0, 0, FrameWidth, FrameHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try {
            Marshal.Copy(rgb, 0, data.Scan0, rgb.Length);
        } finally {
            image.UnlockBits(data);
        }
        hasFrame = true;
        textScaler.Invalidate();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!hasFrame) {
            TextRenderer.DrawText(e.Graphics, "等待 Zero 画面", Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        Rectangle bounds = ImageBounds(ClientSize, Mode == ScreenScaleMode.FitWindow);
        if (bounds.IsEmpty) return;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        if (Mode == ScreenScaleMode.TextEnhanced && bounds.Width > FrameWidth) {
            Bitmap sharpFrame = textScaler.Render(rgb, bounds.Size);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(sharpFrame, bounds, 0, 0, sharpFrame.Width, sharpFrame.Height, GraphicsUnit.Pixel);
        } else if (Mode == ScreenScaleMode.TextEnhanced) {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(image, bounds, 0, 0, FrameWidth, FrameHeight, GraphicsUnit.Pixel, textAttributes);
        } else {
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(image, bounds, 0, 0, FrameWidth, FrameHeight, GraphicsUnit.Pixel);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            textScaler.Dispose();
            textAttributes.Dispose();
            image.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class SharpFrameScaler : IDisposable
{
    struct Sample
    {
        public int Left;
        public int Right;
        public uint Weight;
    }

    readonly int sourceWidth;
    readonly int sourceHeight;
    readonly int[] gamma = new int[256];
    readonly int[] sourceRow;
    Sample[] columns;
    Sample[] rows;
    int[] horizontal;
    int[] outputRow;
    Bitmap output;
    bool valid;

    public SharpFrameScaler(int width, int height, double exponent)
    {
        sourceWidth = width;
        sourceHeight = height;
        sourceRow = new int[width];
        for (int i = 0; i < gamma.Length; i++)
            gamma[i] = (int)Math.Round(255 * Math.Pow(i / 255.0, exponent));
    }

    public void Invalidate()
    {
        valid = false;
    }

    static Sample[] BuildAxis(int input, int output)
    {
        var samples = new Sample[output];
        double step = (double)input / output;
        for (int i = 0; i < output; i++) {
            double center = Math.Max(0, Math.Min(input - 1, (i + 0.5) * step - 0.5));
            int left = (int)center;
            double fraction = center - left;
            double smooth = fraction * fraction * (3 - 2 * fraction);
            // Keep a steeper center while blending more of the diagonal pixel edges.
            double weight = 0.25 * fraction + 0.75 * smooth;
            samples[i] = new Sample {
                Left = left,
                Right = Math.Min(left + 1, input - 1),
                Weight = (uint)Math.Round(256 * weight)
            };
        }
        return samples;
    }

    static int Blend(int left, int right, uint weight)
    {
        const uint redBlueMask = 0x00ff00ff;
        const uint greenMask = 0x0000ff00;
        unchecked {
            uint a = (uint)left;
            uint b = (uint)right;
            uint inverse = 256 - weight;
            uint redBlue = ((a & redBlueMask) * inverse + (b & redBlueMask) * weight + 0x00800080) >> 8;
            uint green = ((a & greenMask) * inverse + (b & greenMask) * weight + 0x00008000) >> 8;
            return (int)((redBlue & redBlueMask) | (green & greenMask));
        }
    }

    public Bitmap Render(int[] rgb, Size size)
    {
        if (output == null || output.Size != size) {
            var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            if (output != null) output.Dispose();
            output = resized;
            columns = BuildAxis(sourceWidth, size.Width);
            rows = BuildAxis(sourceHeight, size.Height);
            horizontal = new int[size.Width * sourceHeight];
            outputRow = new int[size.Width];
            valid = false;
        }
        if (valid) return output;

        int width = size.Width;
        for (int y = 0; y < sourceHeight; y++) {
            int sourceOffset = y * sourceWidth;
            for (int x = 0; x < sourceWidth; x++) {
                int color = rgb[sourceOffset + x];
                sourceRow[x] = gamma[color >> 16] << 16 | gamma[(color >> 8) & 255] << 8 | gamma[color & 255];
            }
            int destOffset = y * width;
            for (int x = 0; x < width; x++) {
                Sample sample = columns[x];
                horizontal[destOffset + x] = Blend(sourceRow[sample.Left], sourceRow[sample.Right], sample.Weight);
            }
        }

        var data = output.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try {
            for (int y = 0; y < size.Height; y++) {
                Sample sample = rows[y];
                int top = sample.Left * width;
                int bottom = sample.Right * width;
                IntPtr target = IntPtr.Add(data.Scan0, y * data.Stride);
                if (sample.Weight == 0 || sample.Weight == 256) {
                    Marshal.Copy(horizontal, sample.Weight == 0 ? top : bottom, target, width);
                } else {
                    for (int x = 0; x < width; x++)
                        outputRow[x] = Blend(horizontal[top + x], horizontal[bottom + x], sample.Weight);
                    Marshal.Copy(outputRow, 0, target, width);
                }
            }
        } finally {
            output.UnlockBits(data);
        }
        valid = true;
        return output;
    }

    public void Dispose()
    {
        if (output != null) output.Dispose();
    }
}

static class EvdevKeys
{
    static readonly int[] letters = {
        30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50,
        49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44
    };

    public static int Code(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return letters[key - Keys.A];
        if (key >= Keys.D1 && key <= Keys.D9) return key - Keys.D1 + 2;
        if (key >= Keys.NumPad1 && key <= Keys.NumPad9) return key - Keys.NumPad1 + 2;
        switch (key) {
            case Keys.D0: case Keys.NumPad0: return 11;
            case Keys.Escape: return 1;
            case Keys.Back: return 14;
            case Keys.Tab: return 15;
            case Keys.Enter: return 28;
            case Keys.Space: return 57;
            case Keys.Up: return 103;
            case Keys.Left: return 105;
            case Keys.Right: return 106;
            case Keys.Down: return 108;
            case Keys.Home: return 102;
            case Keys.End: return 107;
            case Keys.PageUp: return 104;
            case Keys.PageDown: return 109;
            case Keys.Delete: return 111;
            case Keys.OemPeriod: case Keys.Decimal: return 52;
            case Keys.Oemcomma: return 51;
            case Keys.OemMinus: return 12;
            case Keys.Oemplus: return 13;
            case Keys.OemOpenBrackets: return 26;
            case Keys.OemCloseBrackets: return 27;
            case Keys.OemSemicolon: return 39;
            case Keys.OemQuotes: return 40;
            case Keys.Oemtilde: return 41;
            case Keys.OemPipe: return 43;
            case Keys.OemQuestion: return 53;
            case Keys.ShiftKey: case Keys.LShiftKey: return 42;
            case Keys.RShiftKey: return 54;
            case Keys.ControlKey: case Keys.LControlKey: return 29;
            case Keys.RControlKey: return 97;
            default: return 0;
        }
    }
}

sealed class MirrorConnection
{
    readonly Process process;
    readonly BlockingCollection<int> keys = new BlockingCollection<int>(64);
    readonly object frameLock = new object();
    byte[] latest = new byte[ScreenView.FrameBytes];
    long frameNumber;
    int closing;
    string sshError;
    public volatile bool Ready;
    public volatile bool Finished;
    public string Message;

    public MirrorConnection(string host) : this(new ProcessStartInfo {
        FileName = "ssh.exe",
        Arguments = SshArguments(host),
    }) {}

    // Redirect both SSH streams separately so a banner or error cannot corrupt the image.
    internal MirrorConnection(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        process = Process.Start(start);
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
            if (!String.IsNullOrWhiteSpace(e.Data)) Volatile.Write(ref sshError, e.Data.Trim());
        };
        process.BeginErrorReadLine();
        StartWorker(ReadFrames);
        StartWorker(WriteKeys);
    }

    internal static string SshArguments(string host)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("CardputerZero.device.py"))
        using (var buffer = new MemoryStream()) {
            input.CopyTo(buffer);
            string script = Convert.ToBase64String(buffer.ToArray());
            string keyAlias = DeviceAddress.HostKeyAlias;
            string aliasOption = String.IsNullOrEmpty(keyAlias) ? "" : "-o HostKeyAlias=" + keyAlias + " ";
            return "-T -o BatchMode=yes -o ConnectTimeout=5 -o ServerAliveInterval=3 -o ServerAliveCountMax=2 " +
                   aliasOption + "-o StrictHostKeyChecking=yes -o UpdateHostKeys=no pi@" + host +
                   " \"python3 -u -c 'import base64,sys;exec(base64.b64decode(sys.argv[1]))' " + script + "\"";
        }
    }

    static void StartWorker(ThreadStart action)
    {
        new Thread(action) { IsBackground = true }.Start();
    }

    static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length) {
            int count = stream.Read(buffer, offset, buffer.Length - offset);
            if (count == 0) {
                if (offset != 0) throw new EndOfStreamException("Zero 的画面流中途断开。");
                return false;
            }
            offset += count;
        }
        return true;
    }

    void ReadFrames()
    {
        try {
            var stream = process.StandardOutput.BaseStream;
            var header = new byte[4];
            if (!ReadExactly(stream, header)) throw new EndOfStreamException("未收到 Zero 的画面。");
            if (Encoding.ASCII.GetString(header) != "CZM1") throw new IOException("Zero 返回的画面协议不匹配。");
            var frame = new byte[ScreenView.FrameBytes];
            while (ReadExactly(stream, frame)) {
                lock (frameLock) {
                    byte[] spare = latest;
                    latest = frame;
                    frame = spare;
                    frameNumber++;
                }
                Ready = true;
            }
            Message = "Zero 已断开。";
        } catch (IOException error) {
            Message = error.Message;
        } finally {
            bool wasReady = Ready;
            Ready = false;
            Close(true);
            string reason = Volatile.Read(ref sshError);
            if (!String.IsNullOrEmpty(reason) && (reason.StartsWith("ssh:") || reason.StartsWith("Zero mirror:"))) {
                Message = reason;
            } else if (!wasReady && !String.IsNullOrEmpty(reason) && reason.Contains("Permission denied")) {
                Message = reason;
            }
            Finished = true;
        }
    }

    public bool CopyFrame(byte[] target, ref long previousFrame)
    {
        lock (frameLock) {
            if (previousFrame == frameNumber) return false;
            Buffer.BlockCopy(latest, 0, target, 0, latest.Length);
            previousFrame = frameNumber;
            return true;
        }
    }

    public void Send(int code, int value)
    {
        if (closing != 0) return;
        try {
            if (!keys.TryAdd(code | (value << 16))) Close(false);
        } catch (InvalidOperationException) {
            // The SSH input stream has already closed.
        }
    }

    void WriteKeys()
    {
        try {
            using (var output = process.StandardInput.BaseStream) {
                var data = new byte[3];
                foreach (int key in keys.GetConsumingEnumerable()) {
                    data[0] = (byte)key;
                    data[1] = (byte)(key >> 8);
                    data[2] = (byte)(key >> 16);
                    output.Write(data, 0, data.Length);
                    output.Flush();
                }
            }
        } catch (IOException) {
            Close(false);
        }
    }

    public void Close(bool wait)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        keys.TryAdd(0);
        keys.CompleteAdding();
        if (wait) FinishProcess();
        else StartWorker(FinishProcess);
    }

    void FinishProcess()
    {
        try {
            if (!process.WaitForExit(850)) {
                process.Kill();
                process.WaitForExit(850);
            }
            if (process.HasExited) process.WaitForExit();
        } catch (InvalidOperationException) {
            // The owned SSH process exited before the cleanup request.
        }
    }
}
