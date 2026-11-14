using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace NightRun;

struct Vec { public float X, Y, Z; }
struct Quat { public float X, Y, Z, W; }
struct Trs { public Vec P; public Quat Q; public Vec S; }
struct Seg { public Vec A, B; }

static class MathV
{
    public static bool Finite(Vec v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static Vec Add(Vec a, Vec b) => new Vec { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };
    public static Vec Mul(Vec a, float s) => new Vec { X = a.X * s, Y = a.Y * s, Z = a.Z * s };
    public static Vec Mul(Vec a, Vec b) => new Vec { X = a.X * b.X, Y = a.Y * b.Y, Z = a.Z * b.Z };
    public static Vec Cross(Vec a, Vec b) => new Vec { X = a.Y * b.Z - a.Z * b.Y, Y = a.Z * b.X - a.X * b.Z, Z = a.X * b.Y - a.Y * b.X };
    public static Vec Lerp(Vec a, Vec b, float t) => Add(a, Mul(Sub(b, a), t));
    public static Vec Sub(Vec a, Vec b) => new Vec { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
    public static float Dist(Vec a, Vec b) { Vec d = Sub(a, b); return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z); }
    public static Vec Rotate(Quat q, Vec v)
    {
        Vec t = Cross(new Vec { X = q.X, Y = q.Y, Z = q.Z }, v);
        t = new Vec { X = 2 * t.X, Y = 2 * t.Y, Z = 2 * t.Z };
        Vec c = Cross(new Vec { X = q.X, Y = q.Y, Z = q.Z }, t);
        return Add(v, Add(new Vec { X = q.W * t.X, Y = q.W * t.Y, Z = q.W * t.Z }, c));
    }
}

sealed class Ent
{
    public int Id;
    public ulong Obj, View, Transform;
    public float X, Y, Z;
    public float Vx, Vy, Vz;
    public long PosTicks;
    public bool Valid;
    public int Role = -1;
    public bool Dead;
    public bool IsPlayer;
    public float Health, MaxHealth;

    public bool SkelValid;
    public DateTime SkelTime;
    public long SkelTicks, PreviousSkelTicks;
    public Vec SkelOrigin, PreviousSkelOrigin;
    public Seg[] PreviousSegments = Array.Empty<Seg>();
    public Ent Copy() => (Ent)MemberwiseClone();
    public Vec Head, Neck, SpineTop, Pelvis, Chest;
    public Seg[] Segments = Array.Empty<Seg>();
}

sealed class LootItem { public Vec P; public string Text; }

struct Cam
{
    public bool Valid;
    public float X, Y, Z;
    public float Fx, Fy, Fz, Rx, Ry, Rz, Ux, Uy, Uz;
    public float Fov;
}

sealed class Snap
{
    public Cam Cam;
    public Ent[] Ents = Array.Empty<Ent>();
    public LootItem[] Loot = Array.Empty<LootItem>();
    public string LootStatus = "not scanned";
    public int LocalId = -1, LocalRole = -1;
    public long CamTicks;
    public double AgeMs => CamTicks == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(CamTicks).TotalMilliseconds;
}

sealed class GameReader
{
    public ulong Game = 0x79DF9A50, Updater = 0x28C11D300;
    public uint GamePid;
    public int LocalId = -1;
    public int LocalRole = -1;
    public volatile string Status = "init";
    public float ResolveProgress;
    public volatile bool Resolving;
    public volatile bool AddressesValid;
    Snap snapshot = new Snap();
    public Snap Snap => Volatile.Read(ref snapshot);
    Ent[] entities = Array.Empty<Ent>();
    LootItem[] loot = Array.Empty<LootItem>();
    string lootStatus = "not scanned";
    readonly ManualResetEventSlim stopped = new ManualResetEventSlim(false);
    readonly Thread thread;
    int slowIndex, stateIndex;
    DateTime nextState, nextLoot;
    readonly byte[] entryBytes = new byte[160 * 24];
    readonly byte[] viewBytes = new byte[160 * 24];
    const int MaxLootEntries = 512;
    readonly byte[] lootEntryBytes = new byte[MaxLootEntries * 24];
    double lastReadMs;
    public double ReadMs => Volatile.Read(ref lastReadMs);
    volatile bool stop;
    volatile bool requestResolve;
    DateTime lastCache = DateTime.MinValue;

    public void RequestResolve() { requestResolve = true; AddressesValid = false; }

    public GameReader(uint pid)
    {
        GamePid = pid;
        LoadAddresses();
        thread = new Thread(Loop) { IsBackground = true, Name = "reader", Priority = ThreadPriority.Normal };
    }

    public void Start() => thread.Start();
    public void Stop()
    {
        stop = true;
        stopped.Set();
        if (thread.IsAlive && thread != Thread.CurrentThread) thread.Join();
    }

    static string Ascii(ulong a, int max)
    {
        if (a < 0x10000 || !Rw.Read(a, (uint)max, out byte[] b)) return "";
        int n = Array.IndexOf(b, (byte)0); if (n < 0) n = b.Length;
        return Encoding.ASCII.GetString(b, 0, n);
    }
    static string Cn(ulong o)
    {
        ulong k = Rw.U64(o); if (k < 0x10000) return "";
        return Ascii(Rw.U64(k + 0x18), 64) + "." + Ascii(Rw.U64(k + 0x10), 64);
    }

    void LoadAddresses()
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addresses.json");
            if (!File.Exists(p)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("pid", out JsonElement pe))
                if ((uint)pe.GetInt32() != GamePid) return;
            if (doc.RootElement.TryGetProperty("game", out JsonElement g) && g.GetString() is string gs)
                Game = Convert.ToUInt64(gs.Substring(2), 16);
            if (doc.RootElement.TryGetProperty("updater", out JsonElement u) && u.GetString() is string us)
                Updater = Convert.ToUInt64(us.Substring(2), 16);
        }
        catch { }
    }

    void SaveAddresses()
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addresses.json");
            File.WriteAllText(p,
                "{\n  \"pid\": " + GamePid +
                ",\n  \"game\": \"0x" + Game.ToString("X") +
                "\",\n  \"updater\": \"0x" + Updater.ToString("X") + "\"\n}\n");
        }
        catch { }
    }

    bool ValidateAddresses()
    {
        try
        {
            if (Game < 0x10000 || Updater < 0x10000) return false;
            ulong start = Rw.U64(Game + 0x90);
            ulong model = Rw.U64(Updater + 0x18);
            return start >= 0x10000 && model >= 0x10000 &&
                   Cn(start).EndsWith("StartGameProcessor") &&
                   Cn(model).EndsWith("BattleEntitiesModel");
        }
        catch { return false; }
    }

    bool ResolveUpdaterFromGame()
    {
        try
        {
            ulong start = Rw.U64(Game + 0x90);
            if (start < 0x10000) return false;
            ulong screen = Rw.U64(start + 0x20);
            if (screen < 0x10000) return false;
            ulong bsu = Rw.U64(screen + 0x50);
            if (bsu < 0x10000) return false;
            ulong bu = Rw.U64(bsu + 0x18);
            if (bu < 0x10000) return false;
            ulong ent = Rw.U64(bu + 0x90);
            if (ent < 0x10000) return false;
            if (!Cn(ent).EndsWith("BattleEntitiesUpdater")) return false;
            Updater = ent;
            return true;
        }
        catch { return false; }
    }

    void RunResolver()
    {
        Resolving = true;
        Status = "resolving";
        try
        {
            ResolveProgress = 0;
            ulong game = Resolver.Resolve(GamePid, s => { Status = s; }, p => ResolveProgress = p, () => stop);
            if (game >= 0x10000)
            {
                Game = game;
                if (!ResolveUpdaterFromGame()) { Resolving = false; Status = "resolve fail (updater)"; return; }
                SaveAddresses();
                AddressesValid = true;
                Status = "ready";
            }
            else Status = "resolve fail";
        }
        catch (Exception ex) { Status = "resolve error"; Log(ex); }
        Resolving = false;
    }

    public static void Log(Exception ex)
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nightrun_errors.log");
            if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) return;
            File.AppendAllText(p, DateTime.Now + " " + ex + "\n");
        }
        catch { }
    }

    void Loop()
    {
        Span<byte> position = stackalloc byte[12];
        AddressesValid = ValidateAddresses() || ResolveUpdaterFromGame();
        DateTime nextRetry = DateTime.UtcNow.AddSeconds(30);
        if (!AddressesValid) RunResolver();
        while (!stop)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (requestResolve || (!AddressesValid && DateTime.UtcNow >= nextRetry))
                {
                    requestResolve = false;
                    if (!ValidateAddresses() && !ResolveUpdaterFromGame()) RunResolver();
                    else AddressesValid = true;
                    nextRetry = DateTime.UtcNow.AddSeconds(30);
                }
                bool wanted = Cfg.EspEnabled || Cfg.S.Loot || Cfg.S.Aim;
                if (!AddressesValid || !wanted || !Rw.GameFocused(GamePid))
                {
                    Volatile.Write(ref snapshot, new Snap { LocalId = LocalId, LocalRole = LocalRole });
                    stopped.Wait(100);
                    continue;
                }
                if ((DateTime.UtcNow - lastCache).TotalMilliseconds > 500) Cache();
                var c = Cfg.S;
                Cam cam = Snap.Cam;
                foreach (Ent e in entities)
                {
                    if (!Rw.ReadInto(e.Transform + 0x10, position)) { e.Valid = false; continue; }
                    float x = BitConverter.ToSingle(position), y = BitConverter.ToSingle(position[4..]), z = BitConverter.ToSingle(position[8..]);
                    e.Valid = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
                    if (!e.Valid) continue;
                    long now = Stopwatch.GetTimestamp();
                    double dt = (now - e.PosTicks) / (double)Stopwatch.Frequency;
                    if (dt > 0.001 && dt < 0.2)
                    {
                        float vx = (float)((x - e.X) / dt), vy = (float)((y - e.Y) / dt), vz = (float)((z - e.Z) / dt);
                        if (MathF.Abs(vx) < 90 && MathF.Abs(vy) < 90 && MathF.Abs(vz) < 90)
                        {
                            e.Vx += (vx - e.Vx) * 0.4f; e.Vy += (vy - e.Vy) * 0.4f; e.Vz += (vz - e.Vz) * 0.4f;
                        }
                        else { e.Vx = e.Vy = e.Vz = 0; e.SkelValid = false; }
                    }
                    else e.Vx = e.Vy = e.Vz = 0;
                    e.X = x; e.Y = y; e.Z = z; e.PosTicks = now;
                    SkelMiss(e);
                }
                if (entities.Length > 0)
                {
                    slowIndex %= entities.Length;
                    Ent e = entities[slowIndex];
                    bool visible = PassEsp(e, c, LocalId);
                    bool aimWanted = c.Aim && AimBot.KeyDown() && AimBot.PassFilter(e, c, LocalId);
                    float distance = MathV.Dist(new Vec { X = e.X, Y = e.Y, Z = e.Z }, new Vec { X = cam.X, Y = cam.Y, Z = cam.Z });
                    if (e.Valid && cam.Valid && distance <= Math.Min(c.MaxDist, 170) &&
                        ((visible && (c.Skeleton || c.Hitboxes)) || aimWanted) && e.View >= 0x10000 &&
                        (DateTime.UtcNow - lastSkeletonAttempt.GetValueOrDefault(e.Id)).TotalMilliseconds >= 50)
                    {
                        lastSkeletonAttempt[e.Id] = DateTime.UtcNow;
                        ReadSkeleton(e, cam);
                    }
                    if (DateTime.UtcNow >= nextState)
                    {
                        Ent state = entities[stateIndex++ % entities.Length];
                        state.Role = ReadRole(state.Obj); state.Dead = IsDead(state.Obj);
                        if (c.HealthBar && PassEsp(state, c, LocalId)) RefreshHealth(state);
                        if (state.Id == LocalId) LocalRole = state.Role;
                        nextState = DateTime.UtcNow.AddMilliseconds(Math.Max(8, 500 / entities.Length));
                    }
                    slowIndex++;
                }
                if (c.Loot && DateTime.UtcNow >= nextLoot)
                {
                    loot = BuildLoot(worldAddress, out lootStatus);
                    nextLoot = DateTime.UtcNow.AddSeconds(1);
                }
                if (!c.Loot)
                {
                    loot = Array.Empty<LootItem>(); lootStatus = "disabled"; nextLoot = DateTime.MinValue;
                }

                var copy = new Ent[entities.Length];
                for (int i = 0; i < copy.Length; i++) copy[i] = entities[i].Copy();
                cam = Camera();
                Volatile.Write(ref snapshot, new Snap { Cam = cam, CamTicks = Stopwatch.GetTimestamp(), Ents = copy, Loot = loot, LootStatus = lootStatus, LocalId = LocalId, LocalRole = LocalRole });
                Status = cam.Valid ? "ready" : "camera unavailable";
                Volatile.Write(ref lastReadMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                stopped.Wait(Math.Max(1, 8 - (int)ReadMs));
            }
            catch (Exception ex) { Log(ex); stopped.Wait(100); }
        }
    }

    internal static bool PassEsp(Ent e, Cfg.St c, int localId)
    {
        if (e.Id == localId && !c.ShowSelf) return false;
        if (e.Dead && !c.ShowDead) return false;
        return e.IsPlayer || (!c.PlayersOnly && c.ShowNpc);
    }

    readonly Dictionary<int, DateTime> lastSkeletonAttempt = new Dictionary<int, DateTime>();
    ulong worldAddress;

    void Cache()
    {
        if (!ResolveUpdaterFromGame())
        {
            entities = Array.Empty<Ent>(); cachedBrain = 0; worldAddress = 0;
            loot = Array.Empty<LootItem>(); lootStatus = "world unavailable"; nextLoot = DateTime.MinValue;
            lastCache = DateTime.UtcNow;
            return;
        }
        ulong model = Rw.U64(Updater + 0x18);
        LocalId = Rw.I32(Rw.U64(Rw.U64(model + 0xD0) + 0x20) + 0x38);
        ulong nextWorld = Rw.U64(model + 0x60);
        if (nextWorld != worldAddress) { nextLoot = DateTime.MinValue; loot = Array.Empty<LootItem>(); }
        worldAddress = nextWorld;
        var views = new Dictionary<int, ulong>();
        ulong dict = Rw.U64(Updater + 0x110);
        ulong entries = Rw.U64(dict + 0x18);
        int count = (int)Math.Min(Rw.U32(dict + 0x20), 160u);
        if (count > 0 && entries >= 0x10000 && Rw.ReadInto(entries + 0x20, viewBytes.AsSpan(0, count * 24)))
            for (int i = 0; i < count; i++)
            {
                int o = i * 24;
                if (BitConverter.ToInt32(viewBytes, o) < 0) continue;
                ulong view = BitConverter.ToUInt64(viewBytes, o + 16);
                if (view >= 0x10000) views[BitConverter.ToInt32(viewBytes, o + 8)] = view;
            }
        dict = Rw.U64(worldAddress + 0x18);
        entries = Rw.U64(dict + 0x18);
        count = (int)Math.Min(Rw.U32(dict + 0x20), 160u);
        var oldMap = new Dictionary<int, Ent>();
        foreach (var old in entities) oldMap[old.Id] = old;
        var list = new List<Ent>();
        if (count > 0 && entries >= 0x10000 && Rw.ReadInto(entries + 0x20, entryBytes.AsSpan(0, count * 24)))
            for (int i = 0; i < count; i++)
            {
                int o = i * 24;
                if (BitConverter.ToInt32(entryBytes, o) < 0) continue;
                int id = BitConverter.ToInt32(entryBytes, o + 8);
                if (views.Count > 0 && !views.ContainsKey(id)) continue;
                ulong obj = BitConverter.ToUInt64(entryBytes, o + 16);
                if (obj < 0x10000) continue;
                views.TryGetValue(id, out ulong view);
                bool existing = oldMap.TryGetValue(id, out Ent e) && e.Obj == obj && e.View == view;
                if (!existing)
                {
                    e = new Ent { Id = id, Obj = obj, View = view, Transform = Rw.U64(obj + 0x58) };
                    e.Role = ReadRole(obj); e.Dead = IsDead(obj);
                    e.IsPlayer = Rw.U64(obj + 0x258) >= 0x10000;
                    if (Cfg.S.HealthBar) RefreshHealth(e);
                    lastSkeletonAttempt.Remove(id);
                }
                if (e.Transform >= 0x10000) list.Add(e);
                if (id == LocalId) LocalRole = e.Role;
            }
        if (!Cfg.EspEnabled && !Cfg.S.Aim) entities = Array.Empty<Ent>();
        else entities = list.ToArray();
        foreach (int id in lastSkeletonAttempt.Keys.Where(id => !list.Any(e => e.Id == id)).ToArray()) lastSkeletonAttempt.Remove(id);
        lastCache = DateTime.UtcNow;
    }

    static int ReadRole(ulong obj)
    {

        ulong p = Rw.U64(obj + 0x208);
        if (p < 0x10000) return -1;
        ulong v = Rw.U64(p + 0x10);
        if (v < 0x10000) return -1;
        ulong desc = Rw.U64(v + 0x18);
        if (desc < 0x10000) return -1;
        int r = Rw.I32(desc + 0x18);
        return (r >= -1 && r < Roles.Count) ? r : -1;
    }

    static bool IsDead(ulong obj)
    {
        ulong p = Rw.U64(obj + 0xF0);
        if (p < 0x10000) return false;
        ulong v = Rw.U64(p + 0x10);
        return v >= 0x10000 && Rw.I32(v + 0x18) != 0;
    }

    static void RefreshHealth(Ent e)
    {
        ulong priv = Rw.U64(e.Obj + 0x10);
        if (priv < 0x10000) return;
        ulong hm = Rw.U64(priv + 0x1B0);
        if (hm < 0x10000) return;
        ulong hp = Rw.U64(hm + 0x10);
        if (hp >= 0x10000) e.Health = Rw.I32(hp + 0x18);
        e.MaxHealth = Rw.I32(hm + 0x20);
        if (e.MaxHealth <= 0) e.MaxHealth = 100;
    }

    LootItem[] BuildLoot(ulong world, out string status)
    {
        var list = new List<LootItem>();
        if (world < 0x10000) { status = "world unavailable"; return Array.Empty<LootItem>(); }

        string AddDict(ulong dictOffset, ulong trOff, bool cargo)
        {
            string name = cargo ? "cargo" : "loot";
            if (!Rw.U64(world + dictOffset, out ulong d) || d < 0x10000) return name + ": no dictionary";
            if (!Rw.U32(d + 0x20, out uint count)) return name + ": count read failed";
            if (count == 0) return name + ": empty";
            if (!Rw.U64(d + 0x18, out ulong entries) || entries < 0x10000) return name + ": no entries";
            int scanCount = (int)Math.Min(count, (uint)MaxLootEntries);
            bool batchRead = Rw.ReadInto(entries + 0x20, lootEntryBytes.AsSpan(0, scanCount * 24));
            int examined = 0, invalid = 0, found = 0, readFailed = 0;
            Span<byte> position = stackalloc byte[12];
            for (int i = 0; i < scanCount; i++)
            {
                int offset = i * 24;
                if (!batchRead && !Rw.ReadInto(entries + 0x20 + (ulong)offset, lootEntryBytes.AsSpan(offset, 24)))
                { readFailed++; continue; }
                if (BitConverter.ToInt32(lootEntryBytes, offset) < 0) continue;
                examined++;
                ulong obj = BitConverter.ToUInt64(lootEntryBytes, offset + 16);
                if (obj < 0x10000 || !Rw.U64(obj + trOff, out ulong tr) || tr < 0x10000 ||
                    !Rw.ReadInto(tr + 0x10, position)) { invalid++; continue; }
                float x = BitConverter.ToSingle(position), y = BitConverter.ToSingle(position[4..]), z = BitConverter.ToSingle(position[8..]);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { invalid++; continue; }
                int id = BitConverter.ToInt32(lootEntryBytes, offset + 8);
                int price = 0;
                if (!cargo) { ulong bag = Rw.U64(obj + 0x30); ulong data = bag >= 0x10000 ? Rw.U64(bag + 0x10) : 0; if (data >= 0x10000) price = Rw.I32(data + 0x24); }
                else { ulong pts = Rw.U64(obj + 0x58); if (pts >= 0x10000) price = Rw.I32(pts + 0x18); }
                list.Add(new LootItem { P = new Vec { X = x, Y = y, Z = z }, Text = (cargo ? "C " : "$") + price + " #" + id });
                found++;
            }
            return $"{name}: {found}/{examined}, bad={invalid}, readerr={readFailed}, slots={count}" + (count > MaxLootEntries ? $" (first {MaxLootEntries})" : "");
        }
        string lootInfo = AddDict(0x38, 0x20, false);
        string cargoInfo = AddDict(0xC8, 0x30, true);
        status = lootInfo + "; " + cargoInfo;
        return list.ToArray();
    }

    readonly byte[] cameraBytes = new byte[0x68];
    ulong cachedBrain;
    DateTime brainCheck;

    Cam Camera()
    {
        Cam c = new Cam { Fov = 60 };
        try
        {

            if (cachedBrain < 0x10000 || (DateTime.UtcNow - brainCheck).TotalSeconds > 1)
            {
                brainCheck = DateTime.UtcNow;
                ulong start = Rw.U64(Game + 0x90);
                ulong screen = Rw.U64(start + 0x20);
                ulong bsu = Rw.U64(screen + 0x50);
                ulong bu = Rw.U64(bsu + 0x18);
                ulong bcu = Rw.U64(bu + 0x128);
                ulong inner = Rw.U64(bcu + 0x10);
                ulong brain = 0;
                if (Cn(inner).EndsWith("CompositeUpdater"))
                {
                    ulong list = Rw.U64(inner + 0x10);
                    ulong arr = Rw.U64(list + 0x10);
                    uint n = Rw.U32(list + 0x18);
                    for (uint i = 0; i < n && i < 32; i++)
                    {
                        ulong item = Rw.U64(arr + 0x20 + i * 8);
                        if (Cn(item).EndsWith("CameraBrainUpdater")) { brain = Rw.U64(item + 0x10); break; }
                    }
                }
                cachedBrain = brain;
            }
            ulong brainPtr = cachedBrain;
            if (brainPtr >= 0x10000)
            {

                byte[] b = cameraBytes;
                if (Rw.ReadInto(brainPtr + 0xB0, b))
                {
                    float f = BitConverter.ToSingle(b, 0);
                    if (f > 1 && f < 179) c.Fov = f;
                    c.X = BitConverter.ToSingle(b, 0x4C); c.Y = BitConverter.ToSingle(b, 0x50); c.Z = BitConverter.ToSingle(b, 0x54);
                    float qx = BitConverter.ToSingle(b, 0x58), qy = BitConverter.ToSingle(b, 0x5C), qz = BitConverter.ToSingle(b, 0x60), qw = BitConverter.ToSingle(b, 0x64);
                    float n = qx * qx + qy * qy + qz * qz + qw * qw;
                    if (n > 0.5f && n < 1.5f)
                    {
                        c.Fx = 2 * (qx * qz + qw * qy); c.Fy = 2 * (qy * qz - qw * qx); c.Fz = 1 - 2 * (qx * qx + qy * qy);
                        c.Rx = 1 - 2 * (qy * qy + qz * qz); c.Ry = 2 * (qx * qy + qw * qz); c.Rz = 2 * (qx * qz - qw * qy);
                        c.Ux = 2 * (qx * qy - qw * qz); c.Uy = 1 - 2 * (qx * qx + qz * qz); c.Uz = 2 * (qy * qz + qw * qx);
                        c.Valid = true;
                    }
                }
            }
            if (!c.Valid)
            {
                ulong gm = Rw.U64(Game + 0xD0);
                ulong bm = Rw.U64(gm + 0x10);
                ulong cm = Rw.U64(bm + 0x358);
                byte[] b = cameraBytes;
                if (cm >= 0x10000 && Rw.ReadInto(cm + 0x10, b.AsSpan(0, 0x50)))
                {
                    c.X = BitConverter.ToSingle(b, 0x00); c.Y = BitConverter.ToSingle(b, 0x04); c.Z = BitConverter.ToSingle(b, 0x08);
                    c.Fx = BitConverter.ToSingle(b, 0x2C); c.Fy = BitConverter.ToSingle(b, 0x30); c.Fz = BitConverter.ToSingle(b, 0x34);
                    c.Rx = BitConverter.ToSingle(b, 0x38); c.Ry = BitConverter.ToSingle(b, 0x3C); c.Rz = BitConverter.ToSingle(b, 0x40);
                    c.Ux = BitConverter.ToSingle(b, 0x44); c.Uy = BitConverter.ToSingle(b, 0x48); c.Uz = BitConverter.ToSingle(b, 0x4C);
                    c.Valid = true;
                }
            }
        }
        catch { }
        c.Valid = c.Valid && MathV.Finite(new Vec { X = c.X, Y = c.Y, Z = c.Z }) &&
            MathV.Finite(new Vec { X = c.Fx, Y = c.Fy, Z = c.Fz }) &&
            MathV.Finite(new Vec { X = c.Rx, Y = c.Ry, Z = c.Rz }) &&
            MathV.Finite(new Vec { X = c.Ux, Y = c.Uy, Z = c.Uz });
        return c;
    }

    readonly byte[] matrixBytes = new byte[512 * 48], parentBytes = new byte[512 * 4], pointerBytes = new byte[512 * 8];
    readonly Trs[] transforms = new Trs[512];
    readonly int[] parentIndices = new int[512];
    readonly Dictionary<ulong, int> boneIndices = new Dictionary<ulong, int>();
    readonly List<Seg> segments = new List<Seg>(128);
    readonly List<Vec> points = new List<Vec>(32);
    static readonly ulong[] limbOffsets = { 0x58, 0x60, 0x68, 0x70 };

    internal static bool TryWorld(Trs[] trs, int[] parents, int count, int index, out Vec value)
    {
        value = default;
        if (index < 0 || index >= count) return false;
        value = trs[index].P;
        int steps = 0;
        for (int p = parents[index]; p != -1; p = parents[p])
        {
            if (p < 0 || p >= count || ++steps >= count) return false;
            value = MathV.Add(trs[p].P, MathV.Rotate(trs[p].Q, MathV.Mul(trs[p].S, value)));
        }
        return MathV.Finite(value);
    }

    void ReadSkeleton(Ent e, Cam cam)
    {
        try
        {
            ulong view = e.View;
            ulong movement = Rw.U64(view + 0x20);
            ulong managed = Rw.U64(movement + 0x20);
            ulong native = Rw.U64(managed + 0x10);
            ulong h = Rw.U64(native + 0x28);
            if (h < 0x10000) { SkelMiss(e); return; }
            int count = Rw.I32(h + 0x38);
            if (count < 2 || count > 512) { SkelMiss(e); return; }
            ulong matrices = Rw.U64(h + 0x18), parents = Rw.U64(h + 0x20), pointers = Rw.U64(h + 0x30);
            if (matrices < 0x10000 || parents < 0x10000 || pointers < 0x10000) { SkelMiss(e); return; }
            byte[] mb = matrixBytes, pb = parentBytes, tb = pointerBytes;
            if (!Rw.ReadInto(matrices, mb.AsSpan(0, count * 48)) ||
                !Rw.ReadInto(parents, pb.AsSpan(0, count * 4)) ||
                !Rw.ReadInto(pointers, tb.AsSpan(0, count * 8))) { SkelMiss(e); return; }

            Trs[] trs = transforms;
            int[] par = parentIndices;
            boneIndices.Clear();
            for (int i = 0; i < count; i++)
            {
                int o = i * 48;
                trs[i] = new Trs
                {
                    P = new Vec { X = BitConverter.ToSingle(mb, o), Y = BitConverter.ToSingle(mb, o + 4), Z = BitConverter.ToSingle(mb, o + 8) },
                    Q = new Quat { X = BitConverter.ToSingle(mb, o + 16), Y = BitConverter.ToSingle(mb, o + 20), Z = BitConverter.ToSingle(mb, o + 24), W = BitConverter.ToSingle(mb, o + 28) },
                    S = new Vec { X = BitConverter.ToSingle(mb, o + 32), Y = BitConverter.ToSingle(mb, o + 36), Z = BitConverter.ToSingle(mb, o + 40) },
                };
                par[i] = BitConverter.ToInt32(pb, i * 4);
                boneIndices[BitConverter.ToUInt64(tb, i * 8)] = i;
            }
            bool TryBone(ulong managedTransform, out Vec v)
            {
                v = default;
                if (managedTransform < 0x10000) return false;
                ulong n = Rw.U64(managedTransform + 0x10);
                return n >= 0x10000 && boneIndices.TryGetValue(n, out int index) && TryWorld(trs, par, count, index, out v);
            }

            List<Seg> segs = segments;
            segs.Clear();
            Vec head = default, neck = default, spine = default, pelvis = default;
            bool haveHead = false, haveNeck = false;

            ulong anim = Rw.U64(view + 0x28);
            ulong headView = anim >= 0x10000 ? Rw.U64(anim + 0x28) : 0;
            if (headView >= 0x10000)
            {
                haveNeck = TryBone(Rw.U64(headView + 0x20), out neck);
                haveHead = TryBone(Rw.U64(headView + 0x28), out head);
                bool haveSpine = TryBone(Rw.U64(headView + 0x30), out spine);
                if (haveNeck && haveHead) segs.Add(new Seg { A = neck, B = head });
                if (haveNeck && haveSpine) segs.Add(new Seg { A = spine, B = neck });
                if (haveSpine) pelvis = spine;
            }
            ulong torso = anim >= 0x10000 ? Rw.U64(anim + 0x30) : 0;
            if (torso >= 0x10000)
            {
                bool a = TryBone(Rw.U64(torso + 0x28), out Vec s2);
                bool b2 = TryBone(Rw.U64(torso + 0x30), out Vec s3);
                if (a && b2) segs.Add(new Seg { A = s2, B = s3 });
            }
            ulong weapon = Rw.U64(view + 0x30);
            if (weapon >= 0x10000)
            {
                foreach (ulong off in limbOffsets)
                {
                    ulong limb = Rw.U64(weapon + off);
                    if (limb < 0x10000) continue;
                    ulong arr = Rw.U64(limb + 0x40);
                    if (arr < 0x10000) continue;
                    int n = Rw.I32(arr + 0x18);
                    if (n < 2 || n > 32) continue;
                    if (!Rw.Read(arr + 0x20, (uint)(n * 8), out byte[] bones) || bones.Length != n * 8) continue;
                    List<Vec> pts = points;
                    pts.Clear();
                    for (int i = 0; i < n; i++)
                        if (TryBone(BitConverter.ToUInt64(bones, i * 8), out Vec v)) pts.Add(v);
                    for (int i = 1; i < pts.Count; i++) segs.Add(new Seg { A = pts[i - 1], B = pts[i] });
                    if (pts.Count >= 2 && pelvis.Equals(default)) pelvis = pts[0];
                }
            }

            bool ok = (haveHead || haveNeck) && segs.Count > 0;

            if (ok && (float.Abs(head.X) > 100000 || float.Abs(neck.X) > 100000)) ok = false;
            if (ok)
            {
                e.PreviousSegments = e.Segments;
                e.PreviousSkelTicks = e.SkelTicks; e.PreviousSkelOrigin = e.SkelOrigin;
                e.SkelOrigin = new Vec { X = e.X, Y = e.Y, Z = e.Z };
                e.SkelTicks = Stopwatch.GetTimestamp();
                e.Head = haveHead ? head : neck;
                if (haveNeck) e.Neck = neck;
                if (!spine.Equals(default)) e.SpineTop = spine;
                e.Pelvis = pelvis;
                e.Chest = haveNeck && !spine.Equals(default) ? MathV.Lerp(neck, spine, 0.4f) : e.Neck;
                e.Segments = segs.ToArray();
                e.SkelValid = true;
                e.SkelTime = DateTime.UtcNow;
            }
            else SkelMiss(e);
        }
        catch { SkelMiss(e); }
    }

    static void SkelMiss(Ent e)
    {
            if (e.SkelTicks == 0 || Stopwatch.GetElapsedTime(e.SkelTicks).TotalMilliseconds > 350) e.SkelValid = false;
    }
}
