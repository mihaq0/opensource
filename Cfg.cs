using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NightRun;

static class Roles
{

    public const int None = -1, Settler = 0, Citizen = 1, Robber = 2, Cop = 3, Clan = 4, Prisoner = 5, Hubber = 6;
    public static readonly string[] Names = { "SETTLER", "CITIZEN", "ROBBER", "COP", "CLAN", "PRISONER", "HUBBER" };
    public const int Count = 7;

    public static readonly Color[] RoleColors = {
        Color.FromArgb(120, 128, 145),
        Color.FromArgb(140, 148, 165),
        Color.FromArgb(90, 150, 255),
        Color.FromArgb(110, 225, 120),
        Color.FromArgb(100, 140, 190),
        Color.FromArgb(165, 150, 120),
        Color.FromArgb(110, 118, 135) };
    public static string Name(int r) => (r >= 0 && r < Count) ? Names[r] : "UNKNOWN";

    public static Color RoleColor(int r)
    {
        var c = Cfg.S != null ? Cfg.S.RoleColors : null;
        if (c != null && r >= 0 && r < c.Length) return c[r];
        return (r >= 0 && r < Count) ? RoleColors[r] : Color.FromArgb(150, 156, 170);
    }

    public static bool IsEnemy(int local, int target)
    {
        if (local == Robber) return target == Cop;
        if (local == Cop) return target == Robber;
        return false;
    }
}

class Cfg
{
    public static St S = new St();

    public class St
    {

        public int BoxMode = 1;
        public float BoxWidth = 1.8f;
        public bool BoxGlow = true;
        public bool Skeleton = true;
        public float SkeletonWidth = 1.4f;
        public bool Joints = false;
        public bool Hitboxes = true;
        public bool HealthBar = true;
        public int TagMode = 1;
        public int Snapline = 0;
        public int MaxDist = 400;
        public bool ShowDead = false;
        public bool ShowSelf = false;
        public bool ShowNpc = true;
        public bool PlayersOnly = false;

        public bool Aim = false;
        public int AimKey = 0;
        public int AimBone = 0;
        public int AimFov = 260;
        public int StickyRadius = 420;
        public int AimSpeed = 30;
        public bool AimPlayersOnly = false;
        public bool[] AimRoles = { true, true, true, true, true, true, true };
        public int RelockDelay = 500;
        public int AimLead = 40;

        public int ColorScheme = 0;

        public Color[] RoleColors = {
            Color.FromArgb(120, 128, 145),
            Color.FromArgb(140, 148, 165),
            Color.FromArgb(90, 150, 255),
            Color.FromArgb(110, 225, 120),
            Color.FromArgb(100, 140, 190),
            Color.FromArgb(165, 150, 120),
            Color.FromArgb(110, 118, 135) };
        public int BoxAlpha = 235;
        public int SkeletonAlpha = 190;

        public bool Loot = false;
        public int LootDist = 400;

        // Новые визуальные флаги (можно выключить через конфиг, чтобы вернуться к классическому виду)
        public bool NewGui = true;
        public bool EnhancedEsp = true;

        public bool Fps = true;
        public float MenuX = 40, MenuY = 40;

        public Color BoxColor(int role) => ColorScheme switch
        {
            1 => Color.FromArgb(80, 220, 255),
            2 => Color.FromArgb(235, 240, 250),
            3 => Color.FromArgb(110, 230, 140),
            4 => Color.FromArgb(255, 95, 95),
            _ => Roles.RoleColor(role),
        };
    }

    static string Path() => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nightrun.json");

    internal static readonly JsonSerializerOptions J = new JsonSerializerOptions
    {
        WriteIndented = true, IncludeFields = true, Converters = { new ColorConverter() }
    };

    sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return Color.FromArgb(reader.GetInt32());
            using var doc = JsonDocument.ParseValue(ref reader);
            var v = doc.RootElement;
            if (v.TryGetProperty("R", out var r) && v.TryGetProperty("G", out var g) && v.TryGetProperty("B", out var b))
                return Color.FromArgb(v.TryGetProperty("A", out var a) ? a.GetByte() : (byte)255, r.GetByte(), g.GetByte(), b.GetByte());
            throw new JsonException("Invalid color");
        }
        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) => writer.WriteNumberValue(value.ToArgb());
    }

    public static bool EspEnabled => S.BoxMode > 0 || S.Skeleton || S.Hitboxes || S.HealthBar || S.TagMode > 0 || S.Snapline > 0;
    public static void Panic()
    {
        S.BoxMode = 0; S.Skeleton = false; S.Hitboxes = false; S.HealthBar = false;
        S.TagMode = 0; S.Snapline = 0; S.Loot = false; S.Aim = false;
        Touch();
    }

    internal static void Validate(St s)
    {
        s.BoxMode = Math.Clamp(s.BoxMode, 0, 3); s.TagMode = Math.Clamp(s.TagMode, 0, 2);
        s.Snapline = Math.Clamp(s.Snapline, 0, 2); s.ColorScheme = Math.Clamp(s.ColorScheme, 0, 4);
        s.BoxWidth = float.IsFinite(s.BoxWidth) ? Math.Clamp(s.BoxWidth, 0.6f, 4f) : 1.8f;
        s.SkeletonWidth = float.IsFinite(s.SkeletonWidth) ? Math.Clamp(s.SkeletonWidth, 0.6f, 4f) : 1.4f;
        s.BoxAlpha = Math.Clamp(s.BoxAlpha, 40, 255); s.SkeletonAlpha = Math.Clamp(s.SkeletonAlpha, 40, 255);
        s.MaxDist = Math.Clamp(s.MaxDist, 50, 1000); s.LootDist = Math.Clamp(s.LootDist, 20, 500);
        s.AimKey = Math.Clamp(s.AimKey, 0, 3); s.AimBone = Math.Clamp(s.AimBone, 0, 2);
        s.AimFov = Math.Clamp(s.AimFov, 20, 900); s.StickyRadius = Math.Clamp(s.StickyRadius, 0, 900);
        s.AimSpeed = Math.Clamp(s.AimSpeed, 5, 100); s.RelockDelay = Math.Clamp(s.RelockDelay, 0, 2000);
        s.AimLead = Math.Clamp(s.AimLead, 0, 300);
        if (!float.IsFinite(s.MenuX)) s.MenuX = 40;
        if (!float.IsFinite(s.MenuY)) s.MenuY = 40;
        var defaults = new St();
        var colors = (Color[])defaults.RoleColors.Clone();
        if (s.RoleColors != null) Array.Copy(s.RoleColors, colors, Math.Min(s.RoleColors.Length, colors.Length));
        for (int i = 0; i < colors.Length; i++) if (colors[i].IsEmpty) colors[i] = defaults.RoleColors[i];
        s.RoleColors = colors;
        var roles = defaults.AimRoles;
        if (s.AimRoles != null) Array.Copy(s.AimRoles, roles, Math.Min(s.AimRoles.Length, roles.Length));
        s.AimRoles = roles;
    }

    public static void Load()
    {
        try
        {
            string p = Path();
            if (!File.Exists(p)) return;
            var loaded = JsonSerializer.Deserialize<St>(File.ReadAllText(p), J) ?? new St();
            Validate(loaded);
            S = loaded;
        }
        catch (Exception ex) { GameReader.Log(ex); }
    }

    static readonly object saveLock = new object();
    static int savedVersion;
    public static int Version;
    public static void Touch() => Interlocked.Increment(ref Version);
    public static void Save()
    {
        lock (saveLock)
        {
            int version = Volatile.Read(ref Version);
            try
            {
                string p = Path(), temp = p + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(S, J));
                File.Move(temp, p, true);
                savedVersion = version;
            }
            catch (Exception ex) { GameReader.Log(ex); }
        }
    }
    public static void SaveIfDirty() { if (Volatile.Read(ref Version) != Volatile.Read(ref savedVersion)) Save(); }
}
