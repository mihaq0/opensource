using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NightRun;

sealed class Painter : IDisposable
{
    readonly OverlayForm ov;
    readonly GameReader reader;
    readonly AimBot aim;
    internal Rectangle Region;
    Snap frame;
    readonly List<(Ent Entity, float Distance)> ordered = new List<(Ent, float)>(160);
    static readonly bool selfTest = Environment.GetEnvironmentVariable("NR_SELFTEST") == "1";

    readonly Font fTagS = new Font("Segoe UI Semibold", 7f, FontStyle.Bold);
    readonly StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    readonly GraphicsPath linePath = new GraphicsPath();
    readonly Dictionary<(int, float), Pen> pens = new Dictionary<(int, float), Pen>();
    readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
    readonly Dictionary<string, SizeF> measure = new Dictionary<string, SizeF>();
    int lastCfgVersion = -1;

    public Painter(OverlayForm ov, GameReader reader, AimBot aim)
    {
        this.ov = ov; this.reader = reader; this.aim = aim;
    }

    void CheckVersion()
    {
        if (Cfg.Version == lastCfgVersion) return;
        lastCfgVersion = Cfg.Version;
        foreach (var p in pens.Values) p.Dispose(); pens.Clear();
        foreach (var b in brushes.Values) b.Dispose(); brushes.Clear();
        measure.Clear();
    }

    Pen Pen(Color c, float w)
    {
        var key = (c.ToArgb(), w);
        if (!pens.TryGetValue(key, out var p)) pens[key] = p = new Pen(c, w);
        return p;
    }
    SolidBrush Brush(Color c)
    {
        if (!brushes.TryGetValue(c.ToArgb(), out var b)) brushes[c.ToArgb()] = b = new SolidBrush(c);
        return b;
    }
    SizeF Measure(Graphics g, string s)
    {
        if (measure.TryGetValue(s, out var size)) return size;
        if (measure.Count >= 512) measure.Clear();
        return measure[s] = g.MeasureString(s, fTagS);
    }

    internal Rectangle Prepare(Snap snap, bool menuOpen)
    {
        frame = snap;
        Region = Rectangle.Empty;
        ordered.Clear();
        var c = Cfg.S;
        if (selfTest) Mark(100, 100, 450, 200);
        if (!snap.Cam.Valid || snap.AgeMs > 150)
        {
            if (c.Fps || menuOpen) Mark(ov.Sw - 650, ov.Sh - 32, 650, 32);
            return Region;
        }
        Cam cam = snap.Cam;
        double focal = (ov.Sh / 2.0) / Math.Tan(cam.Fov * Math.PI / 360.0);
        if (Cfg.EspEnabled)
            foreach (var e in snap.Ents)
            {
                if (!e.Valid || !GameReader.PassEsp(e, c, snap.LocalId)) continue;
                float dist = Dist(e, cam);
                if (dist > c.MaxDist) continue;
                if (!Project(cam, e.X, e.Y, e.Z, focal, out float bx, out float by, out _) ||
                    !Project(cam, e.X, e.Y + 1.8f, e.Z, focal, out float hx, out float hy, out _)) continue;
                float h = Math.Abs(by - hy), w = Math.Max(12, h * 0.42f);
                if (h < 8 || h > ov.Sh) continue;
                float left = hx - w / 2, top = Math.Min(by, hy);
                if (left + w < -300 || left > ov.Sw + 300 || top + h < -300 || top > ov.Sh + 300) continue;
                ordered.Add((e, dist));
                Mark(left - 50, top - 25, w + 100, h + 50);
                if (c.Snapline > 0) MarkLine(c.Snapline == 1 ? ov.Sw / 2f : bx, c.Snapline == 1 ? ov.Sh - 1 : ov.Sh / 2f, bx, by);
                if (c.Skeleton && e.SkelValid)
                    for (int i = 0; i < e.Segments.Length; i++)
                    {
                        Vec a = SegmentPoint(e, i, false), b = SegmentPoint(e, i, true);
                        if (Project(cam, a.X, a.Y, a.Z, focal, out float ax, out float ay, out _) &&
                            Project(cam, b.X, b.Y, b.Z, focal, out float xx, out float yy, out _)) MarkLine(ax, ay, xx, yy);
                    }
                if (c.Hitboxes)
                    foreach (Vec point in new[] { AimBot.BonePoint(e, 0), AimBot.BonePoint(e, 1), AimBot.BonePoint(e, 2), SkeletonPoint(e, e.Pelvis) })
                        if (Project(cam, point.X, point.Y, point.Z, focal, out float px, out float py, out _)) Mark(px - h * 0.2f - 5, py - h * 0.2f - 5, h * 0.4f + 10, h * 0.4f + 10);
            }
        ordered.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        if (c.Loot)
            foreach (var item in snap.Loot)
                if (Project(cam, item.P.X, item.P.Y, item.P.Z, focal, out float x, out float y, out _) &&
                    MathV.Dist(item.P, new Vec { X = cam.X, Y = cam.Y, Z = cam.Z }) <= c.LootDist) Mark(x - 8, y - 10, 300, 30);
        if (c.Aim && aim.LockedId >= 0)
            foreach (var e in snap.Ents)
                if (e.Id == aim.LockedId)
                {
                    Vec point = AimBot.BonePoint(e, c.AimBone);
                    if (Project(cam, point.X, point.Y, point.Z, focal, out float x, out float y, out _)) Mark(x - 16, y - 16, 32, 32);
                }
        if (c.Aim && menuOpen) Mark(ov.Sw / 2f - c.AimFov - 4, ov.Sh / 2f - c.AimFov - 4, c.AimFov * 2 + 8, c.AimFov * 2 + 8);
        if (c.Fps || menuOpen) Mark(ov.Sw - 650, ov.Sh - 32, 650, 32);
        return Region;
    }

    public void Draw(Graphics g, bool menuOpen)
    {
        var c = Cfg.S;
        CheckVersion();
        Snap snap = frame ?? reader.Snap;
        if (frame == null) Prepare(snap, menuOpen);
        if (selfTest)
        {
            g.FillRectangle(Brush(Color.Magenta), 100, 100, 200, 200);
            g.FillRectangle(Brush(Color.FromArgb(128, 0, 255, 0)), 350, 100, 200, 200);
        }
        Cam cam = snap.Cam;
        if (!cam.Valid || snap.AgeMs > 150) { if (c.Fps || menuOpen) DrawStatus(g, -1); return; }
        double focal = (ov.Sh / 2.0) / Math.Tan(cam.Fov * Math.PI / 360.0);
        if (c.Loot) DrawLoot(g, snap, cam, focal);
        foreach (var item in ordered) DrawEntity(g, item.Entity, cam, focal, item.Distance);

        if (aim.LockedId >= 0)
        {
            foreach (Ent en in snap.Ents)
                if (en.Id == aim.LockedId && en.Valid)
                {
                    DrawAimMarker(g, en, cam, focal);
                    break;
                }
        }
        if (c.Aim && menuOpen) DrawFovCircle(g);

        if (c.Fps || menuOpen) DrawStatus(g, snap.Ents.Length);
    }

    void Mark(float x, float y, float w, float h)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(w) || !float.IsFinite(h)) return;
        float x1 = Math.Max(0, x), y1 = Math.Max(0, y);
        float x2 = Math.Min(ov.Sw, x + w), y2 = Math.Min(ov.Sh, y + h);
        if (x2 <= x1 || y2 <= y1) return;
        var r = Rectangle.FromLTRB((int)Math.Floor(x1), (int)Math.Floor(y1), (int)Math.Ceiling(x2), (int)Math.Ceiling(y2));
        Region = Region.IsEmpty ? r : Rectangle.Union(Region, r);
    }
    void MarkLine(float x1, float y1, float x2, float y2) => Mark(Math.Min(x1, x2) - 8, Math.Min(y1, y2) - 8, Math.Abs(x2 - x1) + 16, Math.Abs(y2 - y1) + 16);
    void MarkRect(float x, float y, float w, float h) => Mark(x - 50, y - 25, w + 100, h + 50);
    static float Dist(Ent e, Cam c) => MathV.Dist(new Vec { X = e.X, Y = e.Y, Z = e.Z }, new Vec { X = c.X, Y = c.Y, Z = c.Z });

    internal static Vec SkeletonPoint(Ent e, Vec point) => MathV.Add(point, MathV.Sub(new Vec { X = e.X, Y = e.Y, Z = e.Z }, e.SkelOrigin));

    internal static Vec SegmentPoint(Ent e, int index, bool end)
    {
        Vec current = end ? e.Segments[index].B : e.Segments[index].A;
        Vec local = MathV.Sub(current, e.SkelOrigin);
        if (e.PreviousSkelTicks > 0 && e.PreviousSegments.Length == e.Segments.Length)
        {
            Vec previous = end ? e.PreviousSegments[index].B : e.PreviousSegments[index].A;
            double interval = (e.SkelTicks - e.PreviousSkelTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            float t = interval <= 0 ? 1 : (float)Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(e.SkelTicks).TotalSeconds / Math.Min(interval, 0.1), 0, 1);
            local = MathV.Lerp(MathV.Sub(previous, e.PreviousSkelOrigin), local, t);
        }
        return MathV.Add(new Vec { X = e.X, Y = e.Y, Z = e.Z }, local);
    }

    bool Project(Cam c, float x, float y, float z, double focal, out float sx, out float sy, out float depth)
    {
        sx = sy = 0; depth = 0;
        float dx = x - c.X, dy = y - c.Y, dz = z - c.Z;
        depth = dx * c.Fx + dy * c.Fy + dz * c.Fz;
        if (depth <= 0.25f) return false;
        float rr = dx * c.Rx + dy * c.Ry + dz * c.Rz;
        float uu = dx * c.Ux + dy * c.Uy + dz * c.Uz;
        sx = (float)(ov.Sw / 2.0 + rr / depth * focal);
        sy = (float)(ov.Sh / 2.0 - uu / depth * focal);
        return float.IsFinite(sx) && float.IsFinite(sy);
    }

    void DrawEntity(Graphics g, Ent en, Cam cam, double focal, float dist)
    {
        var c = Cfg.S;
        float topY = en.Y + 1.8f;
        if (!Project(cam, en.X, en.Y, en.Z, focal, out float bx, out float by, out float bd) || bd <= 0.2f) return;
        if (!Project(cam, en.X, topY, en.Z, focal, out float hx, out float hy, out float hd) || hd <= 0.2f) return;
        float top = Math.Min(by, hy), bot = Math.Max(by, hy);
        float h = bot - top;
        if (h < 8 || h > ov.Sh) return;
        float w = Math.Max(12, h * 0.42f);
        float left = hx - w / 2;

        bool enemy = Roles.IsEnemy(frame.LocalRole, en.Role);
        Color col = c.BoxColor(en.Role);
        int alpha = c.BoxAlpha;
        MarkRect(left, top, w, h);
        if (en.Dead) alpha /= 2;
        Color bc = Color.FromArgb(alpha, col);

        if (c.Snapline > 0)
        {
            float sx = c.Snapline == 1 ? ov.Sw / 2f : bx;
            float sy = c.Snapline == 1 ? ov.Sh - 1 : ov.Sh / 2f;
            g.DrawLine(Pen(Color.FromArgb(70, col), 1), sx, sy, bx, by);
        }

        if (c.BoxMode > 0)
        {
            if (c.BoxGlow) DrawBox(g, left - 1, top - 1, w + 2, h + 2, Color.FromArgb(alpha * 2 / 5, col), Math.Max(1f, c.BoxWidth + 2.2f), true);
            if (c.BoxMode == 3) g.FillRectangle(Brush(Color.FromArgb(28, col)), left, top, w, h);
            DrawBox(g, left, top, w, h, bc, c.BoxWidth, c.BoxMode == 1);
            if (enemy) DrawBox(g, left - 3, top - 3, w + 6, h + 6, Color.FromArgb(Math.Min(255, alpha + 40), Color.FromArgb(255, 90, 90)), 1f, c.BoxMode == 1);
        }

        if (c.HealthBar && !en.Dead && en.MaxHealth > 0)
        {
            float pct = Math.Clamp(en.Health / en.MaxHealth, 0f, 1f);
            pct = (int)(pct * 25) / 25f;
            float bw = 3.5f, bh = h;
            float bxp = left - 8, byp = top;
            g.FillRectangle(Brush(Color.FromArgb(140, 8, 10, 14)), bxp - 1, byp - 1, bw + 2, bh + 2);
            Color hc = Color.FromArgb(255, (int)(255 * (1 - pct)), (int)(255 * pct), 60);
            g.FillRectangle(Brush(hc), bxp, byp + bh * (1 - pct), bw, bh * pct);
        }

        if (c.TagMode > 0)
        {
            string label = Roles.Name(en.Role);
            if (!en.IsPlayer) label += " npc";
            if (enemy) label = "!" + label;
            if (c.TagMode == 1) label += "  " + (int)dist + "m";
            SizeF ts = Measure(g, label);
            float tw = Math.Min(w + 46, ts.Width + 14);
            float tx = left + w / 2 - tw / 2;
            float ty = Math.Max(2, top - 17);
            using GraphicsPath p = Round(tx, ty, tw, 15, 4);
            g.FillPath(Brush(Color.FromArgb(185, 10, 12, 18)), p);
            g.DrawPath(Pen(Color.FromArgb(150, col), 1f), p);
            g.DrawString(label, fTagS, Brush(Color.FromArgb(235, col)), new RectangleF(tx, ty, tw, 15), sf);
        }

        if (c.Skeleton && en.SkelValid)
        {
            float wSk = c.SkeletonWidth;
            Color sk = Color.FromArgb(c.SkeletonAlpha, en.Dead ? Color.FromArgb(140, 146, 160) : col);
            Pen glowPen = Pen(Color.FromArgb(c.SkeletonAlpha * 2 / 5, col), wSk + 2.4f);
            Pen corePen = Pen(sk, wSk);
            var segments = en.Segments;
            linePath.Reset();
            for (int i = 0; i < segments.Length; i++)
            {
                Vec a = SegmentPoint(en, i, false), b = SegmentPoint(en, i, true);
                if (!MathV.Finite(a) || !MathV.Finite(b)) continue;
                if (!Project(cam, a.X, a.Y, a.Z, focal, out float ax, out float ay, out float ad) || ad <= 0.2f) continue;
                if (!Project(cam, b.X, b.Y, b.Z, focal, out float b2x, out float b2y, out float bd2) || bd2 <= 0.2f) continue;
                if (!InScreen(ax, ay) || !InScreen(b2x, b2y)) continue;
                linePath.StartFigure();
                linePath.AddLine(ax, ay, b2x, b2y);
                MarkLine(ax, ay, b2x, b2y);
                if (c.Joints)
                {
                    g.FillEllipse(Brush(sk), ax - 1.6f, ay - 1.6f, 3.2f, 3.2f);
                    g.FillEllipse(Brush(sk), b2x - 1.6f, b2y - 1.6f, 3.2f, 3.2f);
                }
            }
            if (linePath.PointCount > 0)
            {
                g.DrawPath(glowPen, linePath);
                g.DrawPath(corePen, linePath);
            }
        }

        if (c.Hitboxes) DrawHitboxes(g, en, cam, focal, h, col, enemy);
    }

    void DrawHitboxes(Graphics g, Ent en, Cam cam, double focal, float boxH, Color col, bool enemy)
    {
        var c = Cfg.S;
        Vec head = AimBot.BonePoint(en, 0);
        Vec neck = AimBot.BonePoint(en, 1);
        Vec chest = AimBot.BonePoint(en, 2);
        Vec pelvis = en.SkelValid && !en.Pelvis.Equals(default) ? SkeletonPoint(en, en.Pelvis) : new Vec { X = en.X, Y = en.Y + 0.85f, Z = en.Z };

        void Box(Vec wp, float sizeRel, Color color, float width)
        {
            if (!Project(cam, wp.X, wp.Y, wp.Z, focal, out float x, out float y, out float d) || d <= 0.2f) return;
            if (!InScreen(x, y)) return;
            float s = Math.Max(5, boxH * sizeRel);
            DrawBox(g, x - s / 2, y - s / 2, s, s, color, width, false);
        }

        int a = c.SkeletonAlpha;
        int bone = c.AimBone;
        Color dim = Color.FromArgb(a * 3 / 4, col);
        Color accent = Color.FromArgb(255, 108, 92, 255);
        Box(head, 0.19f, bone == 0 ? accent : dim, bone == 0 ? 1.8f : 1f);
        Box(neck, 0.09f, bone == 1 ? accent : dim, bone == 1 ? 1.8f : 1f);
        Box(chest, 0.22f, bone == 2 ? accent : dim, bone == 2 ? 1.8f : 1f);
        Box(pelvis, 0.17f, dim, 1f);
        if (enemy) Box(head, 0.19f, Color.FromArgb(160, 255, 90, 90), 1f);
    }

    void DrawAimMarker(Graphics g, Ent en, Cam cam, double focal)
    {
        Vec p = AimBot.BonePoint(en, Cfg.S.AimBone);
        if (!Project(cam, p.X, p.Y, p.Z, focal, out float x, out float y, out float d) || d <= 0.2f) return;
        Color ac = Color.FromArgb(255, 108, 92, 255);
        g.DrawLine(Pen(ac, 1.6f), x - 7, y, x - 2, y); g.DrawLine(Pen(ac, 1.6f), x + 2, y, x + 7, y);
        g.DrawLine(Pen(ac, 1.6f), x, y - 7, x, y - 2); g.DrawLine(Pen(ac, 1.6f), x, y + 2, x, y + 7);
        g.DrawEllipse(Pen(Color.FromArgb(120, ac), 1), x - 11, y - 11, 22, 22);
    }

    void DrawFovCircle(Graphics g)
    {
        Color ac = Color.FromArgb(70, 108, 92, 255);
        g.DrawEllipse(Pen(ac, 1f), ov.Sw / 2f - Cfg.S.AimFov, ov.Sh / 2f - Cfg.S.AimFov, Cfg.S.AimFov * 2, Cfg.S.AimFov * 2);
    }

    void DrawLoot(Graphics g, Snap snap, Cam cam, double focal)
    {
        var c = Cfg.S;
        Color lc = Color.FromArgb(235, 245, 205, 90);
        Pen lp = Pen(Color.FromArgb(220, 245, 205, 90), 1.4f);
        foreach (LootItem item in snap.Loot)
        {
            if (!MathV.Finite(item.P)) continue;
            if (!Project(cam, item.P.X, item.P.Y, item.P.Z, focal, out float x, out float y, out float d) || d <= 0.3f) continue;
            float dist = MathV.Dist(item.P, new Vec { X = cam.X, Y = cam.Y, Z = cam.Z });
            if (dist > c.LootDist) continue;

            g.DrawLine(lp, x, y - 5, x + 5, y); g.DrawLine(lp, x + 5, y, x, y + 5);
            g.DrawLine(lp, x, y + 5, x - 5, y); g.DrawLine(lp, x - 5, y, x, y - 5);
            g.DrawString(item.Text + " " + (int)dist + "m", fTagS, Brush(lc), x + 8, y - 6);
        }
    }

    bool InScreen(float x, float y) => x > -300 && x < ov.Sw + 300 && y > -300 && y < ov.Sh + 300;

    void DrawStatus(Graphics g, int entCount)
    {
        var c = Cfg.S;
        string s;
        if (reader.Resolving) s = "RESOLVING  " + reader.Status + "  " + (int)(reader.ResolveProgress * 100) + "%";
        else if (!reader.AddressesValid) s = "OFFLINE  " + reader.Status;
        else s = "entities " + (entCount >= 0 ? entCount : frame.Ents.Length) + "   cam " + (int)frame.Cam.Fov + "\u00B0   " + reader.Status;
        if (c.Aim) s += "   aim: " + aim.AimStatus;
        if (c.Fps) s += "   " + ov.Fps.ToString("0") + " fps";

        Mark(ov.Sw - 330, ov.Sh - 26, 330, 26);
        g.DrawString(s, fTagS, Brush(Color.FromArgb(150, 120, 128, 145)), ov.Sw - 640, ov.Sh - 22);
    }

    void DrawBox(Graphics g, float x, float y, float w, float h, Color c, float lw, bool corners)
    {
        Pen p = Pen(c, lw);
        if (!corners) { g.DrawRectangle(p, x, y, w, h); return; }
        float a = Math.Min(13f, w * 0.35f);
        linePath.Reset();
        linePath.StartFigure(); linePath.AddLine(x, y + a, x, y); linePath.AddLine(x, y, x + a, y);
        linePath.StartFigure(); linePath.AddLine(x + w - a, y, x + w, y); linePath.AddLine(x + w, y, x + w, y + a);
        linePath.StartFigure(); linePath.AddLine(x, y + h - a, x, y + h); linePath.AddLine(x, y + h, x + a, y + h);
        linePath.StartFigure(); linePath.AddLine(x + w - a, y + h, x + w, y + h); linePath.AddLine(x + w, y + h, x + w, y + h - a);
        g.DrawPath(p, linePath);
    }

    public void Dispose()
    {
        foreach (var p in pens.Values) p.Dispose();
        foreach (var b in brushes.Values) b.Dispose();
        fTagS.Dispose(); sf.Dispose(); linePath.Dispose();
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
