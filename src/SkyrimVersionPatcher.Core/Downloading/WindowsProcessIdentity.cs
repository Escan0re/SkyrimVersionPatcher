using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkyrimVersionPatcher.Core.Downloading;

/// <summary>Reads only a process image path, without opening its memory or requesting administrator rights.</summary>
public static class WindowsProcessIdentity
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int InvalidParameter = 87;
    private const int NotSupported = 50;
    private const int NotFound = 1168;

    public static bool TryGetExecutablePath(int processId, [NotNullWhen(true)] out string? executablePath, out int errorCode)
    {
        executablePath = null;
        errorCode = 0;
        if (processId <= 0) { errorCode = InvalidParameter; return false; }
        if (!OperatingSystem.IsWindows()) { errorCode = NotSupported; return false; }
        return TryGetWindowsExecutablePath(processId, out executablePath, out errorCode);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsExecutablePath(int processId, [NotNullWhen(true)] out string? executablePath, out int errorCode)
    {
        executablePath = null;
        errorCode = 0;
        using var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle.IsInvalid) { errorCode = Marshal.GetLastWin32Error(); return false; }
        if (!GetExitCodeProcess(handle, out var exitCode))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
        if (exitCode != StillActive) { errorCode = NotFound; return false; }

        // A fixed long-path buffer also handles executable paths beyond MAX_PATH.
        var buffer = new StringBuilder(32768);
        var characters = (uint)buffer.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, buffer, ref characters))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
        if (characters == 0) { errorCode = NotFound; return false; }
        executablePath = buffer.ToString();
        return true;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder executableName, ref uint characters);
}
