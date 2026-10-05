using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

/// <summary>Checks the owner of Steam's local CEF endpoint without requesting process memory access.</summary>
internal static class SteamCdpListener
{
    internal const int Port = 8080;

    internal static int? FindTrustedOwner(SteamClientSession session)
    {
        if (!OperatingSystem.IsWindows() ||
            !ProcessIdToSessionId((uint)Environment.ProcessId, out var currentWindowsSession) ||
            !WindowsProcessIdentity.TryGetExecutablePath(session.ProcessId, out var clientPath, out _) ||
            !Path.GetFullPath(clientPath).Equals(Path.GetFullPath(session.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            return null;

        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (result != 122 || size < sizeof(uint) || size > 16 * 1024 * 1024) return null;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return null;
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || count > (size - sizeof(uint)) / rowSize) return null;
            int? owner = null;
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(table, sizeof(uint) + index * rowSize));
                var port = ((row.LocalPort & 0xff) << 8) | ((row.LocalPort >> 8) & 0xff);
                if (row.State != 2 || port != Port) continue;
                // A wildcard listener also receives local connections, but is deliberately rejected.
                if (!IPAddress.IsLoopback(new IPAddress(row.LocalAddress))) return null;
                if (row.OwningPid is 0 or > int.MaxValue ||
                    !WindowsProcessIdentity.TryGetExecutablePath((int)row.OwningPid, out var path, out _) ||
                    !IsSteamHelperPath(path, session.SteamDirectory)) return null;
                if (owner is not null && owner != (int)row.OwningPid) return null;
                owner = (int)row.OwningPid;
            }
            if (owner is null) return null;
            var parents = ReadParentProcessIds();
            return parents is not null && IsTrustedProcessGraph(owner.Value, session.ProcessId, currentWindowsSession,
                session.ExecutablePath, session.SteamDirectory, parents, ReadLiveMetadata) ? owner : null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    internal static bool IsSteamHelperPath(string executablePath, string steamDirectory)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamDirectory));
        return Path.GetFileName(fullPath).Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) &&
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record ProcessMetadata(uint WindowsSessionId, string ExecutablePath, long CreationTime);

    internal static bool IsTrustedProcessGraph(int ownerPid, int steamPid, uint currentWindowsSession,
        string expectedSteamExecutable, string steamDirectory, IReadOnlyDictionary<int, int> parents,
        Func<int, ProcessMetadata?> readMetadata)
    {
        if (ownerPid <= 0 || steamPid <= 0 || ownerPid == steamPid) return false;
        var steam = readMetadata(steamPid);
        if (steam is null || steam.WindowsSessionId != currentWindowsSession || steam.CreationTime <= 0 ||
            !Path.GetFullPath(steam.ExecutablePath).Equals(Path.GetFullPath(expectedSteamExecutable), StringComparison.OrdinalIgnoreCase))
            return false;
        var visited = new HashSet<int>();
        var pid = ownerPid;
        var childCreationTime = long.MaxValue;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!visited.Add(pid)) return false;
            var process = pid == steamPid ? steam : readMetadata(pid);
            if (process is null || process.WindowsSessionId != currentWindowsSession || process.CreationTime <= 0 ||
                process.CreationTime > childCreationTime) return false;
            if (pid == steamPid) return true;
            if (!IsSteamHelperPath(process.ExecutablePath, steamDirectory) ||
                !parents.TryGetValue(pid, out var parent) || parent <= 0) return false;
            childCreationTime = process.CreationTime;
            pid = parent;
        }
        return false;
    }

    private static ProcessMetadata? ReadLiveMetadata(int pid)
    {
        if (pid <= 0 || !ProcessIdToSessionId((uint)pid, out var windowsSession) ||
            !WindowsProcessIdentity.TryGetExecutablePath(pid, out var executable, out _)) return null;
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        return new ProcessMetadata(windowsSession, executable, created);
    }

    private static IReadOnlyDictionary<int, int>? ReadParentProcessIds()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS; no heaps or modules.
        if (snapshot.IsInvalid) return null;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry)) return null;
        var parents = new Dictionary<int, int>();
        do
        {
            if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            if (parents.Count > 65_536) return null;
        }
        while (Process32NextW(snapshot, ref entry));
        return Marshal.GetLastWin32Error() == 18 ? parents : null; // ERROR_NO_MORE_FILES.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);
}
