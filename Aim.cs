using System;
using System.Diagnostics;
using System.Threading;

namespace NightRun;

sealed class AimBot
{
    readonly GameReader reader;
    readonly int sw, sh;
    readonly Thread thread;
    volatile bool stop;

    public volatile int LockedId = -1;
    public volatile string AimStatus = "off";
    DateTime lostSince = DateTime.MinValue;
    float accX, accY;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastT;

    static readonly int[] KeyVk = { 0x02, 0x05, 0x06, 0x12 };

    public AimBot(GameReader reader, int sw, int sh)
    {
        this.reader = reader; this.sw = sw; this.sh = sh;
        thread = new Thread(Loop) { IsBackground = true, Name = "aim", Priority = ThreadPriority.Normal };
    }

    public void Start() => thread.Start();
    public void Stop()
    {
        stop = true;
        if (thread.IsAlive && thread != Thread.CurrentThread) thread.Join();
    }
    internal static bool KeyDown() => Rw.Key(KeyVk[Math.Clamp(Cfg.S.AimKey, 0, KeyVk.Length - 1)]);
    internal static bool PassFilter(Ent e, Cfg.St c, int localId) =>
        e.Id != localId && (!e.Dead || c.ShowDead) && (!c.AimPlayersOnly || e.IsPlayer) &&
        (e.Role < 0 || e.Role >= Roles.Count || c.AimRoles[e.Role]);

    public static Vec BonePoint(Ent e, int bone)
    {
        bool fresh = e.SkelValid && e.SkelTicks != 0 && Stopwatch.GetElapsedTime(e.SkelTicks).TotalMilliseconds <= 350;
        Vec delta = MathV.Sub(new Vec { X = e.X, Y = e.Y, Z = e.Z }, e.SkelOrigin);
        if (fresh && MathV.Dist(delta, default) < 10)
        {
            Vec point = bone == 1 ? e.Neck : bone == 2 ? e.Chest : e.Head;
            if (MathV.Finite(point) && !point.Equals(default)) return MathV.Add(point, delta);
        }
        switch (bone)
        {
            case 1: return new Vec { X = e.X, Y = e.Y + 1.5f, Z = e.Z };
            case 2: return new Vec { X = e.X, Y = e.Y + 1.15f, Z = e.Z };
            default: return new Vec { X = e.X, Y = e.Y + 1.68f, Z = e.Z };
        }
    }

    static Vec Predicted(Ent e, int bone, float leadMs)
    {
        Vec p = BonePoint(e, bone);
        float age = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - e.PosTicks) / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0);
        float t = (Math.Clamp(age, 0, 100) + leadMs) / 1000f;
        return new Vec { X = p.X + e.Vx * t, Y = p.Y + e.Vy * t, Z = p.Z + e.Vz * t };
    }

    void Loop()
    {
        bool wasActive = false;
        while (!stop)
        {
            try
            {
                var c = Cfg.S;
                bool keyDown = c.Aim && KeyDown();
                bool active = c.Aim && keyDown && !MenuOpenFlag() && Rw.GameFocused(reader.GamePid);
                if (!active)
                {
                    AimStatus = !c.Aim ? "off" : !keyDown ? "key" : MenuOpenFlag() ? "menu" : "focus";
                    LockedId = -1;
                    accX = accY = 0; lostSince = DateTime.MinValue;
                    wasActive = false;
                    Thread.Sleep(c.Aim ? 8 : 100);
                    continue;
                }

                double t = clock.Elapsed.TotalSeconds;

                double dt = wasActive ? Math.Min(0.02, t - lastT) : 0.002;
                lastT = t;
                wasActive = true;

                Snap s = reader.Snap;
                if (!s.Cam.Valid || s.AgeMs > 150)
                {
                    AimStatus = !s.Cam.Valid ? "camera unavailable" : "camera stale";
                    LockedId = -1; accX = accY = 0; lostSince = DateTime.MinValue;
                    Thread.Sleep(8); continue;
                }
                double focal = (sh / 2.0) / Math.Tan(s.Cam.Fov * Math.PI / 360.0);

                Ent best = null; float bestDist = float.MaxValue;
                Ent cur = null; float curDist = float.MaxValue, curX = 0, curY = 0;
                int eligible = 0, passed = 0;

                foreach (Ent e in s.Ents)
                {
                    if (!e.Valid || !PassFilter(e, c, s.LocalId)) continue;
                    float d3 = MathV.Dist(new Vec { X = e.X, Y = e.Y, Z = e.Z }, new Vec { X = s.Cam.X, Y = s.Cam.Y, Z = s.Cam.Z });
                    if (c.MaxDist < 1000 && d3 > c.MaxDist) continue;
                    eligible++;
                    Vec bp = Predicted(e, c.AimBone, c.AimLead);
                    if (!Project(s.Cam, bp, focal, out float px, out float py, out float d) || d < 0.4f) continue;
                    float dx = px - sw / 2f, dy = py - sh / 2f;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    passed++;
                    if (e.Id == LockedId) { cur = e; curDist = dist; curX = px; curY = py; continue; }
                    if (dist < bestDist) { bestDist = dist; best = e; bestX = px; bestY = py; }
                }

                if (cur != null)
                {
                    lostSince = DateTime.MinValue;
                    if (curDist > Math.Max(c.StickyRadius, c.AimFov))
                    {
                        if (best == null || bestDist > c.AimFov)
                        {
                            LockedId = -1; accX = accY = 0;
                            AimStatus = "target outside radius";
                            Thread.Sleep(4); continue;
                        }
                        LockedId = best.Id;
                        curX = bestX; curY = bestY;
                        cur = best;
                    }
                    else
                    {
                        best = cur;

                        Vec bp = Predicted(cur, c.AimBone, c.AimLead);
                        if (Project(s.Cam, bp, focal, out float px, out float py, out float d) && d > 0.4f) { curX = px; curY = py; }
                    }
                    AimStatus = "lock " + Roles.Name(cur.Role);
                }
                else
                {
                    if (LockedId >= 0)
                    {
                        if (lostSince == DateTime.MinValue) lostSince = DateTime.UtcNow;
                        if ((DateTime.UtcNow - lostSince).TotalMilliseconds > c.RelockDelay)
                        {
                            LockedId = -1;
                            lostSince = DateTime.MinValue;
                        }
                        Thread.Sleep(4);
                        continue;
                    }

                    if (best != null && bestDist <= c.AimFov)
                    {
                        LockedId = best.Id;
                        curX = bestX; curY = bestY;
                        AimStatus = "lock " + Roles.Name(best.Role);
                    }
                    else
                    {
                        AimStatus = passed > 0 ? "no target (fov " + c.AimFov + "px)" : eligible > 0 ? "no target (projection)" : "no target (filter/range)";
                        Thread.Sleep(4);
                        continue;
                    }
                }

                if (LockedId >= 0)
                {

                    float vx = curX - sw / 2f, vy = curY - sh / 2f;
                    double k = 7.0 + c.AimSpeed * 0.9;
                    float stepX = (float)(vx * (1 - Math.Exp(-k * dt)));
                    float stepY = (float)(vy * (1 - Math.Exp(-k * dt)));
                    float len = MathF.Sqrt(stepX * stepX + stepY * stepY);
                    float maxLen = (0.8f + c.AimSpeed * 0.05f) * (float)(dt * 1000.0);
                    if (len > maxLen && len > 0) { stepX *= maxLen / len; stepY *= maxLen / len; }
                    accX += stepX; accY += stepY;
                    int mx = (int)accX, my = (int)accY;
                    if (mx != 0 || my != 0)
                    {
                        if (!Rw.Mouse(mx, my, out int error))
                        {
                            AimStatus = "input rejected (" + error + ")";
                            accX = accY = 0;
                            Thread.Sleep(50);
                            continue;
                        }
                        accX -= mx; accY -= my;
                    }
                }
            }
            catch (Exception ex)
            {
                AimStatus = "error: " + ex.GetType().Name;
                LockedId = -1; accX = accY = 0;
                GameReader.Log(ex);
                Thread.Sleep(100);
            }
            Thread.Sleep(4);
        }
    }

    float bestX, bestY;

    static bool MenuOpenFlag() => Program.MenuOpenFlag;

    bool Project(Cam c, Vec p, double focal, out float sx, out float sy, out float depth)
    {
        sx = sy = 0; depth = 0;
        float dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
        depth = dx * c.Fx + dy * c.Fy + dz * c.Fz;
        if (depth <= 0.25f) return false;
        float rr = dx * c.Rx + dy * c.Ry + dz * c.Rz;
        float uu = dx * c.Ux + dy * c.Uy + dz * c.Uz;
        sx = (float)(sw / 2.0 + rr / depth * focal);
        sy = (float)(sh / 2.0 - uu / depth * focal);
        return float.IsFinite(sx) && float.IsFinite(sy);
    }
}
