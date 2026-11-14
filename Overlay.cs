using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace NightRun;

sealed class OverlayForm : Form
{
    internal readonly int Sw, Sh;
    readonly GameReader reader;
    readonly AimBot aim;
    internal bool MenuOpen;
    internal Painter Painter;
    internal MenuUI Menu;
    IntPtr surfaceDc, surfaceBitmap, oldBitmap;
    Graphics surfaceGraphics;
    readonly Thread renderThread;
    readonly ManualResetEventSlim stopped = new ManualResetEventSlim(false);
    volatile bool stop;
    int tickQueued;
    double framePeriod = 1000.0 / 120;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly double[] paintTimes = new double[512];
    readonly double[] frameTimes = new double[512];
    int paintCount, sampleIndex;
    double lastPaint, lastReport, lastIdle;
    bool prevShift, pending, closing;
    Rectangle previousRegion;
    bool previousMenuOpen;
    double paintFps;
    internal double Fps => paintFps;
    internal double lastPaintMs;

    public OverlayForm(GameReader reader, AimBot aim)
    {
        this.reader = reader; this.aim = aim;
        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "render", Priority = ThreadPriority.Normal };
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen.Bounds;
        Sw = Bounds.Width; Sh = Bounds.Height;
        TopMost = true; ShowInTaskbar = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Black; TransparencyKey = Color.Black;
        Load += OnLoad;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80;
            return cp;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name, uint flags, uint access);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr arg, bool resume);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int X, Y; public uint Used, Important;
    }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);

    void OnLoad(object sender, EventArgs e)
    {
        Painter = new Painter(this, reader, aim);
        Menu = new MenuUI(this, reader, aim);
        surfaceDc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BitmapInfo { Size = 40, Width = Sw, Height = -Sh, Planes = 1, Bits = 32 };
        surfaceBitmap = CreateDIBSection(surfaceDc, ref info, 0, out _, IntPtr.Zero, 0);
        if (surfaceDc == IntPtr.Zero || surfaceBitmap == IntPtr.Zero) throw new InvalidOperationException("Cannot create overlay surface");
        oldBitmap = SelectObject(surfaceDc, surfaceBitmap);
        surfaceGraphics = Graphics.FromHdc(surfaceDc);
        surfaceGraphics.Clear(Color.Black);
        surfaceGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        surfaceGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        for (int i = 1; i <= 10; i++) Rw.RegisterHk(Handle, i, (uint)(0x70 + i - 1));
        using (var graphics = CreateGraphics())
        {
            IntPtr dc = graphics.GetHdc();
            try { framePeriod = 1000.0 / Math.Clamp(GetDeviceCaps(dc, 116), 60, 240); }
            finally { graphics.ReleaseHdc(dc); }
        }
        renderThread.Start();
    }

    void RenderLoop()
    {
        IntPtr wait = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003);
        try
        {
            while (!stop)
            {
                long start = Stopwatch.GetTimestamp();
                if (Interlocked.CompareExchange(ref tickQueued, 1, 0) == 0)
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { Tick(this, EventArgs.Empty); }
                            catch (Exception ex) { GameReader.Log(ex); }
                            finally { Volatile.Write(ref tickQueued, 0); }
                        }));
                    }
                    catch (InvalidOperationException) { Volatile.Write(ref tickQueued, 0); }
                double budget = (Rw.GameFocused(reader.GamePid) && (Cfg.EspEnabled || Cfg.S.Loot || Cfg.S.Aim)) || Program.MenuOpenFlag ? framePeriod : 100;
                double remaining = Math.Max(1, budget - Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                long due = -(long)(remaining * 10000);
                if (wait != IntPtr.Zero && SetWaitableTimer(wait, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitForSingleObject(wait, 200);
                else stopped.Wait((int)Math.Ceiling(remaining));
            }
        }
        finally { if (wait != IntPtr.Zero) CloseHandle(wait); }
    }

    internal void SetMenuOpen(bool open)
    {
        MenuOpen = open; Program.MenuOpenFlag = open;
        int ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, open ? ex & ~0x20 : ex | 0x20);
    }

    void Tick(object sender, EventArgs e)
    {
        if (closing) return;
        bool shift = Rw.Key(0xA1);
        if (shift && !prevShift) SetMenuOpen(!MenuOpen);
        prevShift = shift;
        bool focused = Rw.GameFocused(reader.GamePid);
        bool visible = focused || MenuOpen || Environment.GetEnvironmentVariable("NR_SELFTEST") == "1";
        if (Visible != visible)
        {
            Visible = visible;
            if (visible)
            {
                previousRegion = ClientRectangle;
                Rw.Topmost(Handle);
            }
        }
        if (visible && !pending)
        {
            bool active = Cfg.EspEnabled || Cfg.S.Loot || (Cfg.S.Aim && aim.LockedId >= 0) || MenuOpen;
            if (active || clock.Elapsed.TotalMilliseconds - lastIdle >= 500)
            {
                Snap snap = reader.Snap;
                Rectangle next = Painter.Prepare(snap, MenuOpen);
                if (MenuOpen) next = Union(next, Menu.Bounds);
                // Menu dragging changes its position during Draw, after Prepare calculated
                // the dirty rectangle. Clear/present the entire surface while it is open
                // and for the first frame after closing, so no old pixels survive.
                Rectangle dirty = MenuOpen || previousMenuOpen ? ClientRectangle : Union(previousRegion, next);
                dirty.Intersect(ClientRectangle);
                if (!dirty.IsEmpty)
                {
                    long start = Stopwatch.GetTimestamp();
                    surfaceGraphics.SetClip(dirty);
                    surfaceGraphics.FillRectangle(Brushes.Black, dirty);
                    Painter.Draw(surfaceGraphics, MenuOpen);
                    Menu.Draw(surfaceGraphics);
                    surfaceGraphics.ResetClip();
                    lastPaintMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (MenuOpen) next = Union(next, Menu.Bounds);
                    pending = true;
                    Invalidate(dirty, false);
                    Update();
                }
                previousRegion = next;
                previousMenuOpen = MenuOpen;
                lastIdle = clock.Elapsed.TotalMilliseconds;
            }
        }
        if (clock.Elapsed.TotalSeconds - lastReport >= 5) Report();
    }

    internal static Rectangle Union(Rectangle old, Rectangle next) => old.IsEmpty ? next : next.IsEmpty ? old : Rectangle.Union(old, next);

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (surfaceDc == IntPtr.Zero || closing) return;
        double now = clock.Elapsed.TotalMilliseconds;
        long start = Stopwatch.GetTimestamp();
        try
        {
            Rectangle clip = e.ClipRectangle;
            surfaceGraphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            IntPtr dest = e.Graphics.GetHdc();
            try { BitBlt(dest, clip.X, clip.Y, clip.Width, clip.Height, surfaceDc, clip.X, clip.Y, 0x00CC0020); }
            finally { e.Graphics.ReleaseHdc(dest); }
        }
        catch (Exception ex) { GameReader.Log(ex); }
        finally
        {
            int index = sampleIndex++ % paintTimes.Length;
            paintTimes[index] = lastPaintMs + Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            frameTimes[index] = lastPaint == 0 ? 0 : now - lastPaint;
            lastPaint = now; paintCount++; pending = false;
        }
    }

    void Report()
    {
        double now = clock.Elapsed.TotalSeconds, seconds = now - lastReport;
        paintFps = paintCount / Math.Max(0.001, seconds);
        paintCount = 0; lastReport = now;
        int count = Math.Min(sampleIndex, paintTimes.Length);
        sampleIndex = 0;
        double[] paints = paintTimes.Take(count).OrderBy(x => x).ToArray();
        double[] intervals = frameTimes.Take(count).OrderBy(x => x).ToArray();
        double p95 = count == 0 ? 0 : paints[(int)((count - 1) * 0.95)];
        double frame95 = count == 0 ? 0 : intervals[(int)((count - 1) * 0.95)];
        long reads = Interlocked.Exchange(ref Rw.ReadCount, 0);
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nightrun_diag.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
            var snap = reader.Snap;
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} fps={Fps:0} refreshHz={1000 / framePeriod:0} paintMs={lastPaintMs:0.0} paintP95={p95:0.0} frameP95={frame95:0.0} readMs={reader.ReadMs:0.0} camAgeMs={snap.AgeMs:0.0} ents={snap.Ents.Length} aim={aim.AimStatus} lootEnabled={Cfg.S.Loot} loot=[{snap.LootStatus}] lootVisible={Painter.LootVisible} lootFar={Painter.LootFar} lootBehind={Painter.LootBehind} lootOffscreen={Painter.LootOffscreen} cameraValid={snap.Cam.Valid} ioctl/s={reads / Math.Max(0.001, seconds):0}\n");
        }
        catch (Exception ex) { GameReader.Log(ex); }
        Cfg.SaveIfDirty();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) { HandleHotkey(m.WParam.ToInt32()); return; }
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
        if (m.Msg == 0x84)
        {
            m.Result = (IntPtr)(MenuOpen && Menu != null && Menu.Contains(Cursor.Position) ? 1 : -1);
            return;
        }
        base.WndProc(ref m);
    }

    void HandleHotkey(int id)
    {
        var c = Cfg.S;
        switch (id)
        {
            case 1: c.BoxMode = c.BoxMode == 0 ? 1 : 0; break;
            case 2: c.Aim = !c.Aim; break;
            case 3: c.Skeleton = !c.Skeleton; break;
            case 4: c.ShowDead = !c.ShowDead; break;
            case 5: c.Hitboxes = !c.Hitboxes; break;
            case 6: c.MaxDist = c.MaxDist >= 1000 ? 100 : c.MaxDist == 100 ? 200 : c.MaxDist == 200 ? 400 : c.MaxDist == 400 ? 600 : 1000; break;
            case 7: c.ShowSelf = !c.ShowSelf; break;
            case 8: c.Loot = !c.Loot; break;
            case 9: Cfg.Panic(); SetMenuOpen(false); break;
            case 10: Close(); return;
        }
        Cfg.Touch();
        Tick(this, EventArgs.Empty);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true; stop = true; stopped.Set(); Program.MenuOpenFlag = false;
        if (renderThread.IsAlive) renderThread.Join();
        for (int i = 1; i <= 10; i++) Rw.UnregisterHk(Handle, i);
        reader.Stop(); aim.Stop();
        Painter?.Dispose(); Menu?.Dispose(); surfaceGraphics?.Dispose();
        if (surfaceDc != IntPtr.Zero)
        {
            SelectObject(surfaceDc, oldBitmap); DeleteObject(surfaceBitmap); DeleteDC(surfaceDc);
        }
        Cfg.SaveIfDirty();
        base.OnFormClosed(e);
    }
}
