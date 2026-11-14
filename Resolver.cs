using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NightRun;

static class Resolver
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000, MEM_PRIVATE = 0x20000;
    const uint PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
    const int MAX_READ = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public ulong BaseAddress; public ulong AllocationBase; public uint AllocationProtect; public uint Alignment1; public ulong RegionSize; public uint State; public uint Protect; public uint Type; public uint Alignment2; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MBI info, UIntPtr length);

    static readonly AsyncLocal<Func<bool>> cancelled = new AsyncLocal<Func<bool>>();
    static void CheckCancelled() { if (cancelled.Value?.Invoke() == true) throw new OperationCanceledException(); }

    static bool Read(ulong a, uint n, out byte[] d) => Rw.Read(a, n, out d);
    static ulong U64(ulong a) => Read(a, 8, out byte[] b) && b.Length == 8 ? BitConverter.ToUInt64(b, 0) : 0;
    static string Ascii(ulong a, int max)
    {
        if (a < 0x10000 || !Read(a, (uint)max, out byte[] b)) return "";
        int n = Array.IndexOf(b, (byte)0); if (n < 0) n = b.Length;
        return Encoding.ASCII.GetString(b, 0, n);
    }
    static string ClassName(ulong obj, out ulong klass)
    {
        klass = U64(obj);
        if (klass < 0x10000) return "";
        string name = Ascii(U64(klass + 0x10), 96);
        string ns = Ascii(U64(klass + 0x18), 96);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    static List<MBI> Regions(IntPtr proc, ulong maxBytes)
    {
        List<MBI> list = new List<MBI>();
        ulong address = 0; int guard = 0;
        while (guard++ < 200000)
        {
            UIntPtr n = VirtualQueryEx(proc, (IntPtr)(long)address, out MBI info, (UIntPtr)Marshal.SizeOf<MBI>());
            if (n == UIntPtr.Zero) break;
            if (info.State == MEM_COMMIT && info.RegionSize > 0 && (info.Protect & PAGE_NOACCESS) == 0 && (info.Protect & PAGE_GUARD) == 0 && info.RegionSize <= maxBytes)
                list.Add(info);
            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= address) break;
            address = next;
        }
        return list;
    }

    static void ForEachChunk(uint pid, Action<ulong, byte[]> cb, uint typeMask, Action<float> progress)
    {
        IntPtr proc = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) throw new InvalidOperationException("Cannot query target memory regions");
        List<MBI> regions;
        try { regions = Regions(proc, ulong.MaxValue); }
        finally { CloseHandle(proc); }

        ulong total = 0; foreach (MBI r in regions) if (typeMask == 0 || (r.Type & typeMask) != 0) total += r.RegionSize;
        ulong done = 0;
        foreach (MBI region in regions)
        {
            if (typeMask != 0 && (region.Type & typeMask) == 0) continue;
            ulong start = region.BaseAddress, end = region.BaseAddress + region.RegionSize;
            for (ulong a = start; a < end;)
            {
                CheckCancelled();
                uint size = (uint)Math.Min((ulong)MAX_READ, end - a);
                if (Read(a, size, out byte[] data) && data.Length > 0) cb(a, data);
                a += size;
                done += size;
                if ((done & 0xFFFFFFF) == 0) progress(Math.Min(0.99f, (float)done / Math.Max(1, total)));
            }
        }
    }

    static List<ulong> FindString(uint pid, string text, uint mask, Action<float> progress)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text + "\0");
        List<ulong> hits = new List<ulong>();
        ForEachChunk(pid, (address, data) =>
        {
            int start = 0;
            while (true)
            {
                int i = Array.IndexOf(data, needle[0], start);
                if (i < 0 || i + needle.Length > data.Length) break;
                bool match = true;
                for (int j = 1; j < needle.Length; j++) if (data[i + j] != needle[j]) { match = false; break; }
                if (match) hits.Add(address + (ulong)i);
                start = i + 1;
            }
        }, mask, progress);
        return hits;
    }

    static void FindRefs(uint pid, HashSet<ulong> targets, List<ulong> into, uint mask, Action<float> progress)
    {
        ForEachChunk(pid, (address, data) =>
        {
            if (into.Count >= 4096) return;
            for (int i = 0; i + 8 <= data.Length; i += 8)
            {
                ulong v = BitConverter.ToUInt64(data, i);
                if (targets.Contains(v)) { into.Add(address + (ulong)i); if (into.Count >= 4096) return; }
            }
        }, mask, progress);
    }

    public static ulong Resolve(uint pid, Action<string> status, Action<float> progress, Func<bool> stop)
    {
        cancelled.Value = stop;
        void Log(string s) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nightrun_resolve.log"), DateTime.Now.ToString("HH:mm:ss") + " " + s + "\n"); } catch { } }
        status("scan UnityEntry");
        List<ulong> strings = FindString(pid, "UnityEntry", MEM_MAPPED | MEM_IMAGE, progress);
        Log("UnityEntry strings=" + strings.Count);
        if (strings.Count == 0) { strings = FindString(pid, "UnityEntry", 0, progress); Log("fullscan strings=" + strings.Count); }
        if (strings.Count == 0) { status("no UnityEntry"); return 0; }

        status("scan refs");
        List<ulong> refs = new List<ulong>();
        FindRefs(pid, new HashSet<ulong>(strings), refs, MEM_PRIVATE, progress);
        Log("private refs=" + refs.Count);
        if (refs.Count == 0) { FindRefs(pid, new HashSet<ulong>(strings), refs, MEM_MAPPED | MEM_IMAGE, progress); Log("image refs=" + refs.Count); }
        if (refs.Count == 0) { status("no refs"); return 0; }

        status("candidates");
        List<(ulong klass, int off)> candidates = new List<(ulong, int)>();
        HashSet<ulong> stringSet = new HashSet<ulong>(strings);
        foreach (ulong r in refs)
        {
            foreach (int off in new[] { 0x8, 0x10, 0x18, 0x20 })
            {
                if (r < (ulong)off) continue;
                ulong cand = r - (ulong)off;
                string ns = Ascii(U64(cand + (ulong)off + 8), 64);
                if (ns != "Start") continue;
                if (!candidates.Exists(c => c.klass == cand && c.off == off)) candidates.Add((cand, off));
            }
        }
        Log("candidates=" + candidates.Count);
        if (candidates.Count == 0) { status("no candidates"); return 0; }

        status("count refs");
        Dictionary<ulong, int> counts = new Dictionary<ulong, int>();
        Dictionary<ulong, List<ulong>> refLists = new Dictionary<ulong, List<ulong>>();
        foreach (var c in candidates) { counts[c.klass] = 0; refLists[c.klass] = new List<ulong>(); }
        ForEachChunk(pid, (address, data) =>
        {
            for (int i = 0; i + 8 <= data.Length; i += 8)
            {
                ulong v = BitConverter.ToUInt64(data, i);
                if (counts.ContainsKey(v)) { counts[v]++; if (refLists[v].Count < 256) refLists[v].Add(address + (ulong)i); }
            }
        }, MEM_PRIVATE, progress);
        foreach (var c in candidates) Log("  cand 0x" + c.klass.ToString("X") + " off=0x" + c.off.ToString("X") + " refs=" + counts[c.klass]);

        ulong best = 0; int bestCount = -1;
        foreach (var c in candidates)
            if (counts[c.klass] > bestCount) { bestCount = counts[c.klass]; best = c.klass; }
        if (best == 0 || bestCount <= 0) { status("no klass refs"); return 0; }
        Log("chosen=0x" + best.ToString("X") + " refs=" + bestCount);

        status("find Game");
        foreach (ulong r in refLists[best])
        {
            if (ClassName(r, out _) != "Start.UnityEntry") continue;
            ulong g = U64(r + 0x28);
            if (g > 0x10000 && ClassName(g, out _) == "Game.Game") { Log("Game=0x" + g.ToString("X")); return g; }
        }
        status("no Game object");
        Log("no Game object");
        return 0;
    }
}
