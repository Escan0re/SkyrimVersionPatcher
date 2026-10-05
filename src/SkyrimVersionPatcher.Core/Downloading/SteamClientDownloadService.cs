using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Core.Downloading;

/// <summary>Uses the already signed-in Steam client. Steam authenticates and downloads; this service verifies its result.</summary>
public sealed class SteamClientDownloadService(ISteamClientBridge bridge, Func<IReadOnlyList<string>>? steamDirectories = null)
{
    private const long MaximumReceiptBytes = 16L * 1024 * 1024;
    private const int MaximumReceiptFiles = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public TimeSpan CommandAcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan DepotTimeout { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static string GetDepotCacheKey(DepotManifest depot)
    {
        SteamConsoleCommands.ValidateDepot(depot);
        return $"{SteamConsoleCommands.AppId}/{depot.DepotId}/{depot.ManifestId}";
    }

    public static string GetDepotCacheDirectory(string cacheDirectory, DepotManifest depot) =>
        Path.Combine(Path.GetFullPath(cacheDirectory), "depots", SteamConsoleCommands.AppId.ToString(CultureInfo.InvariantCulture),
            depot.DepotId.ToString(CultureInfo.InvariantCulture), depot.ManifestId);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        if (request.Depots.Count == 0 || request.Depots.Select(d => d.DepotId).Distinct().Count() != request.Depots.Count)
            throw new ArgumentException("Нужен непустой набор уникальных депо.");
        foreach (var depot in request.Depots) SteamConsoleCommands.ValidateDepot(depot);
        if (request.ForceFreshDownload && request.LocalFilesOnly)
            throw new ArgumentException("Чистая загрузка Steam несовместима с режимом своих depot-файлов.");
        var cacheRoot = Path.GetFullPath(request.CacheDirectory);
        IReadOnlyList<string>? localSources = null;
        if (request.LocalFilesOnly)
        {
            if (string.IsNullOrWhiteSpace(request.ExistingFilesDirectory))
                throw new ArgumentException("Укажите папку со своими depot-файлами.");
            localSources = LocalDepotResolver.ResolveSources(request.ExistingFilesDirectory, request.Depots);
        }
        if (!string.IsNullOrWhiteSpace(request.InstalledGameDirectory))
        {
            var gameRoot = Path.GetFullPath(request.InstalledGameDirectory);
            if (PathsOverlap(cacheRoot, gameRoot))
                throw new ArgumentException("Хранилище кэша и установленная игра должны находиться в независимых папках.");
            if (localSources is not null && (PathsOverlap(Path.GetFullPath(request.ExistingFilesDirectory!), gameRoot) ||
                localSources.Any(source => PathsOverlap(source, gameRoot))))
                throw new ArgumentException("Папка своих depot-файлов и папка устанавливаемой игры должны находиться независимо друг от друга.");
        }
        SteamClientSession? session = null;
        if (request.ForceFreshDownload)
        {
            session = bridge.GetSession();
            ValidateSession(session);
            foreach (var selected in request.Depots)
                ValidateFreshSteamDepotDirectory(session, selected, request.InstalledGameDirectory, cacheRoot);
        }
        DownloadPathSafety.EnsureNoLinks(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        // Clean receipts live in an independent installer-owned workspace; payload stays
        // in the Steam client's conventional depot folder until installation moves it.
        var preparationRoot = request.ForceFreshDownload
            ? Path.Combine(cacheRoot, "fresh-" + Guid.NewGuid().ToString("N"))
            : cacheRoot;
        DownloadPathSafety.EnsureNoLinks(preparationRoot);
        var lockRoot = Path.Combine(cacheRoot, "locks");
        DownloadPathSafety.EnsureNoLinks(lockRoot);
        Directory.CreateDirectory(lockRoot);
        var sources = new List<string>();
        var overlay = new Dictionary<string, DownloadedFile>(StringComparer.OrdinalIgnoreCase);
        var allCached = true;
        var statistics = new DownloadStatistics(SteamDownloadedBytes: 0);
        FileStream? steamLock = null;
        try
        {
            for (var depotIndex = 0; depotIndex < request.Depots.Count; depotIndex++)
            {
                var depot = request.Depots[depotIndex];
                cancellationToken.ThrowIfCancellationRequested();
                var depotRoot = GetDepotCacheDirectory(preparationRoot, depot);
                DownloadPathSafety.EnsureNoLinks(depotRoot);
                var lockPath = Path.Combine(lockRoot, $"{depot.DepotId}_{depot.ManifestId}.lock");
                DownloadPathSafety.EnsureNoLinks(lockPath);
                await using var depotLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var files = request.ForceFreshDownload ? null : request.LocalFilesOnly
                    ? await ImportLocalDepotAsync(request, depot, localSources![localSources.Count == 1 ? 0 : depotIndex],
                        depotRoot, progress, cancellationToken).ConfigureAwait(false)
                    : await ReadVerifiedCacheAsync(depotRoot, depot, progress, cancellationToken).ConfigureAwait(false);
                if (files is not null)
                {
                    var bytes = SumBytes(files);
                    statistics = request.LocalFilesOnly
                        ? statistics with { TargetBytes = checked(statistics.TargetBytes + bytes), ImportedLocalBytes = checked(statistics.ImportedLocalBytes + bytes) }
                        : statistics with { TargetBytes = checked(statistics.TargetBytes + bytes), ReusedCacheBytes = checked(statistics.ReusedCacheBytes + bytes) };
                }
                else if (!request.ForceFreshDownload)
                {
                    files = await TryImportExistingDepotAsync(request, depot, depotRoot, progress, cancellationToken).ConfigureAwait(false);
                    if (files is not null)
                    {
                        var bytes = SumBytes(files);
                        statistics = statistics with { TargetBytes = checked(statistics.TargetBytes + bytes), ImportedLocalBytes = checked(statistics.ImportedLocalBytes + bytes) };
                    }
                }
                if (files is null && request.ReuseInstalledFiles && !request.LocalFilesOnly && !request.ForceFreshDownload)
                {
                    files = await TryReuseInstalledDepotAsync(request, depot, depotRoot, progress, cancellationToken).ConfigureAwait(false);
                    if (files is not null)
                    {
                        var bytes = SumBytes(files);
                        statistics = statistics with { TargetBytes = checked(statistics.TargetBytes + bytes), ReusedInstalledBytes = checked(statistics.ReusedInstalledBytes + bytes) };
                    }
                }
                if (files is null)
                {
                    if (request.LocalFilesOnly)
                        throw new InvalidDataException($"Свои файлы депо {depot.DepotId}/{depot.ManifestId} не прошли проверку. Игра не изменена.");
                    allCached = false;
                    var expectedBytes = TryGetTargetByteCount(depot, progress) ?? 0;
                    progress?.Report(new($"Для депо {depot.DepotId} нужна загрузка Steam.", Statistics: statistics with
                    {
                        TargetBytes = checked(statistics.TargetBytes + expectedBytes),
                        SteamRequestedBytes = checked(statistics.SteamRequestedBytes + expectedBytes), SteamDownloadedBytes = null
                    }));
                    // A full valid cache works offline. Steam is needed only for a missing/corrupt depot.
                    var currentSession = bridge.GetSession();
                    ValidateSession(currentSession);
                    if (session is not null) RequireSameSessionIdentity(session, currentSession);
                    session = currentSession;
                    if (request.ForceFreshDownload)
                        foreach (var selected in request.Depots)
                            ValidateFreshSteamDepotDirectory(session, selected, request.InstalledGameDirectory, cacheRoot);
                    if (steamLock is null)
                    {
                        var clientLock = Path.Combine(lockRoot, "steam-client.lock");
                        DownloadPathSafety.EnsureNoLinks(clientLock);
                        steamLock = new FileStream(clientLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    if (!request.ForceFreshDownload) QuarantineInvalidCache(cacheRoot, depotRoot, depot, progress);
                    files = await DownloadDepotAsync(session, depot, depotRoot, progress, cancellationToken,
                        request.ForceFreshDownload, request.InstalledGameDirectory, cacheRoot).ConfigureAwait(false);
                    var bytes = SumBytes(files);
                    statistics = statistics with { TargetBytes = checked(statistics.TargetBytes + bytes), SteamRequestedBytes = checked(statistics.SteamRequestedBytes + bytes), SteamDownloadedBytes = null };
                }
                sources.Add(request.ForceFreshDownload ? GetSteamDepotDirectory(session!, depot) : Path.Combine(depotRoot, "content"));
                foreach (var file in files) overlay[file.RelativePath] = file;
                progress?.Report(new($"Депо {depot.DepotId} готово.", Statistics: statistics));
            }
        }
        finally { if (steamLock is not null) await steamLock.DisposeAsync().ConfigureAwait(false); }
        if (!overlay.ContainsKey("SkyrimSE.exe"))
            throw new InvalidDataException("В выбранных депо нет SkyrimSE.exe. Неполный набор депо применять нельзя.");
        progress?.Report(new(allCached ? "Все выбранные депо подготовлены из проверенных локальных данных." : "Steam загрузил все выбранные депо. Файлы проверены.", 100, statistics));
        return new(sources, request.Depots.ToArray(), overlay.Values.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            allCached, statistics, request.ForceFreshDownload ? Path.GetFullPath(session!.SteamDirectory) : null);
    }

    private static long SumBytes(IReadOnlyList<DownloadedFile> files) => files.Aggregate(0L, (sum, file) => checked(sum + file.Length));

    private static bool PathsOverlap(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(first);
        second = Path.TrimEndingDirectorySeparator(second);
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<DownloadedFile>> DownloadDepotAsync(SteamClientSession session,
        DepotManifest depot, string depotRoot, IProgress<DownloadProgress>? progress, CancellationToken token,
        bool forceFreshDownload = false, string? installedGameDirectory = null, string? cacheRoot = null)
    {
        // Locks and pending commands belong to the shared Steam output, not a selectable cache.
        // A cancelled observer does not cancel Steam's writer, including across app restarts.
        var commandDirectory = Path.GetDirectoryName(GetSteamDepotDirectory(session, depot))!;
        DownloadPathSafety.EnsureNoLinks(commandDirectory);
        Directory.CreateDirectory(commandDirectory);
        var commandLockPath = Path.Combine(commandDirectory, $".svp-depot-{depot.DepotId}.lock");
        var pendingPath = Path.Combine(commandDirectory, $".svp-pending-{depot.DepotId}.json");
        DownloadPathSafety.EnsureNoLinks(commandLockPath);
        DownloadPathSafety.EnsureNoLinks(pendingPath);
        await using var commandLock = new FileStream(commandLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await ResolvePendingCommandAsync(pendingPath, session, depot, token).ConfigureAwait(false);
        // The renderer/tab may disappear while the previous depot downloads or its files are verified.
        // Restore it BEFORE recording history and log offsets: reopening can replay old completions.
        await bridge.PrepareConsoleAsync(session, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var readySession = bridge.GetSession();
        ValidateSession(readySession);
        RequireSameSessionIdentity(session, readySession);
        var previousConsole = await bridge.ReadConsoleAsync(session, token).ConfigureAwait(false);
        // Console preparation is restricted to the existing process. A changed PID cannot
        // establish a trusted baseline or authorize a command in another Steam instance.
        var preparedSession = bridge.GetSession();
        ValidateSession(preparedSession);
        RequireSameSessionIdentity(session, preparedSession);
        session = preparedSession;
        if (forceFreshDownload)
            ClearFreshSteamDepotDirectory(session, depot, installedGameDirectory, cacheRoot!, progress, token);
        // Opening the existing console may replay historic success in Steam's logs.
        // Only lines appended after preparation can acknowledge the command below.
        var tails = new[] { "console_log.txt", "content_log.txt" }
            .Select(name => new LogTail(Path.Combine(session.SteamDirectory, "logs", name))).ToArray();
        var observation = new SteamConsoleObservation(depot);
        void AcceptObservedLine(string line)
        {
            try { observation.Accept(line); }
            catch (IOException) when (observation.Failed)
            {
                File.Delete(pendingPath); // A terminal Steam failure also proves that its writer stopped.
                throw;
            }
        }
        var dispatchedUtc = DateTimeOffset.UtcNow;
        var acknowledgementDeadline = dispatchedUtc + CommandAcknowledgementTimeout;
        DateTimeOffset? offlineSince = null;
        progress?.Report(new($"Steam: загрузка депо {depot.DepotId}, манифест {depot.ManifestId}…"));
        if (forceFreshDownload)
        {
            var output = GetSteamDepotDirectory(session, depot);
            DownloadPathSafety.EnsureNoLinks(output);
            if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException("Папка Steam снова содержит файлы до чистой загрузки. Повторите установку; игра не изменена.");
        }
        await WritePendingCommandAsync(pendingPath, new(1, depot, session,
            tails.Select(tail => tail.Checkpoint).ToArray()), token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            File.Delete(pendingPath); // No dispatch has been attempted yet.
            token.ThrowIfCancellationRequested();
        }
        await bridge.DispatchDepotAsync(session, depot, token).ConfigureAwait(false);
        while (observation.Completion is null)
        {
            token.ThrowIfCancellationRequested();
            var current = bridge.GetSession();
            ValidateSession(current, requireOnline: false);
            RequireSameSessionIdentity(session, current);
            foreach (var tail in tails)
                foreach (var line in await tail.ReadNewLinesAsync(token).ConfigureAwait(false)) AcceptObservedLine(line);
            var console = observation.Completion is null && current.Online is not false
                ? await bridge.ReadConsoleAsync(session, token).ConfigureAwait(false) : null;
            var observedSession = bridge.GetSession();
            ValidateSession(observedSession, requireOnline: false);
            RequireSameSessionIdentity(session, observedSession);
            if (console is not null && previousConsole is null)
            {
                // The first accessible view may contain old completions from before our command.
                // Capture it as baseline; only later changes or fresh log lines can prove completion.
                previousConsole = console;
            }
            else if (console is not null && !console.Equals(previousConsole, StringComparison.Ordinal))
            {
                var delta = ConsoleDelta(previousConsole!, console);
                foreach (var line in delta.Split('\n').Select(s => s.TrimEnd('\r')))
                    AcceptObservedLine(line);
                previousConsole = console;
            }
            if (observation.Completion is not null) break;
            var now = DateTimeOffset.UtcNow;
            if (current.Online is false || observedSession.Online is false)
            {
                if (offlineSince is null)
                {
                    offlineSince = now;
                    progress?.Report(new($"Steam потерял подключение к сети. Ожидается восстановление загрузки депо {depot.DepotId}…"));
                }
            }
            else if (offlineSince is { } disconnectedAt)
            {
                acknowledgementDeadline += now - disconnectedAt;
                offlineSince = null;
                progress?.Report(new($"Подключение Steam восстановлено. Ожидание депо {depot.DepotId} продолжается."));
            }
            var elapsed = now - dispatchedUtc;
            if (!observation.Acknowledged && offlineSince is null && now > acknowledgementDeadline)
                throw new SteamConsoleUnavailableException("Steam не подтвердил запуск загрузки. Проверьте подключение Steam к сети и повторите установку. Файлы игры не изменены.",
                    SteamConsoleCommands.ForDepot(depot));
            if (elapsed > DepotTimeout)
                throw new TimeoutException($"Ожидание депо {depot.DepotId} превысило {DepotTimeout.TotalHours:0.#} ч. Проверьте подключение Steam к сети и повторите установку. Файлы игры не изменены.");
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }
        var completion = observation.Completion;
        var source = ValidateCompletionDirectory(session, depot, completion.Directory);
        DownloadPathSafety.EnsureNoLinks(pendingPath);
        File.Delete(pendingPath); // Exact completion proves Steam stopped writing this requested depot.
        var expected = TryReadSteamInventory(session, depot, progress);
        var files = await CreateInventoryAsync(source, expected, progress, token).ConfigureAwait(false);
        if (completion.FileCount is { } expectedCount && files.Count != expectedCount)
            throw new InvalidDataException($"Steam сообщил о {expectedCount} файлах депо {depot.DepotId}, найдено {files.Count}. " +
                $"В папке могли остаться файлы другой версии: {source}. Переместите её в отдельную папку и повторите загрузку; игру патчер ещё не изменял.");
        if (completion.FileCount is null && expected is null)
            throw new InvalidDataException($"Steam не сообщил количество файлов депо {depot.DepotId}, а его манифест недоступен для проверки. " +
                "Автоматическая установка остановлена. Повторите установку в открытом клиенте Steam; файлы игры не изменены.");
        if (forceFreshDownload)
        {
            // The source is our newly requested Steam depot, never the installed game.
            // Steam may leave a file tail; only a manifest-verified prefix authorizes truncation.
            if (expected is not null)
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = DownloadPathSafety.ResolvePayloadPath(source, file.RelativePath);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                    if (stream.Length > file.Length) stream.SetLength(file.Length);
                }
            }
            await VerifyRecordedFilesAsync(source, files, token).ConfigureAwait(false);
            Directory.CreateDirectory(depotRoot);
            await WriteReceiptAsync(depotRoot, depot, files, token).ConfigureAwait(false);
            return files;
        }
        return await StoreVerifiedDepotAsync(source, depotRoot, depot, files, progress, token,
            allowTrailingBytes: expected is not null).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DownloadedFile>?> TryImportExistingDepotAsync(DownloadRequest request,
        DepotManifest depot, string depotRoot, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var roots = (steamDirectories ?? SteamClientLocator.FindSteamDirectories)();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.ExistingFilesDirectory))
        {
            try
            {
                var local = LocalDepotResolver.ResolveSources(request.ExistingFilesDirectory, [depot]);
                foreach (var source in local.Where(source =>
                    string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), "depot_" + depot.DepotId, StringComparison.OrdinalIgnoreCase)))
                    sources.Add(source);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                progress?.Report(new($"Прежняя папка депо {depot.DepotId} недоступна для проверки; используется загрузка Steam. {error.Message}"));
            }
        }
        foreach (var root in roots)
            sources.Add(Path.Combine(root, "steamapps", "content", "app_489830", "depot_" + depot.DepotId));
        foreach (var steam in roots)
        {
            token.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(steam, "depotcache", $"{depot.DepotId}_{depot.ManifestId}.manifest");
            IReadOnlyList<SteamManifestFile> expected;
            try
            {
                DownloadPathSafety.EnsureNoLinks(manifestPath);
                if (!File.Exists(manifestPath)) continue;
                using var manifest = File.OpenRead(manifestPath);
                expected = SteamManifestReader.Read(manifest, depot.DepotId, ulong.Parse(depot.ManifestId, CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
            {
                progress?.Report(new($"Ранее скачанное депо {depot.DepotId} нельзя подтвердить по манифесту: {error.Message} При необходимости Steam загрузит его заново."));
                continue;
            }
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(source)) continue;
                var commandDirectory = Path.GetDirectoryName(source)!;
                var commandLockPath = Path.Combine(commandDirectory, $".svp-depot-{depot.DepotId}.lock");
                var pendingPath = Path.Combine(commandDirectory, $".svp-pending-{depot.DepotId}.json");
                var isSteamOutput = roots.Any(root => SamePath(source,
                    Path.Combine(root, "steamapps", "content", "app_489830", "depot_" + depot.DepotId)));
                FileStream? sourceLock = null;
                if (isSteamOutput)
                {
                    DownloadPathSafety.EnsureNoLinks(commandLockPath);
                    DownloadPathSafety.EnsureNoLinks(pendingPath);
                    sourceLock = new FileStream(commandLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                await using var heldSourceLock = sourceLock;
                if (isSteamOutput && File.Exists(pendingPath))
                {
                    progress?.Report(new($"Прежняя загрузка депо {depot.DepotId} требует подтверждения завершения; её файлы пока не используются."));
                    continue;
                }
                IReadOnlyList<DownloadedFile> files;
                try
                {
                    DownloadPathSafety.EnsureNoLinks(source);
                    if (!Directory.Exists(source)) continue;
                    files = await CreateInventoryAsync(source, expected, progress, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    progress?.Report(new($"Ранее скачанные файлы депо {depot.DepotId} не прошли проверку: {error.Message} При необходимости Steam загрузит их заново."));
                    continue;
                }
                // Do not treat disk or copy failures as a verification failure and silently redownload.
                QuarantineInvalidCache(request.CacheDirectory, depotRoot, depot, progress);
                progress?.Report(new($"Депо {depot.DepotId} уже скачано и соответствует выбранному манифесту; повторная загрузка не нужна."));
                return await StoreVerifiedDepotAsync(source, depotRoot, depot, files, progress, token,
                    allowTrailingBytes: true).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<DownloadedFile>> ImportLocalDepotAsync(DownloadRequest request,
        DepotManifest depot, string source, string depotRoot, IProgress<DownloadProgress>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DownloadPathSafety.EnsureNoLinks(source);
        IReadOnlyList<DownloadedFile> files;
        var sourceDepotRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))!;
        var hasReceipt = Path.GetFileName(Path.TrimEndingDirectorySeparator(source))
            .Equals("content", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(sourceDepotRoot, "complete.json"));
        if (hasReceipt)
        {
            files = await ReadVerifiedCacheAsync(sourceDepotRoot, depot, progress, token).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Свои файлы депо {depot.DepotId}/{depot.ManifestId} повреждены или неполны. " +
                    "Выберите папку с полным проверенным комплектом; игра не изменена.");
            if (SamePath(sourceDepotRoot, depotRoot)) return files;
        }
        else
        {
            var expected = ReadLocalManifest(request.ExistingFilesDirectory!, source, depot, progress, token);
            try
            {
                files = await CreateInventoryAsync(source, expected, progress, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidDataException($"Свои файлы депо {depot.DepotId}/{depot.ManifestId} не прошли проверку. " +
                    "Укажите полный комплект файлов выбранной версии; игра не изменена.\n" + error.Message, error);
            }
        }
        // A selected depot is the sole payload source. Local mode never opens the Steam client
        // or substitutes files from its download folders or the installed game.
        if (PathsOverlap(source, depotRoot))
            throw new ArgumentException("Свои depot-файлы и кэш подготовки должны находиться в независимых папках.");
        QuarantineInvalidCache(request.CacheDirectory, depotRoot, depot, progress);
        progress?.Report(new($"Импорт своих файлов депо {depot.DepotId}, манифест {depot.ManifestId}…"));
        return await StoreVerifiedDepotAsync(source, depotRoot, depot, files, progress, token,
            allowTrailingBytes: !hasReceipt).ConfigureAwait(false);
    }

    private IReadOnlyList<SteamManifestFile> ReadLocalManifest(string selectedDirectory, string source,
        DepotManifest depot, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var name = $"{depot.DepotId}_{depot.ManifestId}.manifest";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startingDirectory in new[] { selectedDirectory, source })
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startingDirectory));
            roots.Add(directory);
            // Only ascend through the conventional public Steam depot containers.
            // A freely chosen folder does not authorize searching unrelated parent folders.
            while (Path.GetFileName(directory) is { } container &&
                (container.StartsWith("depot_", StringComparison.OrdinalIgnoreCase) ||
                 container.Equals("app_489830", StringComparison.OrdinalIgnoreCase) ||
                 container.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                 container.Equals("steamapps", StringComparison.OrdinalIgnoreCase)))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null) break;
                directory = parent;
                roots.Add(directory);
            }
        }
        var paths = roots.SelectMany(root => new[]
        {
            Path.Combine(root, name), Path.Combine(root, "depotcache", name), Path.Combine(root, ".DepotDownloader", name)
        }).Concat(PublicSteamManifestPaths(name)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                DownloadPathSafety.EnsureNoLinks(path);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                return SteamManifestReader.Read(stream, depot.DepotId, ulong.Parse(depot.ManifestId, CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
            {
                progress?.Report(new($"Манифест своих файлов депо {depot.DepotId} недоступен для проверки: {error.Message}"));
            }
        }
        throw new InvalidDataException($"Для проверки своих файлов нужен открытый манифест {name}. " +
            "Поместите его в выбранную папку или её depotcache, либо укажите проверенный кэш патчера. Игра не изменена.");
    }

    private IEnumerable<string> PublicSteamManifestPaths(string name)
    {
        // Discovery is lazy: supplied manifests make even Steam directory lookup unnecessary.
        foreach (var root in (steamDirectories ?? SteamClientLocator.FindSteamDirectories)())
            yield return Path.Combine(root, "depotcache", name);
    }

    private async Task<IReadOnlyList<DownloadedFile>?> TryReuseInstalledDepotAsync(DownloadRequest request,
        DepotManifest depot, string depotRoot, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.InstalledGameDirectory)) return null;
        var game = Path.GetFullPath(request.InstalledGameDirectory);
        var roots = (steamDirectories ?? SteamClientLocator.FindSteamDirectories)();
        IReadOnlyList<SteamChunkFile>? target = null;
        var sourceManifests = new List<SteamChunkFile>();
        var preparing = false;
        try
        {
            DownloadPathSafety.EnsureNoLinks(game);
            if (!Directory.Exists(game)) return null;
            foreach (var steam in roots)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(steam, "depotcache", $"{depot.DepotId}_{depot.ManifestId}.manifest");
                try
                {
                    DownloadPathSafety.EnsureNoLinks(path);
                    if (!File.Exists(path)) continue;
                    using var stream = File.OpenRead(path);
                    var parsed = SteamManifestChunkReader.Read(stream, depot.DepotId, ulong.Parse(depot.ManifestId, CultureInfo.InvariantCulture));
                    if (parsed.Count == 0) throw new InvalidDataException("В целевом манифесте нет файлов.");
                    target = parsed;
                    break;
                }
                catch (Exception error) when (IsReuseReadError(error))
                {
                    progress?.Report(new($"Манифест блоков депо {depot.DepotId} недоступен: {error.Message}"));
                }
            }
            if (target is null) return null;

            // A target manifest alone is enough to verify exact files and unchanged ranges.
            // Other public manifests of this same Skyrim depot describe ranges that moved inside a BSA.
            long scannedBytes = 0;
            var scannedFiles = 0;
            foreach (var steam in roots)
            {
                var directory = Path.Combine(steam, "depotcache");
                try
                {
                    DownloadPathSafety.EnsureNoLinks(directory);
                    if (!Directory.Exists(directory)) continue;
                    foreach (var path in Directory.EnumerateFiles(directory, $"{depot.DepotId}_*.manifest").Take(17))
                    {
                        token.ThrowIfCancellationRequested();
                        if (scannedFiles >= 16) break;
                        var name = Path.GetFileNameWithoutExtension(path);
                        var text = name[(depot.DepotId.ToString(CultureInfo.InvariantCulture).Length + 1)..];
                        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) || manifest == 0 ||
                            manifest.ToString(CultureInfo.InvariantCulture) != text || text == depot.ManifestId) continue;
                        try
                        {
                            DownloadPathSafety.EnsureNoLinks(path);
                            var length = new FileInfo(path).Length;
                            if (length > 128 * 1024 * 1024 - scannedBytes) continue;
                            scannedBytes += length;
                            scannedFiles++;
                            using var stream = File.OpenRead(path);
                            sourceManifests.AddRange(SteamManifestChunkReader.Read(stream, depot.DepotId, manifest));
                        }
                        catch (Exception error) when (IsReuseReadError(error))
                        {
                            progress?.Report(new($"Исходный манифест блоков депо {depot.DepotId} пропущен: {error.Message}"));
                        }
                    }
                }
                catch (Exception error) when (IsReuseReadError(error))
                {
                    progress?.Report(new($"Исходные манифесты депо {depot.DepotId} недоступны: {error.Message}"));
                }
                if (scannedFiles >= 16) break;
            }
            progress?.Report(new($"Проверка пригодных блоков установленной игры для депо {depot.DepotId}…"));
            var sources = new List<SteamChunkSource> { new(game, target) };
            if (sourceManifests.Count != 0) sources.Add(new(game, sourceManifests));
            var plan = await SteamChunkReuseService.AnalyzeAsync(target, sources, token).ConfigureAwait(false);
            if (!plan.IsComplete)
            {
                progress?.Report(new($"Депо {depot.DepotId}: локально подтверждено {plan.ReusedBytes:N0} из {plan.TotalBytes:N0} байт. " +
                    "Клиент Steam загружает целое депо; для недостающих блоков будет запрошено полное депо."));
                return null;
            }
            var staging = depotRoot + ".reuse." + Guid.NewGuid().ToString("N");
            preparing = true;
            try
            {
                var files = await SteamChunkReuseService.PrepareAsync(plan, staging, progress, token).ConfigureAwait(false);
                QuarantineInvalidCache(request.CacheDirectory, depotRoot, depot, progress);
                DownloadPathSafety.EnsureNoLinks(depotRoot);
                Directory.CreateDirectory(depotRoot);
                var content = Path.Combine(depotRoot, "content");
                DownloadPathSafety.EnsureNoLinks(content);
                Directory.Move(staging, content);
                await VerifyRecordedFilesAsync(content, files, token).ConfigureAwait(false);
                await WriteReceiptAsync(depotRoot, depot, files, token).ConfigureAwait(false);
                progress?.Report(new($"Депо {depot.DepotId} собрано из проверенных блоков установленной игры; загрузка не нужна."));
                return files;
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
            {
                progress?.Report(new($"Локальные блоки депо {depot.DepotId} изменились при подготовке: {error.Message} Используется загрузка Steam."));
                return null;
            }
            finally
            {
                if (Directory.Exists(staging)) QuarantineInvalidCache(request.CacheDirectory, staging, depot, progress);
            }
        }
        catch (Exception error) when (!preparing && IsReuseReadError(error))
        {
            progress?.Report(new($"Повторное использование установленного депо {depot.DepotId} недоступно: {error.Message} Используется загрузка Steam."));
            return null;
        }
    }

    private long? TryGetTargetByteCount(DepotManifest depot, IProgress<DownloadProgress>? progress)
    {
        foreach (var steam in (steamDirectories ?? SteamClientLocator.FindSteamDirectories)())
        {
            var path = Path.Combine(steam, "depotcache", $"{depot.DepotId}_{depot.ManifestId}.manifest");
            try
            {
                DownloadPathSafety.EnsureNoLinks(path);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                return SteamManifestReader.Read(stream, depot.DepotId, ulong.Parse(depot.ManifestId, CultureInfo.InvariantCulture))
                    .Aggregate(0L, (sum, file) => checked(sum + file.Length));
            }
            catch (Exception error) when (IsReuseReadError(error))
            {
                progress?.Report(new($"Размер целевого депо {depot.DepotId} пока неизвестен: {error.Message}"));
            }
        }
        return null;
    }

    private static bool IsReuseReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException;

    private static async Task<IReadOnlyList<DownloadedFile>> StoreVerifiedDepotAsync(string source, string depotRoot,
        DepotManifest depot, IReadOnlyList<DownloadedFile> files, IProgress<DownloadProgress>? progress, CancellationToken token,
        bool allowTrailingBytes)
    {
        var content = Path.Combine(depotRoot, "content");
        DownloadPathSafety.EnsureNoLinks(content);
        var required = files.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        var available = new DriveInfo(Path.GetPathRoot(content)!).AvailableFreeSpace;
        if (available < required)
            throw new IOException($"Для кэша депо {depot.DepotId} нужно {required:N0} байт, доступно {available:N0}. Освободите место в выбранном хранилище и повторите загрузку.");
        Directory.CreateDirectory(content);
        var buffer = new byte[128 * 1024];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var sourcePath = DownloadPathSafety.ResolvePayloadPath(source, file.RelativePath);
            var destination = DownloadPathSafety.ResolvePayloadPath(content, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            if (!allowTrailingBytes)
            {
                // Without a manifest, every source byte must still match the full inventory.
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                continue;
            }
            var remaining = file.Length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Скачанный файл оборван при копировании: " + file.RelativePath);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }
        // Verify the copy too: another Steam command must not silently change source files mid-copy.
        await VerifyRecordedFilesAsync(content, files, token).ConfigureAwait(false);
        await WriteReceiptAsync(depotRoot, depot, files, token).ConfigureAwait(false);
        progress?.Report(new($"Депо {depot.DepotId} сохранено и проверено: {files.Count} файлов."));
        return files;
    }

    private static async Task WriteReceiptAsync(string depotRoot, DepotManifest depot,
        IReadOnlyList<DownloadedFile> files, CancellationToken token)
    {
        var receipt = new DepotDownloadReceipt(1, SteamConsoleCommands.AppId, depot.DepotId, depot.ManifestId, DateTimeOffset.UtcNow, files);
        var receiptPath = Path.Combine(depotRoot, "complete.json");
        var temporary = receiptPath + ".tmp";
        DownloadPathSafety.EnsureNoLinks(receiptPath);
        DownloadPathSafety.EnsureNoLinks(temporary);
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt, JsonOptions), token).ConfigureAwait(false);
        File.Move(temporary, receiptPath, true);
    }

    private static IReadOnlyList<SteamManifestFile>? TryReadSteamInventory(SteamClientSession session, DepotManifest depot,
        IProgress<DownloadProgress>? progress)
    {
        var manifestPath = Path.Combine(session.SteamDirectory, "depotcache", $"{depot.DepotId}_{depot.ManifestId}.manifest");
        try
        {
            DownloadPathSafety.EnsureNoLinks(manifestPath);
            if (!File.Exists(manifestPath)) return null;
            using var stream = File.OpenRead(manifestPath);
            return SteamManifestReader.Read(stream, depot.DepotId, ulong.Parse(depot.ManifestId, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException)
        {
            // Steam stores encrypted CDN manifests on some client versions. Never read keys or saved credentials.
            progress?.Report(new($"Манифест депо {depot.DepotId} зашифрован или повреждён; результат проверяется по подтверждению Steam и SHA256 файлов. {ex.Message}"));
            return null;
        }
    }

    public static string GetSteamDepotDirectory(SteamClientSession session, DepotManifest depot)
    {
        SteamConsoleCommands.ValidateDepot(depot);
        return Path.Combine(Path.GetFullPath(session.SteamDirectory), "steamapps", "content", "app_489830", "depot_" + depot.DepotId);
    }

    private static string ValidateFreshSteamDepotDirectory(SteamClientSession session, DepotManifest depot,
        string? installedGameDirectory, string cacheRoot)
    {
        var source = GetSteamDepotDirectory(session, depot);
        DownloadPathSafety.EnsureNoLinks(source);
        if (PathsOverlap(source, Path.GetFullPath(cacheRoot)) ||
            !string.IsNullOrWhiteSpace(installedGameDirectory) && PathsOverlap(source, Path.GetFullPath(installedGameDirectory)))
            throw new ArgumentException("Папка загрузки Steam, папка игры и хранилище установщика должны находиться независимо друг от друга.");
        if (File.Exists(source)) throw new IOException("Путь загрузки Steam занят файлом: " + source);
        return source;
    }

    private static void ClearFreshSteamDepotDirectory(SteamClientSession session, DepotManifest depot,
        string? installedGameDirectory, string cacheRoot, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var source = ValidateFreshSteamDepotDirectory(session, depot, installedGameDirectory, cacheRoot);
        if (!Directory.Exists(source)) return;
        var prefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        var directories = new List<string>();
        var files = new List<string>();
        pending.Push(source);
        // Inspect the whole tree first. A linked descendant must stop cleanup before
        // any old depot file is removed, even when that descendant is not payload.
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            DownloadPathSafety.EnsureNoLinks(directory);
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var full = Path.GetFullPath(entry);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Путь очистки выходит за пределы выбранного депо Steam.");
                DownloadPathSafety.EnsureNoLinks(full);
                if (Directory.Exists(full)) pending.Push(full);
                else files.Add(full);
            }
        }
        progress?.Report(new($"Чистая загрузка: удаление прежних файлов депо {depot.DepotId} из {source}…"));
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            DownloadPathSafety.EnsureNoLinks(file);
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        foreach (var directory in directories.AsEnumerable().Reverse())
        {
            token.ThrowIfCancellationRequested();
            DownloadPathSafety.EnsureNoLinks(directory);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static string ValidateCompletionDirectory(SteamClientSession session, DepotManifest depot, string reported)
    {
        var source = Path.GetFullPath(reported);
        var expected = GetSteamDepotDirectory(session, depot);
        if (!Path.TrimEndingDirectorySeparator(source).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam сообщил неожиданный путь загрузки: " + source);
        DownloadPathSafety.EnsureNoLinks(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Steam сообщил о завершении, но папка депо отсутствует: " + source);
        return source;
    }

    private static void ValidateSession(SteamClientSession session, bool requireOnline = true)
    {
        if (session.ActiveUser == 0 || session.ProcessId <= 0 || requireOnline && session.Online is false)
            throw new InvalidOperationException("Для загрузки нужен запущенный клиент Steam с активным входом и подключением к сети.");
    }

    private static void RequireSameSessionIdentity(SteamClientSession original, SteamClientSession current)
    {
        if (current.ActiveUser != original.ActiveUser ||
            !SamePath(current.SteamDirectory, original.SteamDirectory) || !SamePath(current.ExecutablePath, original.ExecutablePath) ||
            current.ProcessId != original.ProcessId || original.ProcessStartedUtc is { } originalStart &&
                current.ProcessStartedUtc is { } currentStart && originalStart != currentStart)
            throw new InvalidOperationException("Сеанс Steam изменился во время подготовки или загрузки. Повторите установку с тем же активным пользователем Steam.");
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private sealed record PendingSteamDepot(int SchemaVersion, DepotManifest Depot, SteamClientSession Session,
        IReadOnlyList<SteamLogCheckpoint> Logs);

    private sealed record SteamLogCheckpoint(string Name, long Offset, DateTime? CreationUtc, string? AnchorSha256,
        bool AtLineBoundary = true);

    private static async Task WritePendingCommandAsync(string path, PendingSteamDepot pending, CancellationToken token)
    {
        var temporary = path + ".tmp";
        DownloadPathSafety.EnsureNoLinks(path);
        DownloadPathSafety.EnsureNoLinks(temporary);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, pending, cancellationToken: token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true); // The record must exist before an uncertain dispatch or app crash.
        }
        File.Move(temporary, path, overwrite: false);
    }

    private async Task ResolvePendingCommandAsync(string path, SteamClientSession current,
        DepotManifest selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;
        PendingSteamDepot pending;
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > 64 * 1024) throw new InvalidDataException("Слишком большой журнал незавершённой команды Steam.");
            pending = await JsonSerializer.DeserializeAsync<PendingSteamDepot>(stream, cancellationToken: token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Пустой журнал незавершённой команды Steam.");
            if (pending.SchemaVersion != 1 || pending.Depot is null || pending.Session is null ||
                pending.Depot.DepotId != selected.DepotId || pending.Session.ProcessId <= 0 ||
                !SamePath(pending.Session.SteamDirectory, current.SteamDirectory) ||
                !SamePath(pending.Session.ExecutablePath, current.ExecutablePath) ||
                pending.Logs is not { Count: 2 } ||
                pending.Logs.Select(log => log.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                pending.Logs.Any(log => log.Name is not ("console_log.txt" or "content_log.txt") || log.Offset < 0 ||
                    log.AnchorSha256 is not null && !SteamChunkReuseService.IsHash(log.AnchorSha256, 64)))
                throw new InvalidDataException("Журнал незавершённой команды Steam повреждён.");
            SteamConsoleCommands.ValidateDepot(pending.Depot);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NullReferenceException)
        {
            throw new SteamConsoleUnavailableException("Не удалось проверить прежнюю команду Steam. " +
                "Папка депо сохранена; новая загрузка не запущена. " + error.Message);
        }
        if (PendingProcessHasEnded(pending.Session, current))
        {
            File.Delete(path);
            return;
        }
        RequireSameSessionIdentity(pending.Session, current);
        void RequireCurrentSession()
        {
            var observed = bridge.GetSession();
            ValidateSession(observed, requireOnline: false);
            RequireSameSessionIdentity(current, observed);
        }
        // Read before opening the console: preparation can append replayed historic success.
        // Truncated/replaced logs or absent baselines cannot prove that the old writer finished.
        foreach (var checkpoint in pending.Logs)
        {
            // An acknowledgement in one log must not authorize an unrelated failure in another.
            var observation = new SteamConsoleObservation(pending.Depot);
            var log = Path.Combine(current.SteamDirectory, "logs", checkpoint.Name);
            var tail = new LogTail(log, checkpoint);
            foreach (var line in await tail.ReadNewLinesAsync(token).ConfigureAwait(false))
            {
                try { observation.Accept(line); }
                catch (IOException) when (observation.Failed)
                {
                    RequireCurrentSession();
                    File.Delete(path);
                    return;
                }
            }
            if (observation.Completion is { } completion)
            {
                RequireCurrentSession();
                ValidateCompletionDirectory(current, pending.Depot, completion.Directory);
                File.Delete(path);
                return;
            }
        }
        throw new SteamConsoleUnavailableException($"Прежняя загрузка депо {selected.DepotId} ещё не подтверждена как завершённая. " +
            "Дождитесь её завершения в Steam и повторите установку. Папка депо сохранена; новая команда не отправлена.");
    }

    private static bool PendingProcessHasEnded(SteamClientSession pending, SteamClientSession current)
    {
        if (pending.ProcessId == current.ProcessId)
            return pending.ProcessStartedUtc is { } original && current.ProcessStartedUtc is { } now && original != now;
        try
        {
            using var process = Process.GetProcessById(pending.ProcessId);
            if (process.HasExited) return true;
            if (pending.ProcessStartedUtc is { } started && new DateTimeOffset(process.StartTime).ToUniversalTime() != started)
                return true; // PID was reused; the recorded writer no longer exists.
            return WindowsProcessIdentity.TryGetExecutablePath(pending.ProcessId, out var executable, out _) &&
                !SamePath(executable, pending.ExecutablePath);
        }
        catch (ArgumentException) { return true; } // No live process has this PID.
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return false; } // Unknown ownership or lifetime never authorizes deleting Steam's output.
    }

    private static async Task<IReadOnlyList<DownloadedFile>?> ReadVerifiedCacheAsync(string depotRoot, DepotManifest depot,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var receiptPath = Path.Combine(depotRoot, "complete.json");
        DownloadPathSafety.EnsureNoLinks(receiptPath);
        if (!File.Exists(receiptPath)) return null;
        try
        {
            await using var stream = File.OpenRead(receiptPath);
            if (stream.Length > MaximumReceiptBytes)
                throw new InvalidDataException("Квитанция кэша превышает допустимый размер 16 МБ.");
            var receipt = await JsonSerializer.DeserializeAsync<DepotDownloadReceipt>(stream, cancellationToken: token).ConfigureAwait(false);
            if (receipt?.Files is { Count: > MaximumReceiptFiles })
                throw new InvalidDataException("Квитанция кэша содержит слишком много файлов: максимум 100 000.");
            if (receipt is null || receipt.SchemaVersion != 1 || receipt.AppId != SteamConsoleCommands.AppId ||
                receipt.DepotId != depot.DepotId || receipt.ManifestId != depot.ManifestId || receipt.Files is not { Count: > 0 and <= MaximumReceiptFiles })
                throw new InvalidDataException("Квитанция не соответствует выбранному депо/манифесту.");
            progress?.Report(new($"Проверка кэша депо {depot.DepotId}…"));
            await VerifyRecordedFilesAsync(Path.Combine(depotRoot, "content"), receipt.Files, token).ConfigureAwait(false);
            return receipt.Files;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NullReferenceException)
        {
            progress?.Report(new($"Кэш депо {depot.DepotId} повреждён или неполон и не будет использован: {ex.Message}"));
            return null;
        }
    }

    private static void QuarantineInvalidCache(string cacheRoot, string depotRoot, DepotManifest depot, IProgress<DownloadProgress>? progress)
    {
        if (!Directory.Exists(depotRoot)) return;
        DownloadPathSafety.EnsureNoLinks(depotRoot);
        var quarantine = Path.Combine(cacheRoot, "corrupt", $"{depot.DepotId}_{depot.ManifestId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}");
        DownloadPathSafety.EnsureNoLinks(quarantine);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(depotRoot).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(quarantine).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Папка повреждённого кэша выходит за пределы хранилища.");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
        Directory.Move(depotRoot, quarantine);
        progress?.Report(new("Прежний неполный кэш сохранён: " + quarantine));
    }

    private static async Task VerifyRecordedFilesAsync(string root, IReadOnlyList<DownloadedFile> files, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || !names.Add(file.RelativePath) || file.Length < 0 || file.Sha256 is not { Length: 64 } ||
                file.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Некорректная квитанция кэша.");
            var path = DownloadPathSafety.ResolvePayloadPath(root, file.RelativePath);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                    .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл кэша изменился: " + file.RelativePath);
        }
        if (!EnumeratePayload(root).Select(p => Path.GetRelativePath(root, p)).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(names))
            throw new InvalidDataException("В кэше есть лишние или недостающие файлы.");
    }

    private static async Task<IReadOnlyList<DownloadedFile>> CreateInventoryAsync(string root,
        IReadOnlyList<SteamManifestFile>? expected, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var expectedMap = expected?.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var payload = EnumeratePayload(root).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expectedMap is not null)
        {
            var present = payload.Select(path => Path.GetRelativePath(root, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expectedMap.Keys.Where(path => !present.Contains(path)).ToArray();
            if (missing.Length != 0)
                throw new InvalidDataException($"В скачанной папке отсутствуют {missing.Length} файлов манифеста Steam: " +
                    string.Join(", ", missing.Take(5)) + ". Повторите загрузку депо; файлы игры не изменены.");
            // download_depot reuses a folder for every manifest of a depot and can leave
            // files removed in the older version. Only the verified target inventory is
            // copied into our independent cache; the raw Steam folder stays untouched.
            var rawCount = payload.Length;
            payload = payload.Where(path => expectedMap.ContainsKey(Path.GetRelativePath(root, path))).ToArray();
            if (payload.Length != expectedMap.Count)
                throw new InvalidDataException("Неоднозначный набор целевых файлов в скачанной папке.");
            if (rawCount > payload.Length)
                progress?.Report(new($"В Steam-папке найдены {rawCount - payload.Length} файлов вне целевого манифеста. " +
                    "Они сохранятся в исходной папке и не попадут в кэш выбранной версии."));
        }
        if (payload.Length == 0) throw new InvalidDataException("В скачанном депо нет файлов.");
        var files = new List<DownloadedFile>(payload.Length);
        var buffer = new byte[1024 * 1024];
        foreach (var path in payload)
        {
            var relative = Path.GetRelativePath(root, path);
            string? trailingBytesMessage = null;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                SteamManifestFile? expectedFile = null;
                if (expectedMap is not null && !expectedMap.TryGetValue(relative, out expectedFile))
                    throw new InvalidDataException("Файл отсутствует в манифесте Steam: " + relative);
                var length = expectedFile?.Length ?? stream.Length;
                if (stream.Length < length)
                    throw new InvalidDataException($"Файл не соответствует манифесту Steam: {relative}. Ожидается {length:N0} байт, найдено {stream.Length:N0}.");
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                // Steam can overwrite an older, larger depot file without truncating its tail.
                // Accept only the manifest's exact byte range after its SHA1 has been verified;
                // the independent cache copies that range and leaves Steam's source untouched.
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Скачанный файл оборван при проверке: " + relative);
                    sha1.AppendData(buffer, 0, read); sha256.AppendData(buffer, 0, read);
                    remaining -= read;
                }
                if (expectedFile is not null && !Convert.ToHexString(sha1.GetHashAndReset()).Equals(expectedFile.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файл не соответствует манифесту Steam: " + relative);
                if (expectedFile is not null && stream.Length > length)
                    trailingBytesMessage = $"Файл {relative} проверен по манифесту: {length:N0} байт. " +
                        $"Лишние {stream.Length - length:N0} байт в конце файла Steam не попадут в кэш.";
                files.Add(new(relative, length, Convert.ToHexString(sha256.GetHashAndReset())));
            }
            if (trailingBytesMessage is not null) progress?.Report(new(trailingBytesMessage));
            progress?.Report(new($"Проверка депо: {relative}", files.Count * 100d / payload.Length));
        }
        return files;
    }

    private static IEnumerable<string> EnumeratePayload(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            DownloadPathSafety.EnsureNoLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                DownloadPathSafety.EnsureNoLinks(entry);
                if (Directory.Exists(entry))
                {
                    if (!Path.GetFileName(entry).Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)) pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private static string ConsoleDelta(string previous, string current)
    {
        if (current.StartsWith(previous, StringComparison.Ordinal)) return current[previous.Length..];
        // UI consoles trim their oldest lines. Find the longest retained suffix rather than replaying old success lines.
        var previousLines = previous.Split('\n');
        for (var i = 1; i < previousLines.Length; i++)
        {
            var suffix = string.Join('\n', previousLines[i..]);
            if (suffix.Length > 0 && current.StartsWith(suffix, StringComparison.Ordinal)) return current[suffix.Length..];
        }
        // If accessibility reordered the tree, don't replay a historic completion. Treat it as a new baseline.
        return "";
    }

    private sealed class LogTail
    {
        private readonly string path;
        private readonly SteamLogCheckpoint? restored;
        private SteamLogCheckpoint continuity;
        private long offset;
        private string partial = "";
        private bool discardLeadingLine;
        private bool unreliable;
        internal SteamLogCheckpoint Checkpoint { get; }

        internal LogTail(string path, SteamLogCheckpoint? checkpoint = null)
        {
            this.path = path;
            restored = checkpoint;
            Checkpoint = checkpoint ?? CaptureCheckpoint(path);
            continuity = Checkpoint;
            offset = Checkpoint.Offset;
            discardLeadingLine = !Checkpoint.AtLineBoundary;
        }

        private static SteamLogCheckpoint CaptureCheckpoint(string path, long? position = null)
        {
            try
            {
                DownloadPathSafety.EnsureNoLinks(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var end = position ?? stream.Length;
                if (end > stream.Length) return new(Path.GetFileName(path), end, null, null);
                stream.Position = Math.Max(0, end - 4096);
                using var reader = new BinaryReader(stream);
                var anchorLength = (int)(end - stream.Position);
                var anchor = reader.ReadBytes(anchorLength);
                if (anchor.Length != anchorLength) return new(Path.GetFileName(path), end, DateTime.MinValue, null, false);
                return new(Path.GetFileName(path), end, File.GetCreationTimeUtc(path), Convert.ToHexString(SHA256.HashData(anchor)),
                    end == 0 || anchor[^1] == '\n');
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return new(Path.GetFileName(path), position ?? 0, null, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(Path.GetFileName(path), position ?? 0, DateTime.MinValue, null); }
        }

        public async Task<IReadOnlyList<string>> ReadNewLinesAsync(CancellationToken token)
        {
            try
            {
                if (unreliable) return [];
                if (!File.Exists(path)) return [];
                if (continuity.AnchorSha256 is null)
                {
                    if (restored is not null || continuity.CreationUtc is not null)
                    { unreliable = true; return []; }
                    continuity = CaptureCheckpoint(path, offset); // First creation of a previously absent log.
                }
                if (continuity.AnchorSha256 is null || CaptureCheckpoint(path, continuity.Offset) != continuity)
                { unreliable = true; return []; }
                DownloadPathSafety.EnsureNoLinks(path);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length < offset)
                { unreliable = true; return []; } // Rotation/truncation never replays historic terminal output.
                stream.Position = offset;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var added = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                offset = stream.Position;
                if (CaptureCheckpoint(path, continuity.Offset) != continuity)
                { unreliable = true; return []; }
                var nextCheckpoint = CaptureCheckpoint(path, offset);
                if (nextCheckpoint.AnchorSha256 is null || nextCheckpoint.CreationUtc != continuity.CreationUtc)
                { unreliable = true; return []; }
                continuity = nextCheckpoint;
                if (discardLeadingLine)
                {
                    var newline = added.IndexOf('\n');
                    if (newline < 0) return [];
                    added = added[(newline + 1)..];
                    discardLeadingLine = false;
                }
                var lines = (partial + added).Split('\n');
                partial = lines[^1];
                return lines[..^1].Select(s => s.TrimEnd('\r')).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }
}
