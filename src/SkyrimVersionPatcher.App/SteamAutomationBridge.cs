using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Downloading;

namespace SkyrimVersionPatcher.App;

/// <summary>Uses a confirmed console field or a verified local Steam debug transport, never an unknown window.</summary>
internal sealed class SteamAutomationBridge(Action<string>? reportStatus = null) : ISteamClientBridge, IAsyncDisposable
{
    private const int MaxNodes = 8192;
    private readonly SemaphoreSlim openingConsole = new(1, 1);
    private string? openedSession;
    private IReadOnlyList<SteamConsoleCssClasses> consoleClasses = [];
    private string? classesDirectory;
    private SteamCdpTransport? debugConsole;
    private bool useMsaaConsole;

    public SteamClientSession GetSession() => SteamClientLocator.GetSession();

    public async Task PrepareConsoleAsync(SteamClientSession session, CancellationToken token) =>
        _ = await EnsureConsoleOpenedAsync(session, token, forcePreparation: true).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await openingConsole.WaitAsync().ConfigureAwait(false);
        try
        {
            if (debugConsole is not null) await debugConsole.DisposeAsync().ConfigureAwait(false);
            debugConsole = null;
        }
        finally { openingConsole.Release(); openingConsole.Dispose(); }
    }

    public static void OpenConsole(SteamClientSession session)
    {
        try
        {
            var start = new ProcessStartInfo(session.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(SteamConsoleCommands.ConsoleUri);
            using var process = Process.Start(start);
            if (process is null) throw new SteamConsoleUnavailableException("Не удалось автоматически открыть Steam Console.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 5) { throw ConsoleAccessDenied("открыть Steam Console"); }
    }

    public async Task DispatchDepotAsync(SteamClientSession requested, DepotManifest depot, CancellationToken token)
    {
        var command = SteamConsoleCommands.ForDepot(depot);
        var session = await EnsureConsoleOpenedAsync(requested, token).ConfigureAwait(false);
        if (debugConsole is not null)
        {
            ResolveSession(session);
            await debugConsole.DispatchAsync(command, token).ConfigureAwait(false);
            return;
        }
        SteamConsoleInputDispatchResult? lastFailure = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            session = ResolveSession(session);
            var transport = await Task.Run(() => SteamConsoleTransportFallback.TryDispatch(
                useMsaaConsole ? SteamConsoleTransport.Msaa : SteamConsoleTransport.UiAutomation,
                () => TryDispatch(ResolveSession(session), command, token, result => lastFailure = result),
                () => SteamMsaaConsole.TryDispatch(ResolveSession(session), consoleClasses, command, token, result => lastFailure = result),
                token), token).ConfigureAwait(false);
            if (transport is { } selected)
            { useMsaaConsole = selected == SteamConsoleTransport.Msaa; return; }
            await Task.Delay(350, token).ConfigureAwait(false);
        }
        var detail = lastFailure switch
        {
            SteamConsoleInputDispatchResult.WindowUnavailable => "Windows не разрешила вывести окно Steam на передний план.",
            SteamConsoleInputDispatchResult.FocusUnavailable => "Steam не подтвердил фокус в поле консоли.",
            SteamConsoleInputDispatchResult.ValueUnavailable => "Steam не подтвердил введённую команду в поле консоли.",
            SteamConsoleInputDispatchResult.FocusLost => "Окно или поле Steam потеряло фокус во время ввода.",
            SteamConsoleInputDispatchResult.ModifiersPressed => "Во время ввода была нажата Shift, Ctrl, Alt или клавиша Windows.",
            _ => "Поле консоли стало недоступно для Windows."
        };
        throw new SteamConsoleUnavailableException("Не удалось автоматически отправить запрос загрузки в Steam Console. " +
            detail + " Файлы игры не изменены. Закройте диалоги Steam, отпустите клавиши и повторите установку.", command);
    }

    private SteamClientSession ResolveSession(SteamClientSession requested)
    {
        var current = GetSession();
        SteamConsoleIdentity.RequireSameAccountAndExecutable(requested, current);
        if (requested.ProcessId != current.ProcessId)
            throw new SteamConsoleUnavailableException("Процесс Steam изменился во время загрузки. Патчер не перезапускает клиент; повторите установку с открытым Steam.");
        return current;
    }

    private async Task<SteamClientSession> EnsureConsoleOpenedAsync(SteamClientSession requested, CancellationToken token,
        bool forcePreparation = false)
    {
        await openingConsole.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var session = ResolveSession(requested);
            if (!forcePreparation && openedSession == SessionKey(session)) return session;
            openedSession = null;
            if (debugConsole is not null) { await debugConsole.DisposeAsync().ConfigureAwait(false); debugConsole = null; }
            session = await new SteamConsolePreparer(new ConsoleEnvironment(this), reportStatus)
                .PrepareAsync(session, token).ConfigureAwait(false);
            openedSession = SessionKey(session);
            return session;
        }
        finally { openingConsole.Release(); }
    }

    private sealed class ConsoleEnvironment(SteamAutomationBridge bridge) : ISteamConsoleEnvironment
    {
        public SteamClientSession GetSession() => bridge.GetSession();
        public void OpenConsole(SteamClientSession session) => SteamAutomationBridge.OpenConsole(session);
        public async Task<bool> TryConnectExistingDebugAsync(SteamClientSession session, CancellationToken token)
        {
            bridge.debugConsole = await SteamCdpTransport.TryConnectAsync(session, token).ConfigureAwait(false);
            return bridge.debugConsole is not null;
        }
        public async Task<bool> TryPrepareAccessibilityAsync(SteamClientSession session, CancellationToken token)
        {
            if (bridge.classesDirectory != session.SteamDirectory)
            {
                try { bridge.consoleClasses = SteamConsoleIdentity.ReadClasses(session.SteamDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                { bridge.consoleClasses = []; }
                bridge.classesDirectory = session.SteamDirectory;
            }
            SteamConsoleUnavailableException? accessError = null;
            try { await Task.Run(() => ActivateAccessibility(session, token), token).ConfigureAwait(false); }
            catch (SteamConsoleUnavailableException error) when (IsConsoleAccessDenied(error)) { accessError = error; }
            return await bridge.WaitForConsoleAsync(session, token, accessError).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForConsoleAsync(SteamClientSession session, CancellationToken token,
        SteamConsoleUnavailableException? accessError = null)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            session = ResolveSession(session);
            try
            {
                if (await Task.Run(() => DiscoverConsole(session, token).Count == 1, token).ConfigureAwait(false))
                { useMsaaConsole = false; return true; }
            }
            catch (SteamConsoleUnavailableException error) when (IsConsoleAccessDenied(error)) { accessError = error; }
            try
            {
                if (await Task.Run(() => SteamMsaaConsole.HasConsole(session, consoleClasses, token), token).ConfigureAwait(false))
                { useMsaaConsole = true; return true; }
            }
            catch (SteamConsoleUnavailableException error) when (IsConsoleAccessDenied(error)) { accessError = error; }
        }
        if (accessError is not null) throw accessError;
        return false;
    }

    public async Task<string?> ReadConsoleAsync(SteamClientSession requested, CancellationToken token)
    {
        var session = await EnsureConsoleOpenedAsync(requested, token).ConfigureAwait(false);
        ResolveSession(session);
        if (debugConsole is not null) return await debugConsole.ReadAsync(token).ConfigureAwait(false);
        return await Task.Run(() => useMsaaConsole
            ? SteamMsaaConsole.ReadConsole(session, consoleClasses, token)
            : ReadConsole(session, token), token).ConfigureAwait(false);
    }

    private string? ReadConsole(SteamClientSession session, CancellationToken token)
    {
        var targets = DiscoverConsole(session, token);
        if (targets.Count != 1) return null;
        string? canonicalOutput = null;
        var canonicalIsDocument = false;
        var fallback = new StringBuilder();
        foreach (var node in ReadRawTree(targets[0].Container, token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = node.Element.Current;
                if (info.IsPassword || info.IsOffscreen || !BelongsToSteam(info.ProcessId, session) ||
                    info.ControlType != ControlType.Document && info.ControlType != ControlType.Text) continue;
                if (node.Element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                {
                    var range = ((TextPattern)pattern).DocumentRange.Clone();
                    range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                    range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -256000);
                    var text = range.GetText(256000);
                    var document = info.ControlType == ControlType.Document && text.Length > 0;
                    if (canonicalOutput is null || document && !canonicalIsDocument ||
                        document == canonicalIsDocument && text.Length > canonicalOutput.Length)
                    { canonicalOutput = text; canonicalIsDocument = document; }
                }
                else if (info.ControlType == ControlType.Text && !string.IsNullOrWhiteSpace(info.Name))
                {
                    fallback.Append(info.Name).Append('\n');
                    if (fallback.Length > 512000) fallback.Remove(0, fallback.Length - 512000);
                }
            }
            catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); }
        }
        // Empty identified history is a usable baseline. Ignore unrelated console/history output.
        return string.Join('\n', (canonicalOutput ?? fallback.ToString()).Split('\n').Where(line =>
            line.Contains("download_depot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Downloading depot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Depot download", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error downloading manifest", StringComparison.OrdinalIgnoreCase)));
    }

    private bool TryDispatch(SteamClientSession session, string command, CancellationToken token,
        Action<SteamConsoleInputDispatchResult> reportFailure)
    {
        var targets = DiscoverConsole(session, token);
        if (targets.Count != 1) return false;
        var target = targets[0];
        try
        {
            token.ThrowIfCancellationRequested();
            var input = target.Input;
            var foreground = new IntPtr(target.Window.Current.NativeWindowHandle);
            if (foreground != IntPtr.Zero)
            {
                var topLevel = GetAncestor(foreground, 2); // GA_ROOT; renderer roots can be child HWNDs.
                if (topLevel != IntPtr.Zero) foreground = topLevel;
            }
            if (foreground == IntPtr.Zero || !IsSteamWindow(foreground, session)) return false;
            var value = (ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern);
            var nativeInput = new IntPtr(input.Current.NativeWindowHandle);
            var postToRenderer = nativeInput != IntPtr.Zero && nativeInput != foreground && IsChild(foreground, nativeInput);
            if (postToRenderer)
            {
                GetWindowThreadProcessId(nativeInput, out var inputPid);
                if (!BelongsToSteam((int)inputPid, session)) return false;
            }
            var result = SteamConsoleInputDispatch.Run(command,
                () => SetForegroundWindow(foreground) || GetForegroundWindow() == foreground,
                input.SetFocus,
                () => GetForegroundWindow() == foreground && IsSteamWindow(foreground, session),
                () => HasExactFocus(input, foreground, session),
                () => value.Current.Value,
                value.SetValue,
                ModifiersPressed,
                () =>
                {
                    if (postToRenderer)
                    {
                        GetWindowThreadProcessId(nativeInput, out var currentInputPid);
                        if (!IsChild(foreground, nativeInput) || !BelongsToSteam((int)currentInputPid, session))
                            throw new SteamConsoleUnavailableException("Поле Steam Console изменилось до отправки команды. Повторите установку.", command);
                        if (!PostMessage(nativeInput, 0x0100, new IntPtr(0x0D), new IntPtr(0x001C0001)) ||
                            !PostMessage(nativeInput, 0x0101, new IntPtr(0x0D), new IntPtr(unchecked((int)0xC01C0001))))
                            throw new SteamConsoleUnavailableException("Windows не подтвердила отправку команды в Steam. Загрузка может продолжаться; установка остановлена.", command);
                        return;
                    }
                    var inputs = new[]
                    {
                        new NativeInput { Type = 1, Keyboard = new NativeKeyboard { VirtualKey = 0x0D } },
                        new NativeInput { Type = 1, Keyboard = new NativeKeyboard { VirtualKey = 0x0D, Flags = 2 } }
                    };
                    if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2)
                        throw new SteamConsoleUnavailableException("Windows не подтвердила отправку команды в Steam. Загрузка может продолжаться; установка остановлена.", command);
                }, token);
            if (result != SteamConsoleInputDispatchResult.Sent) reportFailure(result);
            return result == SteamConsoleInputDispatchResult.Sent;
        }
        catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); return false; }
    }

    private IReadOnlyList<ConsoleTarget> DiscoverConsole(SteamClientSession session, CancellationToken token)
    {
        var targets = new List<ConsoleTarget>();
        if (consoleClasses.Count == 0) return targets;
        foreach (var window in FindSteamAccessibilityRoots(session))
        {
            var nodes = ReadRawTree(window, token);
            for (var index = 0; index < nodes.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var input = nodes[index].Element;
                try
                {
                    var info = input.Current;
                    if (!BelongsToSteam(info.ProcessId, session) || info.ControlType != ControlType.Edit ||
                        info.IsPassword || info.IsOffscreen || !info.IsEnabled || !info.IsKeyboardFocusable ||
                        !input.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly) continue;
                    foreach (var classes in consoleClasses)
                    {
                        if (!SteamConsoleIdentity.HasClass(info.ClassName, classes.InputBox)) continue;
                        AutomationElement? container = null;
                        AutomationElement? routeDocument = null;
                        var form = false;
                        for (var ancestor = nodes[index].Parent; ancestor >= 0; ancestor = nodes[ancestor].Parent)
                        {
                            var element = nodes[ancestor].Element;
                            var parent = element.Current;
                            if (!BelongsToSteam(parent.ProcessId, session)) break;
                            if (SteamConsoleIdentity.HasClass(parent.ClassName, classes.ConsoleInput)) form = true;
                            if (SteamConsoleIdentity.HasClass(parent.ClassName, classes.Console)) { container = element; break; }
                            if (parent.ControlType == ControlType.Document && IsConsoleDocument(element)) routeDocument = element;
                        }
                        // Exact installed CSS class plus console ancestor (or form and verified internal console URL).
                        // An arbitrary selected tab/name never makes other search/login edits eligible.
                        container ??= form ? routeDocument : null;
                        if (container is null) continue;
                        if (!targets.Any(existing => Automation.Compare(existing.Input, input)))
                            targets.Add(new(window, container, input));
                        break;
                    }
                }
                catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); }
            }
        }
        return targets;
    }

    private static bool IsConsoleDocument(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
            SteamConsoleIdentity.IsConsoleRoute(((ValuePattern)value).Current.Value);

    private static IReadOnlyList<RawNode> ReadRawTree(AutomationElement root, CancellationToken token)
    {
        var nodes = new List<RawNode>();
        var pending = new Stack<(AutomationElement Element, int Parent, int Depth)>();
        pending.Push((root, -1, 0));
        var walker = TreeWalker.RawViewWalker;
        while (pending.Count > 0 && nodes.Count < MaxNodes)
        {
            token.ThrowIfCancellationRequested();
            var next = pending.Pop();
            var index = nodes.Count;
            nodes.Add(new(next.Element, next.Parent));
            if (next.Depth >= 64) continue;
            try
            {
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(next.Element);
                while (child is not null && children.Count + nodes.Count + pending.Count < MaxNodes)
                {
                    token.ThrowIfCancellationRequested();
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                for (var item = children.Count - 1; item >= 0; item--) pending.Push((children[item], index, next.Depth + 1));
            }
            catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); }
        }
        return nodes;
    }

    private static void ActivateAccessibility(SteamClientSession session, CancellationToken token)
    {
        // Standard MSAA request, no injection or changes to saved Steam settings.
        foreach (var window in FindNativeSteamWindows(session))
        {
            token.ThrowIfCancellationRequested();
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (GetNativeClass(window) == "Chrome_RenderWidgetHostHWND")
            {
                // Chromium's screen-reader honey pot enables web accessibility in the running
                // renderer. This is a standard WM_GETOBJECT request, not a client restart or OS setting.
                AccessibleObjectFromWindow(window, 1, ref iid, out var honeyPot);
                if (honeyPot is not null && Marshal.IsComObject(honeyPot)) Marshal.ReleaseComObject(honeyPot);
            }
            var result = AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out var accessible);
            if (accessible is not null)
            {
                try
                {
                    // Chromium 126 requires accName as well as its screen-reader probe.
                    // The name is discarded; this only requests the running accessibility provider.
                    if (accessible is Accessibility.IAccessible msaa) _ = msaa.get_accName(0);
                }
                catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); }
                finally { if (Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
            }
            if (result == unchecked((int)0x80070005)) throw ConsoleAccessDenied("включить доступ к интерфейсу Steam Console");
        }
    }

    private static IReadOnlyList<IntPtr> FindNativeSteamWindows(SteamClientSession session)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (IsSteamWindow(window, session)) windows.Add(window);
            return windows.Count < 256;
        }, IntPtr.Zero);
        foreach (var window in windows.ToArray())
        {
            if (windows.Count >= 256) break;
            EnumChildWindows(window, (child, _) =>
            {
                if (IsSteamWindow(child, session)) windows.Add(child);
                return windows.Count < 256;
            }, IntPtr.Zero);
        }
        return windows.Distinct().ToArray();
    }

    private static IEnumerable<AutomationElement> FindSteamAccessibilityRoots(SteamClientSession session)
    {
        var roots = FindSteamWindows(session).ToList();
        foreach (var window in FindNativeSteamWindows(session))
        {
            if (GetNativeClass(window) != "Chrome_RenderWidgetHostHWND") continue;
            try
            {
                var root = AutomationElement.FromHandle(window);
                if (BelongsToSteam(root.Current.ProcessId, session) && !roots.Any(existing => Automation.Compare(existing, root)))
                    roots.Add(root);
            }
            catch (Exception error) when (IsAutomationError(error)) { ThrowIfAccessDenied(error); }
        }
        return roots;
    }

    private static string GetNativeClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) == 0 ? "" : name.ToString();
    }

    private static bool IsSteamWindow(IntPtr window, SteamClientSession session)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId <= int.MaxValue && BelongsToSteam((int)processId, session);
    }
    private static bool HasExactFocus(AutomationElement input, IntPtr window, SteamClientSession session)
    {
        var focused = AutomationElement.FocusedElement;
        if (GetForegroundWindow() != window || !Automation.Compare(input, focused)) return false;
        GetWindowThreadProcessId(window, out var windowPid);
        return BelongsToSteam((int)windowPid, session) && BelongsToSteam(focused.Current.ProcessId, session);
    }
    private static bool ModifiersPressed() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private static IEnumerable<AutomationElement> FindSteamWindows(SteamClientSession session)
    {
        AutomationElementCollection roots;
        try { roots = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch (Exception error) when (IsAccessDenied(error)) { throw ConsoleAccessDenied("получить окно Steam Console"); }
        var result = new List<AutomationElement>();
        foreach (AutomationElement root in roots)
        {
            var confirmedSteam = false;
            try
            {
                confirmedSteam = BelongsToSteam(root.Current.ProcessId, session);
                if (confirmedSteam && !root.Current.IsOffscreen) result.Add(root);
            }
            // An unrelated protected desktop window must not prevent Steam automation.
            catch (Exception error) when (IsAutomationError(error)) { if (confirmedSteam) ThrowIfAccessDenied(error); }
        }
        return result;
    }
    private static string SessionKey(SteamClientSession session) => $"{session.ProcessId}:{session.ActiveUser}:{session.ExecutablePath}";
    private static bool IsAutomationError(Exception error) => error is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;
    private static bool IsAccessDenied(Exception error) => error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005);
    private static bool IsConsoleAccessDenied(SteamConsoleUnavailableException error) =>
        error.Message.StartsWith("Windows не разрешила", StringComparison.Ordinal);
    private static void ThrowIfAccessDenied(Exception error) { if (IsAccessDenied(error)) throw ConsoleAccessDenied("обратиться к интерфейсу Steam Console"); }
    private static SteamConsoleUnavailableException ConsoleAccessDenied(string operation) => new(
        $"Windows не разрешила {operation}. Steam и патчер должны работать от одного пользователя с одинаковыми правами. Steam не перезапускался; файлы игры не изменены.");
    private static bool BelongsToSteam(int processId, SteamClientSession session)
    {
        if (processId == session.ProcessId) return true;
        return WindowsProcessIdentity.TryGetExecutablePath(processId, out var path, out _) &&
            Path.GetFileName(path).Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(session.SteamDirectory).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record ConsoleTarget(AutomationElement Window, AutomationElement Container, AutomationElement Input);
    private sealed record RawNode(AutomationElement Element, int Parent);
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public NativeKeyboard Keyboard; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? accessible);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
}
