using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NightRun;

sealed class MenuUI : IDisposable
{
    readonly OverlayForm ov;
    readonly GameReader reader;
    readonly AimBot aim;

    readonly Font fTitle = new Font("Consolas", 12f, FontStyle.Bold);
    readonly Font fText = new Font("Consolas", 8.5f);
    readonly Font fVal = new Font("Consolas", 8.5f, FontStyle.Bold);
    readonly Bitmap measureBitmap = new Bitmap(1, 1);
    readonly Graphics measureG;

    static readonly Color Ac = Color.FromArgb(0, 255, 255);
    static readonly Color Ac2 = Color.FromArgb(50, 150, 255);
    static readonly Color Bg = Color.FromArgb(20, 0, 30);
    static readonly Color Bg2 = Color.FromArgb(25, 15, 35);
    static readonly Color Tx = Color.FromArgb(220, 255, 255);
    static readonly Color Tx2 = Color.FromArgb(120, 180, 255);
    static readonly Color Fld = Color.FromArgb(30, 10, 20, 30);

    readonly string[] tabs = { "AIM", "ESP", "COLORS", "MISC" };
    int tab;

    int hotId, activeId;
    float dragStartX, dragStartY, dragOrigX, dragOrigY;
    // Inertia after release
    float dragVelX, dragVelY;
    float inertiaX, inertiaY; // current interpolated position during inertia

    float mx, my; bool mdown, mprev, mclick, released;
    // Button hover animation
    readonly Dictionary<int, float> hoverAnim = new Dictionary<int, float>(); // id -> hover progress 0-1 (0 = normal, 1 = hovered)
    readonly HashSet<int> prevHoverIds = new HashSet<int>();
    // Update hover animation progress for a given button id.
    void UpdateHoverAnim(int id, bool isHover)
    {
        // Target progress: 1 when hovered, otherwise decay towards 0.
        float target = isHover ? 1f : 0f;
        // Retrieve current progress; default 0.
        hoverAnim.TryGetValue(id, out float cur);
        // Simple linear interpolation with a speed factor.
        float speed = 0.15f; // per frame step (~60fps gives ~0.15 per frame for smooth.
        if (Math.Abs(cur - target) < 0.01f) cur = target;
        else cur = cur + (target - cur) * speed;
        hoverAnim[id] = cur;
    }

    public MenuUI(OverlayForm ov, GameReader reader, AimBot aim)
    {
        this.ov = ov; this.reader = reader; this.aim = aim;
        measureG = Graphics.FromImage(measureBitmap);
    }

    public Rectangle Bounds => Rectangle.FromLTRB((int)Cfg.S.MenuX - 2, (int)Cfg.S.MenuY - 2, (int)Cfg.S.MenuX + 442, (int)Cfg.S.MenuY + 482);

    public void Draw(Graphics g)
    {
        if (!ov.MenuOpen) { mprev = false; activeId = 0; return; }
        var c = Cfg.S;
        if (c.NewGui)
        {
            // Новый неоновый интерфейс: увеличенные размеры, плавная анимация, тёмно-неоновая палитра.
            UpdateInput((int)c.MenuX, (int)c.MenuY, 520, 550);
            c.MenuX = Math.Clamp(c.MenuX, -470, Math.Max(-470, ov.Sw - 60));
            c.MenuY = Math.Clamp(c.MenuY, 0, Math.Max(0, ov.Sh - 60));
            RenderMenuNeon(g, (int)c.MenuX, (int)c.MenuY, 520, 550);
        }
        else
        {
            // Классический интерфейс (логика без изменений, сохранена совместимость с конфигом).
            UpdateInput((int)c.MenuX, (int)c.MenuY, 430, 470);
            c.MenuX = Math.Clamp(c.MenuX, -370, Math.Max(-370, ov.Sw - 60));
            c.MenuY = Math.Clamp(c.MenuY, 0, Math.Max(0, ov.Sh - 40));
            RenderMenuClassic(g, (int)c.MenuX, (int)c.MenuY, 430, 470);
        }
    }

    void RenderMenuClassic(Graphics g, int x, int y, int w, int h)
    {

        using (GraphicsPath sh = Round(x + 4, y + 6, w, h, 10)) g.FillPath(Brush(Color.FromArgb(90, 0, 0, 0)), sh);
        using (GraphicsPath p = Round(x, y, w, h, 10))
        {
            g.FillPath(Brush(Bg), p);
            using (LinearGradientBrush gb = new LinearGradientBrush(new Rectangle(x, y, w, 3), Ac, Ac2, 0f))
                g.FillRectangle(gb, x + 10, y, w - 20, 2);
        }
        using (GraphicsPath p = Round(x, y, w, h, 10)) g.DrawPath(Pen(Color.FromArgb(70, Ac), 1f), p);

        g.DrawString("NIGHT RUN", fTitle, Brush(Tx), x + 14, y + 10);
        g.DrawString("v2.0", fText, Brush(Tx2), x + 92, y + 13);
        DrawStateChip(g, x + w - 150, y + 10, 60);
        DrawSmallButton(g, x + w - 82, y + 9, 34, 16, "HIDE", false, 1000);
        if (Clicked(x + w - 82, y + 9, 34, 16)) ov.SetMenuOpen(false);
        DrawSmallButton(g, x + w - 44, y + 9, 30, 16, "X", false, 1001);
        if (Clicked(x + w - 44, y + 9, 30, 16)) { PanicAll(); ov.SetMenuOpen(false); }

        int tx = x + 14;
        for (int i = 0; i < tabs.Length; i++)
        {
            int tw = TabWidth(tabs[i]);
            bool on = tab == i;
            if (on) g.FillRectangle(Brush(Color.FromArgb(60, Ac)), tx, y + 34, tw, 20);
            g.DrawString(tabs[i], fText, Brush(on ? Ac : Tx2), tx + tw / 2f - TextW(tabs[i]) / 2f, y + 38);
            if (on) g.FillRectangle(Brush(Ac), tx, y + 53, tw, 2);
            if (Clicked(tx, y + 34, tw, 20)) tab = i;
            tx += tw + 6;
        }
        g.DrawLine(Pen(Color.FromArgb(40, 255, 255, 255), 1f), x + 12, y + 55, x + w - 12, y + 55);

        int cy = y + 64;
        try
        {
            switch (tab)
            {
                case 0: DrawAim(g, x + 14, cy, w - 28); break;
                case 1: DrawEsp(g, x + 14, cy, w - 28); break;
                case 2: DrawColors(g, x + 14, cy, w - 28); break;
                default: DrawMisc(g, x + 14, cy, w - 28); break;
            }
        }
        catch (Exception ex) { GameReader.Log(ex); }

        g.DrawString("RShift menu  |  F1 box  F2 aim  F3 skel  F9 panic  F10 exit", fText, Brush(Tx2), x + 14, y + h - 18);
    }

    void PanicAll() => Cfg.Panic();

    void UpdateInput(int px, int py, int pw, int ph)
    {
        var cursor = ov.PointToClient(System.Windows.Forms.Cursor.Position);
        mx = cursor.X; my = cursor.Y;
        mdown = Rw.Key(0x01);
        mclick = mdown && !mprev;
        released = !mdown && mprev;
        mprev = mdown;
        hotId = 0;

        // Track previous menu position for velocity calculation
        float prevMenuX = Cfg.S.MenuX, prevMenuY = Cfg.S.MenuY;

        if (mclick && mx >= px && mx <= px + pw - 180 && my >= py + 4 && my <= py + 30)
        {
            activeId = -1;
            dragStartX = mx; dragStartY = my;
            dragOrigX = Cfg.S.MenuX; dragOrigY = Cfg.S.MenuY;
            dragVelX = dragVelY = 0; // reset velocity on new drag
        }
        if (activeId == -1 && mdown)
        {
            Cfg.S.MenuX = dragOrigX + (mx - dragStartX);
            Cfg.S.MenuY = dragOrigY + (my - dragStartY);
            // Calculate instantaneous velocity
            dragVelX = Cfg.S.MenuX - prevMenuX;
            dragVelY = Cfg.S.MenuY - prevMenuY;
        }
        else if (released && activeId == -1)
        {
            // Apply inertia on release
            activeId = 0;
            Cfg.Touch();
        }
        else if (activeId == 0 && (Math.Abs(dragVelX) > 0.5f || Math.Abs(dragVelY) > 0.5f))
        {
            // Apply inertia with friction
            Cfg.S.MenuX += dragVelX;
            Cfg.S.MenuY += dragVelY;
            dragVelX *= 0.92f; // friction
            dragVelY *= 0.92f;
            Cfg.Touch();
        }

        // Hover animation logic
        HashSet<int> currentHoverIds = new HashSet<int>();
        if (hotId != 0) currentHoverIds.Add(hotId);
        foreach (var id in prevHoverIds)
        {
            if (id == 1000 || id == 1001) continue;
            if (!currentHoverIds.Contains(id))
            {
                if (hoverAnim.TryGetValue(id, out var v))
                {
                    hoverAnim[id] = Math.Max(0, v - 0.15f);
                    if (hoverAnim[id] <= 0) hoverAnim.Remove(id);
                }
            }
        }
        if (hotId != 0)
        {
            hoverAnim[hotId] = Math.Min(1, (hoverAnim.TryGetValue(hotId, out var v) ? v : 0) + 0.15f);
        }
        // Small button hover detection
        if (Hover(px + pw - 82, py + 9, 34, 16)) hotId = 1000;
        else if (Hover(px + pw - 44, py + 9, 30, 16)) hotId = 1001;
        if (hotId != 0) currentHoverIds.Add(hotId);
        prevHoverIds.Clear();
        foreach (var id in currentHoverIds) prevHoverIds.Add(id);
    }

    bool Hover(float x, float y, float w, float h) => mx >= x && mx <= x + w && my >= y && my <= y + h;
    bool Clicked(float x, float y, float w, float h) => Hover(x, y, w, h) && mclick;

    float TextW(string s) => measureG.MeasureString(s, fText).Width - 4;
    int TabWidth(string s)
    {
        float w = TextW(s) + 26;
        return (int)w;
    }

    public bool Contains(Point screenPt)
    {
        var c = Cfg.S;
        var pt = ov.PointToClient(screenPt);
        var cw = c.NewGui ? 520 : 430;
        var ch = c.NewGui ? 550 : 470;
        return pt.X >= c.MenuX && pt.X <= c.MenuX + cw && pt.Y >= c.MenuY && pt.Y <= c.MenuY + ch;
    }

    void FloatSlider(Graphics g, ref int y, float x, int w, string label, ref float val, float min, float max, int id, string suffix = "")
    {
        g.DrawString(label, fText, Brush(Tx2), x, y);
        string vs = val.ToString("0.0") + suffix;
        g.DrawString(vs, fVal, Brush(Tx), x + w - TextW(vs) - 2, y);
        y += 14;
        float bx = x, by = y, bw = w, bh = 6;
        bool hv = Hover(bx - 2, by - 4, bw + 4, bh + 8);
        if (hv) hotId = id;
        if (hv && mclick) activeId = id;
        if (activeId == id && mdown)
        {
            float t = Math.Clamp((mx - bx) / bw, 0f, 1f);
            float nv = min + t * (max - min);
            if (Math.Abs(nv - val) > 0.01f) { val = nv; Cfg.Touch(); }
        }
        if (released && activeId == id) activeId = 0;
        using GraphicsPath p = Round(bx, by, bw, bh, 3);
        g.FillPath(Brush(Fld), p);
        float t2 = (val - min) / Math.Max(0.01f, max - min);
        if (t2 > 0)
        {
            using GraphicsPath f = Round(bx, by, Math.Max(4, bw * t2), bh, 3);
            using LinearGradientBrush lg = new LinearGradientBrush(new Rectangle((int)bx, (int)by, Math.Max(2, (int)(bw * t2)), (int)bh), Ac2, Ac, 0f);
            g.FillPath(lg, f);
        }
        float kx = bx + bw * t2;
        g.FillEllipse(Brush(Color.FromArgb(255, 220, 232, 250)), kx - 4, by - 2, 8, 10);
        g.DrawEllipse(Pen(Ac, 1f), kx - 4, by - 2, 8, 10);
        y += 18;
    }

    void DrawStateChip(Graphics g, float x, float y, float w)
    {
        string s;
        Color c;
        if (reader.Resolving) { s = "RESOLVE " + (int)(reader.ResolveProgress * 100) + "%"; c = Color.FromArgb(255, 190, 70); }
        else if (!reader.AddressesValid) { s = "OFFLINE"; c = Color.FromArgb(255, 90, 90); }
        else { s = "LIVE"; c = Color.FromArgb(110, 220, 130); }
        using GraphicsPath p = Round(x, y, w, 16, 5);
        g.FillPath(Brush(Color.FromArgb(200, 10, 12, 18)), p);
        g.DrawPath(Pen(Color.FromArgb(120, c), 1f), p);
        g.DrawString(s, fText, Brush(c), x + 6, y + 2);
    }

    void DrawSmallButton(Graphics g, float x, float y, float w, float h, string text, bool on, int btnId)
    {
        // Update hover animation for this button
        bool isHover = Hover(x, y, w, h);
        UpdateHoverAnim(btnId, isHover);
        float progress = hoverAnim.TryGetValue(btnId, out var hoverP) ? hoverP : 0f;
        Color bg = Color.FromArgb((int)(170 + 30 * progress), 24, 28, 40);
        // Slight size pulse on hover
        float scale = 1f + 0.04f * progress;
        float w2 = w * scale;
        float h2 = h * scale;
        float dx = (w - w2) / 2f;
        float dy = (h - h2) / 2f;
        using GraphicsPath path = Round(x + dx, y + dy, w2, h2, 4);
        g.FillPath(Brush(bg), path);
        g.DrawPath(Pen(Color.FromArgb(90, on ? Ac : Color.FromArgb(90, 100, 120)), 1f), path);
        g.FillPath(Brush(bg), p);
        g.DrawPath(Pen(Color.FromArgb(90, on ? Ac : Color.FromArgb(90, 100, 120)), 1f), p);
        g.DrawString(text, fText, Brush(on ? Ac : Tx), x + w / 2f - TextW(text) / 2f, y + 2);
    }

    void Section(Graphics g, ref int y, float x, int w, string label)
    {
        y += 6;
        g.DrawString(label, fText, Brush(Ac), x, y);
        y += 14;
        g.DrawLine(Pen(Color.FromArgb(45, Ac), 1f), x, y, x + w, y);
        y += 5;
    }

    void Checkbox(Graphics g, ref int y, float x, int w, string label, ref bool val, int id)
    {
        float cx = x, cy = y + 1;
        bool hv = Hover(cx - 2, cy - 2, w, 16);
        if (hv) hotId = id;
        if (Clicked(cx - 2, cy - 2, w, 16)) { val = !val; Cfg.Touch(); }
        using GraphicsPath p = Round(cx, cy, 12, 12, 3);
        g.FillPath(Brush(Fld), p);
        g.DrawPath(Pen(Color.FromArgb(hv ? 200 : 100, val ? Ac : Color.FromArgb(90, 100, 120)), 1f), p);
        if (val)
        {
            using GraphicsPath k = Round(cx + 3, cy + 3, 6, 6, 2);
            g.FillPath(Brush(Ac), k);
            if (hv) g.FillRectangle(Brush(Color.FromArgb(30, Ac)), cx + 18, cy + 1, w - 20, 12);
        }
        g.DrawString(label, fText, Brush(val ? Tx : Tx2), cx + 18, y);
        y += 17;
    }

    void Slider(Graphics g, ref int y, float x, int w, string label, ref int val, int min, int max, int id, string suffix = "")
    {
        g.DrawString(label, fText, Brush(Tx2), x, y);
        string vs = val.ToString() + suffix;
        g.DrawString(vs, fVal, Brush(Tx), x + w - TextW(vs) - 2, y);
        y += 14;
        float bx = x, by = y, bw = w, bh = 6;
        bool hv = Hover(bx - 2, by - 4, bw + 4, bh + 8);
        if (hv) hotId = id;
        if (hv && mclick) activeId = id;
        if (activeId == id && mdown)
        {
            float t = Math.Clamp((mx - bx) / bw, 0f, 1f);
            int nv = min + (int)Math.Round(t * (max - min));
            if (nv != val) { val = nv; Cfg.Touch(); }
        }
        if (released && activeId == id) activeId = 0;
        using GraphicsPath p = Round(bx, by, bw, bh, 3);
        g.FillPath(Brush(Fld), p);
        float t2 = (float)(val - min) / Math.Max(1, max - min);
        if (t2 > 0)
        {
            using GraphicsPath f = Round(bx, by, Math.Max(4, bw * t2), bh, 3);
            using LinearGradientBrush lg = new LinearGradientBrush(new Rectangle((int)bx, (int)by, Math.Max(2, (int)(bw * t2)), (int)bh), Ac2, Ac, 0f);
            g.FillPath(lg, f);
        }
        float kx = bx + bw * t2;
        g.FillEllipse(Brush(Color.FromArgb(255, 220, 232, 250)), kx - 4, by - 2, 8, 10);
        g.DrawEllipse(Pen(Ac, 1f), kx - 4, by - 2, 8, 10);
        y += 18;
    }

    void Choice(Graphics g, ref int y, float x, int w, string label, ref int val, string[] options, int id)
    {
        g.DrawString(label, fText, Brush(Tx2), x, y);
        y += 14;
        float cx = x;
        for (int i = 0; i < options.Length; i++)
        {
            float bw = TextW(options[i]) + 14;
            bool on = val == i;
            bool hv = Hover(cx, y, bw, 17);
            if (hv) hotId = id + i;
            using GraphicsPath p = Round(cx, y, bw, 17, 4);
            g.FillPath(Brush(on ? Color.FromArgb(120, Ac) : Fld), p);
            g.DrawPath(Pen(Color.FromArgb(hv ? 220 : 90, on ? Ac : Color.FromArgb(90, 100, 120)), 1f), p);
            g.DrawString(options[i], fText, Brush(on ? Color.FromArgb(255, 240, 248, 255) : Tx2), cx + 7, y + 2);
            if (Clicked(cx, y, bw, 17)) { val = i; Cfg.Touch(); }
            cx += bw + 5;
        }
        y += 24;
    }

    void ColorRow(Graphics g, ref int y, float x, int w, string label, int idx)
    {
        g.DrawString(label, fText, Brush(Tx), x, y);
        var c = Cfg.S.RoleColors[idx];
        float px = x + w - 150;
        float[] chans = { 0, 1, 2 };
        for (int ch = 0; ch < 3; ch++)
        {
            float sx = px + ch * 50;
            int v = ch == 0 ? c.R : ch == 1 ? c.G : c.B;
            Color bar = Color.FromArgb(v, 40, 40, 46);
            g.DrawString(v.ToString(), fText, Brush(Tx2), sx, y + 12);
            var r = new RectangleF(sx, y + 2, 46, 8);
            g.FillRectangle(Brush(Fld), r);
            g.FillRectangle(Brush(Color.FromArgb(200, 40, 40, 46)), sx, y + 2, 46 * v / 255f, 8);
            g.DrawRectangle(Pen(Color.FromArgb(90, 90, 100, 120), 1f), sx, y + 2, 46, 8);
            bool hv = Hover(sx, y, 46, 18);
            if (hv && mclick) activeId = 1000 + idx * 4 + ch;
            if (activeId == 1000 + idx * 4 + ch && mdown)
            {
                float t = Math.Clamp((mx - sx) / 46f, 0f, 1f);
                int nv = (int)(t * 255);
                Cfg.S.RoleColors[idx] = ch == 0 ? Color.FromArgb(nv, c.G, c.B) : ch == 1 ? Color.FromArgb(c.R, nv, c.B) : Color.FromArgb(c.R, c.G, nv);
                Cfg.Touch();
            }
        }
        if (released && activeId >= 1000) activeId = 0;
        using GraphicsPath p = Round(x + w - 30, y + 1, 26, 15, 4);
        g.FillPath(Brush(Cfg.S.RoleColors[idx]), p);
        g.DrawPath(Pen(Color.FromArgb(120, 220, 230, 245), 1f), p);
        y += 30;
    }

    void DrawAim(Graphics g, float x, int y, int w)
    {
        var c = Cfg.S;
        int id = 100;
        Checkbox(g, ref y, x, w, "Enable aim", ref c.Aim, id++);
        Choice(g, ref y, x, w, "Aim key", ref c.AimKey, new[] { "RMB", "XB1", "XB2", "RALT" }, id++);
        Choice(g, ref y, x, w, "Target bone", ref c.AimBone, new[] { "HEAD", "NECK", "CHEST" }, id++);
        Section(g, ref y, x, w, "TUNING");
        Slider(g, ref y, x, w, "Snap speed", ref c.AimSpeed, 5, 100, id++, "%");
        Slider(g, ref y, x, w, "FOV radius", ref c.AimFov, 20, 900, id++, "px");
        Slider(g, ref y, x, w, "Sticky radius", ref c.StickyRadius, 0, 900, id++, "px");
        Slider(g, ref y, x, w, "Relock delay", ref c.RelockDelay, 0, 2000, id++, "ms");
        Slider(g, ref y, x, w, "Prediction lead", ref c.AimLead, 0, 300, id++, "ms");
        Section(g, ref y, x, w, "FILTER");
        Checkbox(g, ref y, x, w, "Players only (skip NPC)", ref c.AimPlayersOnly, id++);
        g.DrawString("Target roles", fText, Brush(Tx2), x, y); y += 15;
        float bx = x;
        for (int r = 0; r < Roles.Count; r++)
        {
            string nm = Roles.Names[r];
            float bw = TextW(nm) + 14;
            if (bx + bw > x + w) { bx = x; y += 22; }
            bool on = c.AimRoles[r];
            bool hv = Hover(bx, y, bw, 17);
            using GraphicsPath p = Round(bx, y, bw, 17, 4);
            g.FillPath(Brush(on ? Color.FromArgb(120, Roles.RoleColor(r)) : Fld), p);
            g.DrawPath(Pen(Color.FromArgb(hv ? 220 : 90, on ? Roles.RoleColor(r) : Color.FromArgb(90, 100, 120)), 1f), p);
            g.DrawString(nm, fText, Brush(on ? Color.FromArgb(255, 245, 248, 255) : Tx2), bx + 7, y + 2);
            if (Clicked(bx, y, bw, 17)) { c.AimRoles[r] = !on; Cfg.Touch(); }
            bx += bw + 5;
        }
        y += 26;
        g.DrawString("Aim works only when the game window is focused.", fText, Brush(Tx2), x, y);
    }

    void DrawEsp(Graphics g, float x, int y, int w)
    {
        var c = Cfg.S;
        int id = 200;
        Choice(g, ref y, x, w, "Box style", ref c.BoxMode, new[] { "OFF", "CORNER", "FULL", "FILL" }, id++);
        FloatSlider(g, ref y, x, w, "Box width", ref c.BoxWidth, 0.6f, 4f, id++, "px");
        Checkbox(g, ref y, x, w, "Box glow / outline", ref c.BoxGlow, id++);
        Checkbox(g, ref y, x, w, "Skeleton", ref c.Skeleton, id++);
        Checkbox(g, ref y, x, w, "Hitboxes", ref c.Hitboxes, id++);
        Checkbox(g, ref y, x, w, "Joints", ref c.Joints, id++);
        Checkbox(g, ref y, x, w, "Health bar", ref c.HealthBar, id++);
        Section(g, ref y, x, w, "TAG / LINES");
        Choice(g, ref y, x, w, "Tag", ref c.TagMode, new[] { "OFF", "ROLE+DIST", "ROLE" }, id++);
        Choice(g, ref y, x, w, "Snapline", ref c.Snapline, new[] { "OFF", "BOTTOM", "CENTER" }, id++);
        Section(g, ref y, x, w, "RANGE / FILTER");
        Slider(g, ref y, x, w, "Max distance", ref c.MaxDist, 50, 1000, id++, "m");
        Checkbox(g, ref y, x, w, "Show dead", ref c.ShowDead, id++);
        Checkbox(g, ref y, x, w, "Show self", ref c.ShowSelf, id++);
        Checkbox(g, ref y, x, w, "Players only", ref c.PlayersOnly, id++);
        Checkbox(g, ref y, x, w, "Loot", ref c.Loot, id++);
        Slider(g, ref y, x, w, "Loot distance", ref c.LootDist, 20, 500, id++, "m");
    }

    void DrawColors(Graphics g, float x, int y, int w)
    {
        var c = Cfg.S;
        int id = 300;
        Choice(g, ref y, x, w, "Scheme", ref c.ColorScheme, new[] { "ROLE", "CYAN", "WHITE", "GREEN", "RED" }, id++);
        Section(g, ref y, x, w, "FACTION COLORS");
        for (int i = 0; i < Roles.Count; i++) ColorRow(g, ref y, x, w, Roles.Names[i], i);
        Section(g, ref y, x, w, "ALPHA");
        Slider(g, ref y, x, w, "Box alpha", ref c.BoxAlpha, 40, 255, id++);
        Slider(g, ref y, x, w, "Skeleton alpha", ref c.SkeletonAlpha, 40, 255, id++);
        FloatSlider(g, ref y, x, w, "Skeleton width", ref c.SkeletonWidth, 0.6f, 4f, id++, "px");
        g.DrawString("Scheme ROLE uses the colors above.", fText, Brush(Tx2), x, y);
    }

    void DrawMisc(Graphics g, float x, int y, int w)
    {
        var c = Cfg.S;
        int id = 400;
        g.DrawString("PID " + reader.GamePid + "   local id " + reader.LocalId + "   role " + Roles.Name(reader.LocalRole), fText, Brush(Tx), x, y); y += 16;
        g.DrawString("game 0x" + reader.Game.ToString("X") + "   updater 0x" + reader.Updater.ToString("X"), fText, Brush(Tx2), x, y); y += 16;
        g.DrawString("fps " + (int)ov.Fps + "   status " + reader.Status, fText, Brush(Tx2), x, y); y += 20;
        Section(g, ref y, x, w, "SESSION");
        Checkbox(g, ref y, x, w, "Show fps", ref c.Fps, id++);
        if (Hover(x, y, 120, 22) && mclick) reader.RequestResolve();
        DrawButton(g, x, y, 130, 22, "RE-RESOLVE", id++); y += 28;
        if (Hover(x, y, 130, 22) && mclick) PanicAll();
        DrawButton(g, x, y, 130, 22, "PANIC OFF", id++); y += 34;
        g.DrawString("config saved automatically to nightrun.json", fText, Brush(Tx2), x, y); y += 14;
        g.DrawString("menu is click-through when hidden (RShift)", fText, Brush(Tx2), x, y);
    }

    void DrawButton(Graphics g, float x, float y, float w, float h, string text, int id)
    {
        bool hv = Hover(x, y, w, h);
        if (hv) hotId = id;
        using GraphicsPath p = Round(x, y, w, h, 5);
        g.FillPath(Brush(hv ? Color.FromArgb(200, Ac2) : Fld), p);
        g.DrawPath(Pen(Color.FromArgb(140, Ac), 1f), p);
        g.DrawString(text, fText, Brush(Tx), x + w / 2f - TextW(text) / 2f, y + 4);
    }

    void RenderMenuNeon(Graphics g, int x, int y, int w, int h)
    {
        // Внешнее неоновое свечение окна.
        using (GraphicsPath glow = Round(x - 10, y - 10, w + 20, h + 20, 16))
            g.FillPath(new SolidBrush(Color.FromArgb(50, 0, 255, 255)), glow);

        // Основной фон окна с акцентной полосой сверху.
        using (GraphicsPath p = Round(x, y, w, h, 12))
        {
            g.FillPath(new SolidBrush(Color.FromArgb(240, Bg.R, Bg.G, Bg.B)), p);
            using (LinearGradientBrush gb = new LinearGradientBrush(new Rectangle(x, y, w, 4), Ac, Ac2, 90f))
                g.FillRectangle(gb, x + 12, y, w - 24, 4);
        }
        // Рамка окна.
        using (GraphicsPath p = Round(x, y, w, h, 12))
            g.DrawPath(new Pen(Color.FromArgb(180, Ac), 1.6f), p);

        // Заголовок.
        using Font neonTitle = new Font("Consolas", 17f, FontStyle.Bold);
        g.DrawString("NIGHT RUN", neonTitle, Brush(Tx), x + 20, y + 14);
        using Font neonVer = new Font("Consolas", 9.5f, FontStyle.Bold);
        g.DrawString("v2.0 NEON", neonVer, Brush(Ac2), x + 170, y + 19);

        DrawStateChip(g, x + w - 190, y + 14, 160);

        // Кнопки управления окном (X и HIDE) с анимацией наведения.
        DrawSmallButtonNeon(g, x + w - 94, y + 12, 38, 24, "X", 9000);
        if (Clicked(x + w - 94, y + 12, 38, 24)) { PanicAll(); ov.SetMenuOpen(false); }
        DrawSmallButtonNeon(g, x + w - 138, y + 12, 38, 24, "HIDE", 9001);
        if (Clicked(x + w - 138, y + 12, 38, 24)) ov.SetMenuOpen(false);

        // Табы.
        int tx = x + 20;
        for (int i = 0; i < tabs.Length; i++)
        {
            int tw = TabWidth(tabs[i]) + 10;
            bool on = tab == i;
            bool hv = Hover(tx, y + 56, tw, 28);
            if (hv) hotId = 200 + i;
            UpdateHoverAnim(200 + i, hv || on);
            float ha = hoverAnim.TryGetValue(200 + i, out var v) ? v : 0f;
            using GraphicsPath tp = Round(tx, y + 56, tw, 28, 9);
            g.FillPath(new SolidBrush(Color.FromArgb(30 + (int)(40 * ha), 0, 10, 20)), tp);
            g.DrawPath(new Pen(Color.FromArgb(120 + (int)(135 * ha), on ? Ac : Tx2), 1.2f), tp);
            g.DrawString(tabs[i], fText, Brush(on ? Color.FromArgb(255, 255, 255) : Color.FromArgb(150 + (int)(105 * ha), Tx2.R, Tx2.G, Tx2.B)), tx + tw / 2f - TextW(tabs[i]) / 2f, y + 65);
            if (on) g.FillRectangle(new SolidBrush(Ac), tx + 6, y + 84, tw - 12, 3);
            if (Clicked(tx, y + 56, tw, 28)) tab = i;
            tx += tw + 10;
        }
        g.DrawLine(new Pen(Color.FromArgb(70, Ac), 1f), x + 16, y + 92, x + w - 16, y + 92);

        int cy = y + 106;
        try
        {
            switch (tab)
            {
                case 0: DrawAim(g, x + 20, cy, w - 40); break;
                case 1: DrawEsp(g, x + 20, cy, w - 40); break;
                case 2: DrawColors(g, x + 20, cy, w - 40); break;
                default: DrawMisc(g, x + 20, cy, w - 40); break;
            }
        }
        catch (Exception ex) { GameReader.Log(ex); }

        g.DrawString("RShift menu  |  F1 box  F2 aim  F3 skel  F9 panic  F10 exit", fText, Brush(Tx2), x + 20, y + h - 24);
    }

    void DrawSmallButtonNeon(Graphics g, float x, float y, float w, float h, string text, int btnId)
    {
        // Кнопка с плавной анимацией наведения и лёгким масштабированием.
        bool isHover = Hover(x, y, w, h);
        UpdateHoverAnim(btnId, isHover);
        float progress = hoverAnim.TryGetValue(btnId, out var p) ? p : 0f;
        float scale = 1f + 0.06f * progress;
        float w2 = w * scale, h2 = h * scale;
        float dx = (w - w2) / 2f, dy = (h - h2) / 2f;
        using GraphicsPath p = Round(x + dx, y + dy, w2, h2, 5);
        g.FillPath(new SolidBrush(Color.FromArgb(150 + (int)(100 * progress), 10, 10, 20)), p);
        g.DrawPath(new Pen(Color.FromArgb(80 + (int)(175 * progress), Ac), 1.2f), p);
        g.DrawString(text, fText, Brush(Color.FromArgb(255, 240, 255, 255)), x + w / 2f - TextW(text) / 2f, y + 3);
    }

    readonly Dictionary<int, SolidBrush> bs = new Dictionary<int, SolidBrush>();
    SolidBrush Brush(Color c) { int k = c.ToArgb(); if (bs.TryGetValue(k, out var b)) return b; if (bs.Count > 512) { foreach (var x in bs.Values) x.Dispose(); bs.Clear(); } b = new SolidBrush(c); bs[k] = b; return b; }
    readonly Dictionary<int, Pen> ps = new Dictionary<int, Pen>();
    Pen Pen(Color c, float w) { int k = c.ToArgb() ^ ((int)(w * 10) << 27); if (ps.TryGetValue(k, out var p)) return p; if (ps.Count > 256) { foreach (var x in ps.Values) x.Dispose(); ps.Clear(); } p = new Pen(c, w); ps[k] = p; return p; }
    public void Dispose()
    {
        foreach (var p in ps.Values) p.Dispose();
        foreach (var b in bs.Values) b.Dispose();
        measureG.Dispose(); measureBitmap.Dispose();
        fTitle.Dispose(); fText.Dispose(); fVal.Dispose();
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Min(r * 2, Math.Min(w, h));
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}
