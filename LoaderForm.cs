using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NightRun;

// Окно загрузчика (loader) в тёмно-неоновом стиле.
// Показывается перед запуском основного оверлея, затем закрывается.
sealed class LoaderForm : Form
{
    readonly Timer anim;
    float angle;

    public LoaderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(420, 260);
        BackColor = Color.FromArgb(15, 0, 25);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        anim = new Timer { Interval = 16 };
        anim.Tick += (_, _) => { angle += 6f; if (angle >= 360f) angle = 0f; Invalidate(); };
        anim.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80; // загрузочное окно без фокуса/задачи
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        g.Clear(Color.FromArgb(15, 0, 25));

        // Внешнее неоновое свечение вокруг окна.
        using (var glowPath = Round(ClientRectangle.X + 2, ClientRectangle.Y + 2, ClientRectangle.Width - 4, ClientRectangle.Height - 4, 16))
        using (var glowBrush = new SolidBrush(Color.FromArgb(40, 0, 255, 255)))
            g.FillPath(glowBrush, glowPath);

        // Рамка окна.
        using (var border = new Pen(Color.FromArgb(200, 0, 255, 255), 2f))
            g.DrawPath(border, Round(ClientRectangle.X + 2, ClientRectangle.Y + 2, ClientRectangle.Width - 4, ClientRectangle.Height - 4, 16));

        // Заголовок.
        using (var titleFont = new Font("Consolas", 20f, FontStyle.Bold))
        {
            var ts = g.MeasureString("NIGHT RUN", titleFont);
            g.DrawString("NIGHT RUN", titleFont, Brushes.White, ClientRectangle.Width / 2f - ts.Width / 2f, ClientRectangle.Height / 2f - 62f);
        }

        // Подпись состояния.
        using (var subFont = new Font("Consolas", 9f, FontStyle.Bold))
        using (var subBrush = new SolidBrush(Color.FromArgb(0, 255, 255)))
        {
            var sub = "LOADING MODULES...";
            var ss = g.MeasureString(sub, subFont);
            g.DrawString(sub, subFont, subBrush, ClientRectangle.Width / 2f - ss.Width / 2f, ClientRectangle.Height / 2f - 10f);
        }

        // Круговой индикатор загрузки.
        float cx = ClientRectangle.Width / 2f, cy = ClientRectangle.Height / 2f + 36f, r = 26f;
        using (var arcPen = new Pen(Color.FromArgb(0, 255, 255), 3f))
            g.DrawArc(arcPen, cx - r, cy - r, r * 2f, r * 2f, -90f + angle, 120f);

        // Точка-индикатор по окружности.
        double rad = angle * Math.PI / 180.0;
        using (var dot = new SolidBrush(Color.FromArgb(0, 255, 255)))
            g.FillEllipse(dot, cx + (float)Math.Cos(rad) * r - 4f, cy + (float)Math.Sin(rad) * r - 4f, 8f, 8f);
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = Math.Min(r * 2f, Math.Min(w, h));
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
