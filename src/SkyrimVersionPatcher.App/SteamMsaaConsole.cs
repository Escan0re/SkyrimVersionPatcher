using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Accessibility;
using Microsoft.Win32.SafeHandles;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

/// <summary>Reads Chromium's standard MSAA/IA2 provider without restarting Steam or changing settings.</summary>
internal static class SteamMsaaConsole
{
    private const int MaxNodes = 8192;
    private const int MaxDepth = 48;
    private const int MaxText = 524288;
    private const int RoleDocument = 15;
    private const int RoleText = 42;
    private const int RoleStaticText = 41;
    private const int ForbiddenStates = 0x1 | 0x40 | 0x8000 | 0x10000 | 0x200000;
    private const int FocusableState = 0x100000;
    private const int FocusedState = 0x4;
    private static readonly Guid AccessibleIid = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static readonly Guid Accessible2Iid = new("E89F726E-C4F4-4C19-BB19-B647D7FA8478");

    internal sealed record InputProof(string Classes, string Tag, int Role, int State, string? DocumentUrl,
        bool ConsoleAncestor, bool FormAncestor);
    internal static bool MatchesInput(InputProof input, SteamConsoleCssClasses classes) =>
        input.Role == RoleText && input.Tag.Equals("input", StringComparison.OrdinalIgnoreCase) &&
        (input.State & ForbiddenStates) == 0 && (input.State & FocusableState) != 0 &&
        SteamConsoleIdentity.HasClass(input.Classes, classes.InputBox) &&
        (input.ConsoleAncestor || SteamConsoleIdentity.IsConsoleRoute(input.DocumentUrl) ||
            SteamConsoleIdentity.IsSteamClientDocument(input.DocumentUrl));

    internal static IReadOnlyDictionary<string, string> ParseAttributes(string attributes)
    {
        if (attributes.Length > 65536) throw new InvalidDataException("Слишком длинные атрибуты интерфейса Steam.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var key = new StringBuilder();
        var value = new StringBuilder();
        var hasColon = false;
        var escaped = false;
        void Finish()
        {
            if (key.Length == 0 && value.Length == 0 && !hasColon) return;
            if (!hasColon || key.Length == 0 || !result.TryAdd(key.ToString(), value.ToString()))
                throw new InvalidDataException("Неоднозначные атрибуты интерфейса Steam.");
            key.Clear(); value.Clear(); hasColon = false;
        }
        foreach (var character in attributes)
        {
            var current = hasColon ? value : key;
            if (escaped) { current.Append(character); escaped = false; }
            else if (character == '\\') escaped = true;
            else if (character == ';') Finish();
            else if (character == ':' && !hasColon) hasColon = true;
            else current.Append(character);
        }
        if (escaped) throw new InvalidDataException("Оборванные атрибуты интерфейса Steam.");
        Finish();
        return result;
    }

    internal static string FilterHistory(IEnumerable<string> lines) => string.Join('\n', lines.SelectMany(text => text.Split('\n'))
        .Where(line => line.Contains("download_depot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Downloading depot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Depot download", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error downloading manifest", StringComparison.OrdinalIgnoreCase)));

    internal static bool HasConsole(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, CancellationToken token) =>
        OperatingSystem.IsWindows() && classes.Count > 0 && HasWindowsConsole(session, classes, token);

    [SupportedOSPlatform("windows")]
    private static bool HasWindowsConsole(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, CancellationToken token)
    {
        using var snapshot = Capture(session, classes, token);
        return snapshot.Targets.Count == 1;
    }

    internal static string? ReadConsole(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || classes.Count == 0) return null;
        return ReadWindowsConsole(session, classes, token);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsConsole(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, CancellationToken token)
    {
        using var snapshot = Capture(session, classes, token);
        if (snapshot.Targets.Count != 1) return null;
        var target = snapshot.Targets[0];
        var texts = new List<string>();
        var length = 0;
        var parents = target.Tree.Select(node => node.Parent).ToHashSet();
        for (var index = target.HistoryIndex; index < target.Tree.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!DescendsFrom(target.Tree, index, target.HistoryIndex)) continue;
            var node = target.Tree[index];
            if (node.Role != RoleStaticText || node.Tag.Equals("input", StringComparison.OrdinalIgnoreCase) ||
                parents.Contains(index)) continue;
            texts.Add(node.Name);
            length += node.Name.Length;
            while (length > MaxText && texts.Count > 1) { length -= texts[0].Length; texts.RemoveAt(0); }
        }
        var text = FilterHistory(texts);
        return text.Length > MaxText ? text[^MaxText..] : text;
    }

    internal static bool TryDispatch(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, string command, CancellationToken token,
        Action<SteamConsoleInputDispatchResult>? reportFailure = null)
    {
        SteamCdpTransport.ValidateCommand(command);
        if (!OperatingSystem.IsWindows() || classes.Count == 0) return false;
        return DispatchWindows(session, classes, command, token, reportFailure);
    }

    [SupportedOSPlatform("windows")]
    private static bool DispatchWindows(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, string command, CancellationToken token,
        Action<SteamConsoleInputDispatchResult>? reportFailure)
    {
        using var snapshot = Capture(session, classes, token);
        if (snapshot.Targets.Count != 1) return false;
        var target = snapshot.Targets[0];
        try
        {
            var result = SteamConsoleInputDispatch.Run(command,
                () => SetForegroundWindow(target.TopWindow) || GetForegroundWindow() == target.TopWindow,
                () => target.Input.Accessible.accSelect(1, 0), // SELFLAG_TAKEFOCUS, CHILDID_SELF.
                () => HasForeground(target, snapshot, session),
                () => HasFocus(target, snapshot, session),
                () => target.Input.Accessible.get_accValue(0),
                value => target.Input.Accessible.set_accValue(0, value),
                ModifiersPressed,
                () =>
                {
                    var input = new[]
                    {
                        new NativeInput { Type = 1, Keyboard = new NativeKeyboard { VirtualKey = 13 } },
                        new NativeInput { Type = 1, Keyboard = new NativeKeyboard { VirtualKey = 13, Flags = 2 } }
                    };
                    // No COM operations or fallback retry occur after attempting the irreversible Enter.
                    if (SendInput(2, input, Marshal.SizeOf<NativeInput>()) != 2)
                        throw new SteamConsoleUnavailableException("Windows не подтвердила отправку команды в Steam. Загрузка может продолжаться; установка остановлена.", command);
                }, token);
            if (result != SteamConsoleInputDispatchResult.Sent) reportFailure?.Invoke(result);
            return result == SteamConsoleInputDispatchResult.Sent;
        }
        catch (COMException error) { ThrowIfDenied(error.HResult); return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasForeground(Target target, Snapshot snapshot, SteamClientSession session)
    {
        if (GetForegroundWindow() != target.TopWindow) return false;
        GetWindowThreadProcessId(target.TopWindow, out var foregroundPid);
        return snapshot.TrustedPids.Contains((int)foregroundPid) &&
            WindowsProcessIdentity.TryGetExecutablePath(session.ProcessId, out var path, out _) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(session.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasFocus(Target target, Snapshot snapshot, SteamClientSession session)
    {
        if (!HasForeground(target, snapshot, session) || (Convert.ToInt32(target.Input.Accessible.get_accState(0)) & FocusedState) == 0)
            return false;
        var focus = target.Root.accFocus;
        if (focus is not IAccessible focused) return false;
        snapshot.Own(focus);
        var ia2 = GetIa2(focused, snapshot);
        if (ia2 is null) return false;
        var result = ia2.GetUniqueId(out var id);
        ThrowIfDenied(result);
        if (result < 0 || id != target.Input.Id) return false;
        result = ia2.GetWindowHandle(out var handle);
        ThrowIfDenied(result);
        if (result < 0) return false;
        GetWindowThreadProcessId(handle, out var focusPid);
        return snapshot.TrustedPids.Contains((int)focusPid) && GetAncestor(handle, 2) == target.TopWindow &&
            WindowsProcessIdentity.TryGetExecutablePath(session.ProcessId, out var path, out _) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(session.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static Snapshot Capture(SteamClientSession session, IReadOnlyList<SteamConsoleCssClasses> classes, CancellationToken token)
    {
        var snapshot = new Snapshot();
        try
        {
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var currentSession)) return snapshot;
            var parents = ReadParents();
            if (parents is null) return snapshot;
            bool Trusted(int pid)
            {
                if (snapshot.TrustedPids.Contains(pid)) return true;
                var metadata = ReadMetadata(pid);
                var valid = pid == session.ProcessId
                    ? metadata is not null && metadata.WindowsSessionId == currentSession &&
                        Path.GetFullPath(metadata.ExecutablePath).Equals(Path.GetFullPath(session.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                    : SteamCdpListener.IsTrustedProcessGraph(pid, session.ProcessId, currentSession, session.ExecutablePath,
                        session.SteamDirectory, parents, ReadMetadata);
                if (valid) snapshot.TrustedPids.Add(pid);
                return valid;
            }
            bool Owned(IntPtr window)
            {
                GetWindowThreadProcessId(window, out var pid);
                return pid <= int.MaxValue && Trusted((int)pid);
            }
            var roots = new HashSet<IntPtr>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || !Owned(window)) return true;
                void Add(IntPtr handle)
                {
                    var name = new StringBuilder(256);
                    GetClassNameW(handle, name, name.Capacity);
                    if (Owned(handle) && name.ToString().Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
                        roots.Add(handle);
                }
                Add(window);
                EnumChildWindows(window, (child, _) => { Add(child); return roots.Count < 128; }, IntPtr.Zero);
                return roots.Count < 128;
            }, IntPtr.Zero);
            if (roots.Count >= 128) return snapshot; // An incomplete window scan cannot prove there is only one input.
            foreach (var handle in roots)
            {
                token.ThrowIfCancellationRequested();
                ProbeAccessibility(handle);
                var iid = AccessibleIid;
                var result = AccessibleObjectFromWindow(handle, 0xfffffffc, ref iid, out var value);
                snapshot.Own(value);
                ThrowIfDenied(result);
                if (result < 0 || value is not IAccessible root) continue;
                try
                {
                    _ = root.get_accName(0); // Chromium's accessibility detection and lazy AX-mode activation.
                    var tree = ReadTree(root, snapshot, token);
                    if (tree is null) continue;
                    for (var index = 0; index < tree.Count; index++)
                    {
                        var node = tree[index];
                        if (node.Id == 0) continue;
                        foreach (var css in classes)
                        {
                            var history = -1;
                            var form = false;
                            var console = false;
                            var documentSeen = false;
                            string? url = null;
                            for (var ancestor = node.Parent; ancestor >= 0; ancestor = tree[ancestor].Parent)
                            {
                                var parent = tree[ancestor];
                                if (parent.Role == RoleDocument && !documentSeen)
                                {
                                    documentSeen = true;
                                    url = parent.Value;
                                    // Steam's SPA keeps /index.html while generic Console div/form ancestors
                                    // can be omitted from Chromium's AX tree. The exact installed input class
                                    // remains required; this document alone never identifies an arbitrary edit.
                                    if (SteamConsoleIdentity.IsConsoleRoute(parent.Value) ||
                                        SteamConsoleIdentity.IsSteamClientDocument(parent.Value)) history = ancestor;
                                }
                                if (SteamConsoleIdentity.HasClass(parent.Classes, css.ConsoleInput)) form = true;
                                if (SteamConsoleIdentity.HasClass(parent.Classes, css.Console)) { console = true; history = ancestor; }
                            }
                            if (history < 0 || !MatchesInput(new(node.Classes, node.Tag, node.Role, node.State, url, console, form), css)) continue;
                            var top = GetAncestor(handle, 2);
                            if (top == IntPtr.Zero || !Owned(top)) continue;
                            if (!snapshot.Targets.Any(target => target.TopWindow == top && target.Input.Id == node.Id))
                                snapshot.Targets.Add(new(top, root, tree, node, history));
                            break;
                        }
                    }
                }
                catch (COMException error) { ThrowIfDenied(error.HResult); }
                catch (InvalidDataException) { /* Unsupported or ambiguous provider metadata is never eligible. */ }
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static void ProbeAccessibility(IntPtr ownedRenderer)
    {
        // Chromium's custom accessibility detection object is queried only in an owned, exact renderer HWND.
        // E_FAIL/E_NOINTERFACE merely mean that this provider does not expose the optional detection object.
        object? probe = null;
        try
        {
            var iid = AccessibleIid;
            var result = AccessibleObjectFromWindow(ownedRenderer, 1, ref iid, out probe);
            ThrowIfDenied(result);
            if (result >= 0 && probe is IAccessible accessible) _ = accessible.get_accName(0);
        }
        catch (COMException error) { ThrowIfDenied(error.HResult); }
        finally
        {
            if (probe is not null && Marshal.IsComObject(probe))
                try { Marshal.ReleaseComObject(probe); } catch (Exception error) when (error is ArgumentException or InvalidComObjectException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<Node>? ReadTree(IAccessible root, Snapshot scope, CancellationToken token)
    {
        var pending = new Stack<(IAccessible Accessible, int Parent, int Depth)>();
        pending.Push((root, -1, 0));
        var nodes = new List<Node>();
        var visited = new HashSet<int>();
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (nodes.Count >= MaxNodes) return null;
            var next = pending.Pop();
            var ia2 = GetIa2(next.Accessible, scope);
            if (ia2 is null) return null;
            var result = ia2.GetAttributes(out var attributes);
            ThrowIfDenied(result);
            if (result < 0) return null;
            result = ia2.GetUniqueId(out var id);
            ThrowIfDenied(result);
            if (result < 0 || id == 0 || !visited.Add(id)) return null;
            var attrs = ParseAttributes(attributes ?? "");
            var role = Convert.ToInt32(next.Accessible.get_accRole(0));
            var state = Convert.ToInt32(next.Accessible.get_accState(0));
            var value = role == RoleDocument ? next.Accessible.get_accValue(0) : null;
            var node = new Node(next.Accessible, next.Parent, id, role, state, attrs.GetValueOrDefault("class") ?? "",
                attrs.GetValueOrDefault("tag") ?? "", value, next.Accessible.get_accName(0) ?? "");
            var index = nodes.Count;
            nodes.Add(node);
            var count = next.Accessible.accChildCount;
            if (count < 0 || count > MaxNodes || next.Depth >= MaxDepth && count > 0) return null;
            if (count == 0) continue;
            var children = new object[count];
            result = AccessibleChildren(next.Accessible, 0, count, children, out var obtained);
            foreach (var child in children) scope.Own(child);
            ThrowIfDenied(result);
            if (result < 0 || obtained != count) return null;
            for (var childIndex = children.Length - 1; childIndex >= 0; childIndex--)
            {
                var child = children[childIndex];
                if (child is int childId)
                {
                    child = next.Accessible.get_accChild(childId);
                    scope.Own(child);
                }
                if (child is not IAccessible accessible) return null;
                pending.Push((accessible, index, next.Depth + 1));
            }
        }
        return nodes;
    }

    [SupportedOSPlatform("windows")]
    private static INativeAccessible2? GetIa2(IAccessible accessible, Snapshot scope)
    {
        if (accessible is not INativeServiceProvider provider) return null;
        var service = AccessibleIid;
        var iid = Accessible2Iid;
        var result = provider.QueryService(ref service, ref iid, out var pointer);
        try
        {
            ThrowIfDenied(result);
            if (result < 0 || pointer == IntPtr.Zero) return null;
            var value = Marshal.GetObjectForIUnknown(pointer);
            scope.Own(value);
            return value as INativeAccessible2;
        }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }

    private static bool DescendsFrom(IReadOnlyList<Node> tree, int index, int ancestor)
    {
        for (var current = index; current >= 0; current = tree[current].Parent) if (current == ancestor) return true;
        return false;
    }
    internal static void ThrowIfDenied(int result)
    {
        if (result == unchecked((int)0x80070005))
            throw new SteamConsoleUnavailableException("Windows не разрешила доступ к полю Steam Console. Steam и патчер должны работать с одинаковыми правами.");
    }

    [SupportedOSPlatform("windows")]
    private sealed class Snapshot : IDisposable
    {
        private readonly List<object> owned = [];
        internal List<Target> Targets { get; } = [];
        internal HashSet<int> TrustedPids { get; } = [];
        internal void Own(object? value) { if (value is not null && Marshal.IsComObject(value)) owned.Add(value); }
        public void Dispose()
        {
            for (var index = owned.Count - 1; index >= 0; index--)
                try { Marshal.ReleaseComObject(owned[index]); } catch (Exception error) when (error is ArgumentException or InvalidComObjectException) { }
            owned.Clear();
        }
    }
    private sealed record Node(IAccessible Accessible, int Parent, int Id, int Role, int State, string Classes, string Tag, string? Value, string Name);
    private sealed record Target(IntPtr TopWindow, IAccessible Root, List<Node> Tree, Node Input, int HistoryIndex);

    [SupportedOSPlatform("windows")]
    private static SteamCdpListener.ProcessMetadata? ReadMetadata(int pid)
    {
        if (pid <= 0 || !ProcessIdToSessionId((uint)pid, out var session) ||
            !WindowsProcessIdentity.TryGetExecutablePath(pid, out var path, out _)) return null;
        using var handle = OpenProcess(0x1000, false, pid);
        return handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) ? null : new(session, path, created);
    }

    private static IReadOnlyDictionary<int, int>? ReadParents()
    {
        using var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle.IsInvalid) return null;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(handle, ref entry)) return null;
        var parents = new Dictionary<int, int>();
        do
        {
            if (parents.Count > 65536) return null;
            if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        } while (Process32NextW(handle, ref entry));
        return Marshal.GetLastWin32Error() == 18 ? parents : null;
    }

    private static bool ModifiersPressed() => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size; public uint Usage; public uint ProcessId; public UIntPtr Heap; public uint ModuleId;
        public uint Threads; public uint ParentProcessId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public NativeKeyboard Keyboard; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? accessible);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren([MarshalAs(UnmanagedType.Interface)] IAccessible accessible,
        int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2, ArraySubType = UnmanagedType.Struct)] object[] children, out int obtained);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] input, int size);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeServiceProvider
    { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr value); }

    // Complete inherited IDispatch/IAccessible prefix is required; CLR COM interface inheritance
    // does not flatten it automatically. Method order and signatures come from oleacc.h and IA2 IDL:
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/oleacc.h
    // https://github.com/LinuxA11y/IAccessible2/blob/master/api/Accessible2.idl
    [ComImport, Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeAccessible2
    {

    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint locale, out IntPtr info);
    [PreserveSig] int GetIdsOfNames(ref Guid iid, IntPtr names, uint count, uint locale, IntPtr ids);
    [PreserveSig] int Invoke(int id, ref Guid iid, uint locale, ushort flags, IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argument);
    [PreserveSig] int GetParent([MarshalAs(UnmanagedType.IDispatch)] out object parent);
    [PreserveSig] int GetChildCount(out int count);
    [PreserveSig] int GetChild([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.IDispatch)] out object accessible);
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] int GetValue([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string description);
    [PreserveSig] int GetRole([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object role);
    [PreserveSig] int GetState([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object state);
    [PreserveSig] int GetHelp([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string help);
    [PreserveSig] int GetHelpTopic([MarshalAs(UnmanagedType.BStr)] out string helpFile, [MarshalAs(UnmanagedType.Struct)] object child, out int topic);
    [PreserveSig] int GetKeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string shortcut);
    [PreserveSig] int GetFocus([MarshalAs(UnmanagedType.Struct)] out object focus);
    [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Struct)] out object selection);
    [PreserveSig] int GetDefaultAction([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string action);
    [PreserveSig] int Select(int flags, [MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int Location(out int left, out int top, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int Navigate(int direction, [MarshalAs(UnmanagedType.Struct)] object start, [MarshalAs(UnmanagedType.Struct)] out object end);
    [PreserveSig] int HitTest(int x, int y, [MarshalAs(UnmanagedType.Struct)] out object child);
    [PreserveSig] int DefaultAction([MarshalAs(UnmanagedType.Struct)] object child);
    [PreserveSig] int PutName([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] string name);
    [PreserveSig] int PutValue([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] string value);

        [PreserveSig] int GetRelationCount(out int count);
        [PreserveSig] int GetRelation(int index, out IntPtr relation);
        [PreserveSig] int GetRelations(int maximum, IntPtr relations, out int count);
        [PreserveSig] int GetExtendedRoleId(out int role);
        [PreserveSig] int ScrollTo(int type);
        [PreserveSig] int ScrollToPoint(int coordinateType, int x, int y);
        [PreserveSig] int GetGroupPosition(out int level, out int similarCount, out int position);
        [PreserveSig] int GetStates(out int states);
        [PreserveSig] int GetExtendedRole([MarshalAs(UnmanagedType.BStr)] out string role);
        [PreserveSig] int GetLocalizedExtendedRole([MarshalAs(UnmanagedType.BStr)] out string role);
        [PreserveSig] int GetExtendedStateCount(out int count);
        [PreserveSig] int GetExtendedStates(int maximum, IntPtr states, out int count);
        [PreserveSig] int GetLocalizedExtendedStates(int maximum, IntPtr states, out int count);
        [PreserveSig] int GetUniqueId(out int id);
        [PreserveSig] int GetWindowHandle(out IntPtr window);
        [PreserveSig] int GetIndexInParent(out int index);
        [PreserveSig] int GetLocale(out NativeLocale locale);
        [PreserveSig] int GetAttributes([MarshalAs(UnmanagedType.BStr)] out string attributes);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeLocale
    {
        [MarshalAs(UnmanagedType.BStr)] public string Language;
        [MarshalAs(UnmanagedType.BStr)] public string Country;
        [MarshalAs(UnmanagedType.BStr)] public string Variant;
    }
}
