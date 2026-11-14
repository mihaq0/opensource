using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NightRun;

static class Rw
{
    public static readonly uint C_ATTACH = (0x22u << 16) | (2u << 14) | (0xA00u << 2);
    public static readonly uint C_READ = (0x22u << 16) | (1u << 14) | (0xA01u << 2);
    public static readonly uint C_DETACH = (0x22u << 16) | (2u << 14) | (0xA02u << 2);
    public static IntPtr Device = new IntPtr(-1);
    static readonly object IoLock = new object();
    static IntPtr output;
    static int outputSize;
    public static long ReadCount;

    [StructLayout(LayoutKind.Sequential)] struct Req { public ulong address; public uint size; public uint reserved; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr se, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr d, uint c, IntPtr i, uint isz, IntPtr o, uint osz, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, ref Input inputs, int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static unsafe bool ReadCore(ulong address, uint size, out int transferred)
    {
        transferred = 0;
        if (Device == new IntPtr(-1) || address < 0x10000 || size == 0 || size > 0x100000) return false;
        int needed = 12 + (int)size;
        if (needed > outputSize)
        {
            IntPtr next = Marshal.AllocHGlobal(needed);
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
            output = next; outputSize = needed;
        }
        Req request = new Req { address = address, size = size };
        Interlocked.Increment(ref ReadCount);
        if (!DeviceIoControl(Device, C_READ, (IntPtr)(&request), 16, output, (uint)needed, out uint returned, IntPtr.Zero) || returned < 12) return false;
        int status = Marshal.ReadInt32(output), count = Marshal.ReadInt32(output, 4);
        if (status < 0 || count < 0 || count > size || returned < 12u + (uint)count) return false;
        transferred = count;
        return true;
    }

    public static unsafe bool ReadInto(ulong address, Span<byte> destination)
    {
        lock (IoLock)
        {
            if (!ReadCore(address, (uint)destination.Length, out int count) || count != destination.Length) return false;
            new ReadOnlySpan<byte>((void*)IntPtr.Add(output, 12), count).CopyTo(destination);
            return true;
        }
    }

    public static bool Read(ulong address, uint size, out byte[] data)
    {
        lock (IoLock)
        {
            data = Array.Empty<byte>();
            if (!ReadCore(address, size, out int count)) return false;
            data = new byte[count];
            if (count > 0) Marshal.Copy(IntPtr.Add(output, 12), data, 0, count);
            return true;
        }
    }
    public static bool U64(ulong address, out ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        bool ok = ReadInto(address, bytes);
        value = ok ? BitConverter.ToUInt64(bytes) : 0;
        return ok;
    }
    public static ulong U64(ulong address) { U64(address, out ulong value); return value; }
    public static bool U32(ulong address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        bool ok = ReadInto(address, bytes);
        value = ok ? BitConverter.ToUInt32(bytes) : 0;
        return ok;
    }
    public static uint U32(ulong address) { U32(address, out uint value); return value; }
    public static int I32(ulong address) => unchecked((int)U32(address));
    public static float F32(ulong address) => BitConverter.Int32BitsToSingle(I32(address));
    public static bool Key(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static bool Mouse(int dx, int dy, out int error)
    {
        var input = new Input { Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = 1 } } };
        bool sent = SendInput(1, ref input, Marshal.SizeOf<Input>()) == 1;
        error = sent ? 0 : Marshal.GetLastWin32Error();
        return sent;
    }
    public static void Topmost(IntPtr h) => SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x53);
    public static void RegisterHk(IntPtr h, int id, uint vk) => RegisterHotKey(h, id, 0x4000, vk);
    public static void UnregisterHk(IntPtr h, int id) => UnregisterHotKey(h, id);
    public static bool GameFocused(uint gamePid)
    {
        IntPtr h = GetForegroundWindow();
        return h != IntPtr.Zero && GetWindowThreadProcessId(h, out uint pid) != 0 && pid == gamePid;
    }

    public static bool OpenDevice()
    {
        lock (IoLock)
        {
            if (Device != new IntPtr(-1)) return true;
            Device = CreateFileW(@"\\.\WhReader4", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            return Device != new IntPtr(-1);
        }
    }
    public static unsafe bool Attach(uint pid)
    {
        lock (IoLock)
        {
            ulong request = pid;
            return Device != new IntPtr(-1) && DeviceIoControl(Device, C_ATTACH, (IntPtr)(&request), 8, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }
    public static void Detach()
    {
        lock (IoLock)
        {
            if (Device != new IntPtr(-1))
            {
                DeviceIoControl(Device, C_DETACH, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                CloseHandle(Device); Device = new IntPtr(-1);
            }
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
            output = IntPtr.Zero; outputSize = 0;
        }
    }
}
