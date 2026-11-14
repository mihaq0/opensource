using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace NightRun.Loader;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Launcher());
    }
}

sealed class Launcher : Form
{
    readonly Label status = new() { AutoSize = false, ForeColor = Color.FromArgb(146, 184, 198), Location = new Point(32, 294), Size = new Size(490, 40), Text = "Готово. Файлы должны находиться рядом с загрузчиком." };
    readonly string root = AppContext.BaseDirectory;

    public Launcher()
    {
        Text = "NightRun • Launcher";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(550, 350);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(17, 14, 27);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "NIGHT RUN", Font = new Font("Segoe UI", 24, FontStyle.Bold), ForeColor = Color.FromArgb(110, 242, 237), Location = new Point(26, 24), AutoSize = true });
        Controls.Add(new Label { Text = "Панель запуска  /  Windows x64", ForeColor = Color.FromArgb(170, 180, 195), Location = new Point(32, 76), AutoSize = true });
        Controls.Add(Button("1   Загрузить драйвер", 115, () => RunScript("load-driver.ps1")));
        Controls.Add(Button("2   Запустить оверлей", 170, () => RunScript("start-overlay.ps1")));
        Controls.Add(Button("Настройки", 225, OpenSettings));
        Controls.Add(status);
    }

    Button Button(string text, int y, Action action)
    {
        var button = new Button { Text = text, Location = new Point(32, y), Size = new Size(486, 42), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(31, 38, 53), ForeColor = Color.White, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Color.FromArgb(53, 126, 133);
        button.Click += (_, _) => action();
        return button;
    }

    void RunScript(string name)
    {
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) { status.Text = $"Не найден {name} рядом с загрузчиком."; return; }
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = root, ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path } });
            status.Text = "Открыто отдельное окно. Следуйте инструкциям в нём.";
        }
        catch (Exception ex) { status.Text = "Ошибка запуска: " + ex.Message; }
    }

    void OpenSettings()
    {
        string path = Path.Combine(root, "NightRun.exe");
        if (!File.Exists(path)) { status.Text = "Не найден NightRun.exe рядом с загрузчиком."; return; }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = root, ArgumentList = { "--settings" } });
            status.Text = "Окно настроек открыто.";
        }
        catch (Exception ex) { status.Text = "Ошибка запуска: " + ex.Message; }
    }
}
