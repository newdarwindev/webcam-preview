using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

internal sealed class WebcamPreview : Form
{
    private readonly PreviewPanel videoPanel = new PreviewPanel();
    private readonly string preferredCamera;
    private readonly Timer refreshTimer = new Timer { Interval = 30 };
    private readonly Timer verificationTimer = new Timer { Interval = 1000 };
    private readonly ImageAttributes invertedColors = CreateInversion();
    private readonly ToolStripMenuItem inversion = new ToolStripMenuItem("Invert colors") {
        Checked = true, CheckOnClick = true
    };
    private Point dragStart;
    private Rectangle dragBounds;
    private ResizeEdges dragEdges;
    private string activeCamera;
    private string captureMode;
    private FrameBuffer frames;
    private Bitmap bitmap;
    private int renderedFrames;
    private ISampleGrabber grabber;
    private IMediaControl control;
    private object graph;
    private ICaptureGraphBuilder builder;
    private IBaseFilter source;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.Run(new WebcamPreview(args.Length == 0 ? null : args[0]));
    }

    private WebcamPreview(string preferredCamera)
    {
        this.preferredCamera = preferredCamera;
        verificationTimer.Tick += VerifyPreview;
        Text = "Webcam Preview";
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = new Size(160, 120);
        ClientSize = new Size(400, 300);
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        Controls.Add(videoPanel);
        var menu = new ContextMenuStrip();
        menu.Items.Add(inversion);
        menu.Items.Add("Close preview", null, delegate { Close(); });
        videoPanel.ContextMenuStrip = menu;
        inversion.CheckedChanged += delegate { videoPanel.Invalidate(); };
        videoPanel.Paint += DrawPreview;
        videoPanel.MouseDown += PreviewMouseDown;
        videoPanel.MouseMove += PreviewMouseMove;
        videoPanel.MouseUp += delegate(object sender, MouseEventArgs args) {
            if (args.Button == MouseButtons.Left)
                videoPanel.Capture = false;
        };
        refreshTimer.Tick += RefreshPreview;
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        object selected = null;
        string selectedName = null;
        string targetCamera = preferredCamera ?? "USB Video";
        string stage = "selecting device";
        try
        {
            var devices = (ICreateDevEnum)Create("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
            IEnumMoniker enumerator;
            Guid category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            int result = devices.CreateClassEnumerator(ref category, out enumerator, 0);
            Check(result);
            if (enumerator == null)
                throw new InvalidOperationException("Windows reported no video capture devices.");
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                object bag;
                Guid bagId = typeof(IPropertyBag).GUID;
                moniker.BindToStorage(null, null, ref bagId, out bag);
                object name;
                Check(((IPropertyBag)bag).Read("FriendlyName", out name, IntPtr.Zero));
                string friendlyName = Convert.ToString(name);
                if (preferredCamera == null && selected == null)
                {
                    selected = moniker;
                    selectedName = friendlyName;
                }
                if (string.Equals(friendlyName.Trim(), targetCamera, StringComparison.OrdinalIgnoreCase))
                {
                    selected = moniker;
                    selectedName = friendlyName;
                    break;
                }
            }
            if (selected == null)
                throw new InvalidOperationException("Windows did not find " + preferredCamera + ".");

            string devicePath;
            ((IMoniker)selected).GetDisplayName(null, null, out devicePath);
            Guid filterId = new Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770");
            object cameraFilter;
            stage = "opening device";
            ((IMoniker)selected).BindToObject(null, null, ref filterId, out cameraFilter);
            source = (IBaseFilter)cameraFilter;
            graph = Create("E436EBB3-524F-11CE-9F53-0020AF0BA770");
            builder = (ICaptureGraphBuilder)Create("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
            Check(builder.SetFiltergraph((IFilterGraph)graph));
            Check(((IFilterGraph)graph).AddFilter(source, selectedName));

            Guid pin = new Guid("FB6C4282-0353-11D1-905F-0000C0CC16BA");
            Guid media = new Guid("73646976-0000-0010-8000-00AA00389B71");
            if (devicePath.IndexOf("vid_534d&pid_2109", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                stage = "selecting capture format";
                ConfigureUsbStream(media);
            }
            stage = "building preview";
            ConfigurePreview(ref pin, ref media);
            control = (IMediaControl)graph;
            stage = "starting capture " + captureMode;
            Check(control.Run());
            Text = "Webcam Preview | " + selectedName;
            activeCamera = selectedName;
            Log("Waiting for frames from " + activeCamera);
            refreshTimer.Start();
            verificationTimer.Start();
        }
        catch (Exception error)
        {
            Text = "Webcam Preview | Camera unavailable";
            Log(stage + ": " + error);
            videoPanel.Controls.Add(new Label {
                Dock = DockStyle.Fill, ForeColor = Color.White,
                ContextMenuStrip = videoPanel.ContextMenuStrip,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Could not open " + (selectedName ?? "a webcam") + "." + Environment.NewLine + error.Message
            });
        }
    }

    private void ConfigureUsbStream(Guid media)
    {
        Guid capturePin = new Guid("FB6C4281-0353-11D1-905F-0000C0CC16BA");
        Guid configId = typeof(IAMStreamConfig).GUID;
        object value;
        Check(builder.FindInterface(ref capturePin, ref media, source, ref configId, out value));
        var config = (IAMStreamConfig)value;
        int count;
        int size;
        Check(config.GetNumberOfCapabilities(out count, out size));
        IntPtr capabilities = Marshal.AllocCoTaskMem(size);
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr pointer;
                if (config.GetStreamCaps(i, out pointer, capabilities) < 0 || pointer == IntPtr.Zero)
                    continue;

                var format = (AMMediaType)Marshal.PtrToStructure(pointer, typeof(AMMediaType));
                try
                {
                    int offset = format.FormatType == new Guid("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA") ? 76 : 52;
                    if (format.FormatSize < offset + 8 ||
                        format.Subtype != new Guid("47504A4D-0000-0010-8000-00AA00389B71"))
                        continue;
                    int width = Marshal.ReadInt32(format.Format, offset);
                    int height = Math.Abs(Marshal.ReadInt32(format.Format, offset + 4));
                    if (width != 640 || height != 480)
                        continue;
                    Marshal.WriteInt64(format.Format, 40, 666667); // 15 fps
                    Check(config.SetFormat(pointer));
                    captureMode = "640 x 480 MJPEG at 15 fps";
                    return;
                }
                finally
                {
                    if (format.Format != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(format.Format);
                    if (format.Unknown != IntPtr.Zero)
                        Marshal.Release(format.Unknown);
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
            throw new InvalidOperationException("USB Video did not provide a 640 x 480 MJPEG mode.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(capabilities);
        }
    }

    private void ConfigurePreview(ref Guid pin, ref Guid media)
    {
        var filterGraph = (IFilterGraph)graph;
        var sampleFilter = (IBaseFilter)Create("C1F400A0-3F08-11D3-9F0B-006008039E37");
        var sink = (IBaseFilter)Create("C1F400A4-3F08-11D3-9F0B-006008039E37");
        grabber = (ISampleGrabber)sampleFilter;
        var videoType = new AMMediaType {
            MajorType = media,
            Subtype = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770"),
            FormatType = new Guid("05589F80-C356-11CE-BF01-00AA0055595A")
        };
        Check(grabber.SetMediaType(ref videoType));
        Check(filterGraph.AddFilter(sampleFilter, "Preview Frames"));
        Check(filterGraph.AddFilter(sink, "Preview Sink"));
        Check(builder.RenderStream(ref pin, ref media, source, sampleFilter, sink));

        AMMediaType negotiated;
        Check(grabber.GetConnectedMediaType(out negotiated));
        try
        {
            if (negotiated.Format == IntPtr.Zero || negotiated.FormatSize < 88)
                throw new InvalidOperationException("The preview did not negotiate an RGB video format.");
            int width = Marshal.ReadInt32(negotiated.Format, 52);
            int height = Marshal.ReadInt32(negotiated.Format, 56);
            frames = new FrameBuffer(width, height);
            bitmap = new Bitmap(width, Math.Abs(height), PixelFormat.Format24bppRgb);
        }
        finally
        {
            if (negotiated.Format != IntPtr.Zero)
                Marshal.FreeCoTaskMem(negotiated.Format);
            if (negotiated.Unknown != IntPtr.Zero)
                Marshal.Release(negotiated.Unknown);
        }
        Check(grabber.SetCallback(frames, 1));
    }

    private void RefreshPreview(object sender, EventArgs args)
    {
        if (frames.CopyTo(bitmap, ref renderedFrames))
            videoPanel.Invalidate();
    }

    [Flags]
    private enum ResizeEdges
    {
        None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8
    }

    private void PreviewMouseDown(object sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
            return;

        dragStart = MousePosition;
        dragBounds = Bounds;
        dragEdges = HitTestPreview(args.Location);
        videoPanel.Capture = true;
    }

    private void PreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (videoPanel.Capture && (args.Button & MouseButtons.Left) != 0)
        {
            Point cursor = MousePosition;
            var delta = new Point(cursor.X - dragStart.X, cursor.Y - dragStart.Y);
            Bounds = AdjustPreviewBounds(dragBounds, delta, dragEdges, MinimumSize);
            return;
        }

        switch (HitTestPreview(args.Location))
        {
            case ResizeEdges.Left:
            case ResizeEdges.Right:
                videoPanel.Cursor = Cursors.SizeWE;
                break;
            case ResizeEdges.Top:
            case ResizeEdges.Bottom:
                videoPanel.Cursor = Cursors.SizeNS;
                break;
            case ResizeEdges.Top | ResizeEdges.Left:
            case ResizeEdges.Bottom | ResizeEdges.Right:
                videoPanel.Cursor = Cursors.SizeNWSE;
                break;
            case ResizeEdges.Top | ResizeEdges.Right:
            case ResizeEdges.Bottom | ResizeEdges.Left:
                videoPanel.Cursor = Cursors.SizeNESW;
                break;
            default:
                videoPanel.Cursor = Cursors.Default;
                break;
        }
    }

    private ResizeEdges HitTestPreview(Point cursor)
    {
        const int resizeMargin = 7;
        ResizeEdges edges = ResizeEdges.None;
        if (cursor.X < resizeMargin)
            edges |= ResizeEdges.Left;
        else if (cursor.X >= videoPanel.ClientSize.Width - resizeMargin)
            edges |= ResizeEdges.Right;
        if (cursor.Y < resizeMargin)
            edges |= ResizeEdges.Top;
        else if (cursor.Y >= videoPanel.ClientSize.Height - resizeMargin)
            edges |= ResizeEdges.Bottom;
        return edges;
    }

    private static Rectangle AdjustPreviewBounds(Rectangle original, Point delta, ResizeEdges edges, Size minimum)
    {
        if (edges == ResizeEdges.None)
        {
            original.Offset(delta);
            return original;
        }

        int left = original.Left;
        int top = original.Top;
        int right = original.Right;
        int bottom = original.Bottom;
        if ((edges & ResizeEdges.Left) != 0)
            left = Math.Min(left + delta.X, right - minimum.Width);
        if ((edges & ResizeEdges.Right) != 0)
            right = Math.Max(right + delta.X, left + minimum.Width);
        if ((edges & ResizeEdges.Top) != 0)
            top = Math.Min(top + delta.Y, bottom - minimum.Height);
        if ((edges & ResizeEdges.Bottom) != 0)
            bottom = Math.Max(bottom + delta.Y, top + minimum.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
    private void DrawPreview(object sender, PaintEventArgs args)
    {
        if (renderedFrames == 0 || videoPanel.ClientSize.Width == 0 || videoPanel.ClientSize.Height == 0)
            return;

        var bounds = FitVideo(bitmap.Size, videoPanel.ClientSize);
        if (inversion.Checked)
            args.Graphics.DrawImage(bitmap, bounds, 0, 0, bitmap.Width, bitmap.Height,
                GraphicsUnit.Pixel, invertedColors);
        else
            args.Graphics.DrawImage(bitmap, bounds);
    }

    private static ImageAttributes CreateInversion()
    {
        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(new[] {
            new[] { -1f,  0f,  0f, 0f, 0f },
            new[] {  0f, -1f,  0f, 0f, 0f },
            new[] {  0f,  0f, -1f, 0f, 0f },
            new[] {  0f,  0f,  0f, 1f, 0f },
            new[] {  1f,  1f,  1f, 0f, 1f }
        }));
        return attributes;
    }

    private void VerifyPreview(object sender, EventArgs args)
    {
        Log("Device: " + activeCamera + Environment.NewLine
            + "Capture mode: " + captureMode + Environment.NewLine
            + "Frames rendered: " + renderedFrames + Environment.NewLine
            + "Video visible: " + (renderedFrames > 0 && videoPanel.Visible) + Environment.NewLine
            + "Always on top: " + TopMost + Environment.NewLine
            + "Source size: " + bitmap.Width + " x " + bitmap.Height + Environment.NewLine
            + "Preview bounds: " + FitVideo(bitmap.Size, videoPanel.ClientSize) + Environment.NewLine
            + "Window fully on screen: " + Screen.FromRectangle(Bounds).WorkingArea.Contains(Bounds) + Environment.NewLine
            + "Invert colors: " + inversion.Checked + Environment.NewLine
            + "Borderless: " + (FormBorderStyle == FormBorderStyle.None));
    }
    private static Rectangle FitVideo(Size source, Size available)
    {
        double scale = Math.Min((double)available.Width / source.Width, (double)available.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle((available.Width - width) / 2, (available.Height - height) / 2, width, height);
    }

    protected override void OnFormClosed(FormClosedEventArgs args)
    {
        refreshTimer.Dispose();
        verificationTimer.Dispose();
        if (control != null)
            control.Stop();
        if (grabber != null)
            grabber.SetCallback(null, 1);
        if (bitmap != null)
            bitmap.Dispose();
        invertedColors.Dispose();
        base.OnFormClosed(args);
    }

    private static object Create(string id)
    {
        return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(id)));
    }

    private static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static void Log(string message)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "status.txt"), message);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string property,
            [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
    }

    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter { }

    [ComImport, Guid("56A8689F-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilterGraph
    {
        [PreserveSig] int AddFilter(IBaseFilter filter,
            [MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AMMediaType
    {
        public Guid MajorType;
        public Guid Subtype;
        public int FixedSizeSamples;
        public int TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr Unknown;
        public int FormatSize;
        public IntPtr Format;
    }

    [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(IntPtr mediaType);
        [PreserveSig] int GetFormat(out IntPtr mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr capabilities);
    }

    [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureGraphBuilder
    {
        [PreserveSig] int SetFiltergraph(IFilterGraph graph);
        [PreserveSig] int GetFiltergraph(out IFilterGraph graph);
        [PreserveSig] int SetOutputFileName(ref Guid type, IntPtr name, IntPtr filter, IntPtr sink);
        [PreserveSig] int FindInterface(ref Guid category, ref Guid type, IBaseFilter filter, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int RenderStream(ref Guid category, ref Guid type,
            [MarshalAs(UnmanagedType.IUnknown)] object source,
            IBaseFilter intermediate,
            IBaseFilter renderer);
    }

    [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
    }

    [ComImport, Guid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISampleGrabber
    {
        [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int SetMediaType(ref AMMediaType mediaType);
        [PreserveSig] int GetConnectedMediaType(out AMMediaType mediaType);
        [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
        [PreserveSig] int GetCurrentSample(out IntPtr sample);
        [PreserveSig] int SetCallback(ISampleGrabberCallback callback, int method);
    }

    private sealed class PreviewPanel : Panel
    {
        public PreviewPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.Black;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }
}

[ComVisible(true), Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISampleGrabberCallback
{
    [PreserveSig] int SampleCB(double time, IntPtr sample);
    [PreserveSig] int BufferCB(double time, IntPtr buffer, int length);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FrameBuffer : ISampleGrabberCallback
{
    private readonly byte[] pixels;
    private readonly int stride;
    private readonly int height;
    private readonly bool bottomUp;
    private int frameNumber;

    public FrameBuffer(int width, int signedHeight)
    {
        if (width <= 0 || signedHeight == 0)
            throw new ArgumentException("Invalid frame dimensions.");
        stride = checked((width * 3 + 3) & ~3);
        height = Math.Abs(signedHeight);
        bottomUp = signedHeight > 0;
        pixels = new byte[checked(stride * height)];
    }

    public int SampleCB(double time, IntPtr sample)
    {
        return 0;
    }

    public int BufferCB(double time, IntPtr buffer, int length)
    {
        if (length < pixels.Length || buffer == IntPtr.Zero)
            return 0;
        lock (pixels)
        {
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            frameNumber++;
        }
        return 0;
    }

    internal bool CopyTo(Bitmap bitmap, ref int copiedFrame)
    {
        lock (pixels)
        {
            if (frameNumber == copiedFrame)
                return false;

            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (int row = 0; row < height; row++)
                {
                    int sourceRow = bottomUp ? height - row - 1 : row;
                    Marshal.Copy(pixels, sourceRow * stride, IntPtr.Add(data.Scan0, row * data.Stride), stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            copiedFrame = frameNumber;
            return true;
        }
    }
}
