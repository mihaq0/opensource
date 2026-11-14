using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NightRun;

static class Program
{
    public static volatile bool MenuOpenFlag;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibraryW(string path);

    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }

        bool settingsOnly = args.Length > 0 && args[0] == "--settings";
        bool selfTest = args.Contains("--selftest");
        bool noMap = args.Contains("--no-map");
        if (selfTest)
        {
            Environment.SetEnvironmentVariable("NR_SELFTEST", "1");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var testReader = new GameReader(0);
            using var test = new OverlayForm(testReader, new AimBot(testReader, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height));
            using var exit = new System.Windows.Forms.Timer { Interval = 3000 };
            exit.Tick += (_, _) => test.Close();
            exit.Start();
            Application.Run(test);
            return 0;
        }

        Cfg.Load();

        if (settingsOnly)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new SettingsStandalone());
            return 0;
        }

        Process game = null;
        try
        {
            foreach (Process p in Process.GetProcessesByName("ROB RUN"))
            {
                if (p.MainWindowHandle != IntPtr.Zero || p.WorkingSet64 > 0) { game = p; break; }
            }
        }
        catch { }

        if (game == null)
        {
            MessageBox.Show("ROB RUN is not running.", "NightRun", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        uint pid = (uint)game.Id;

        if (!Rw.OpenDevice())
        {
            if (noMap || !TryMapDriver())
            {
                MessageBox.Show("Driver \\\\.\\WhReader4 is not loaded and mapping failed.\nSee nightrun_errors.log.", "NightRun");
                return 1;
            }
            if (!Rw.OpenDevice())
            {
                MessageBox.Show("Driver device open failed: " + Marshal.GetLastWin32Error(), "NightRun");
                return 1;
            }
        }

        if (!Rw.Attach(pid))
        {
            MessageBox.Show("Attach failed: " + Marshal.GetLastWin32Error(), "NightRun");
            Rw.Detach();
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        var reader = new GameReader(pid);
        reader.Start();

        var screen = Screen.PrimaryScreen.Bounds;
        var aim = new AimBot(reader, screen.Width, screen.Height);
        aim.Start();

        using var form = new OverlayForm(reader, aim);
        try { Application.Run(form); }
        finally { reader.Stop(); aim.Stop(); Rw.Detach(); }
        return 0;
    }

    static bool TryMapDriver()
    {
        try
        {
            string sys = Path.Combine(Base, "kernel-mode.sys");
            string mapper = Path.Combine(Base, "zxmapper.exe");
            if (!File.Exists(sys)) sys = Path.Combine(Root, @"kernel_wh\mapper_noci\zxmapper\build\kernel-mode.sys");
            if (!File.Exists(mapper)) mapper = Path.Combine(Root, @"kernel_wh\mapper_noci\zxmapper\build\zxmapper.exe");
            if (!File.Exists(sys) || !File.Exists(mapper))
            {
                File.AppendAllText(Path.Combine(Base, "nightrun_errors.log"),
                    DateTime.Now + " map: loader files missing (" + sys + ", " + mapper + ")\n");
                return false;
            }

            var psi = new ProcessStartInfo(mapper, "\"" + sys + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(mapper),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120000)) throw new TimeoutException("Driver loader did not exit within 120 seconds");
            Task.WaitAll(stdout, stderr);
            File.AppendAllText(Path.Combine(Base, "nightrun_errors.log"),
                DateTime.Now + " map exit=" + p.ExitCode + "\n" + stdout.Result + stderr.Result);

            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (Rw.OpenDevice()) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            GameReader.Log(ex);
            return false;
        }
    }
}
