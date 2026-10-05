using SkyrimVersionPatcher.Core.Catalog;
using SkyrimVersionPatcher.Core.Compatibility;
using SkyrimVersionPatcher.Core.Downloading;
using SkyrimVersionPatcher.Core.Identity;
using SkyrimVersionPatcher.Core.Patching;
using SkyrimVersionPatcher.Core.Transitions;

namespace SkyrimVersionPatcher.Core.Workflow;

/// <summary>One operation: inspect interruptions, obtain verified depots, validate and install.</summary>
public sealed class AutomaticPatchService
{
    private readonly PatchEngine engine;
    private readonly Func<DownloadRequest, IProgress<DownloadProgress>?, CancellationToken, Task<DownloadResult>> download;
    private readonly Func<string, string?> getExecutableVersion;
    private readonly Func<string?>? getProfileDirectory;
    private readonly Func<string?>? getLocalizationProfileDirectory;
    private readonly Func<string, CancellationToken, Task<ExecutableIdentity>>? identifyExecutable;
    private readonly TransitionPackageStore transitionPackages;
    private int running;

    public AutomaticPatchService(PatchEngine engine,
        Func<DownloadRequest, IProgress<DownloadProgress>?, CancellationToken, Task<DownloadResult>> download,
        Func<string, string?> getExecutableVersion, Func<string?>? getProfileDirectory = null,
        Func<string, CancellationToken, Task<ExecutableIdentity>>? identifyExecutable = null,
        TransitionPackageStore? transitionPackages = null, Func<string?>? getLocalizationProfileDirectory = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.download = download ?? throw new ArgumentNullException(nameof(download));
        this.getExecutableVersion = getExecutableVersion ?? throw new ArgumentNullException(nameof(getExecutableVersion));
        this.getProfileDirectory = getProfileDirectory;
        this.getLocalizationProfileDirectory = getLocalizationProfileDirectory ?? getProfileDirectory;
        this.identifyExecutable = identifyExecutable;
        this.transitionPackages = transitionPackages ?? new TransitionPackageStore();
    }

    public async Task<AutomaticPatchResult> RunAsync(AutomaticPatchRequest request,
        IProgress<AutomaticPatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("Установка уже выполняется. Дождитесь её завершения.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMode(request);
            var game = CanonicalDirectory(request.GameDirectory, "Не найдена папка Skyrim.");
            var storage = CanonicalDirectory(request.StorageDirectory, "Не удалось определить рабочую папку.");
            if (!Directory.Exists(game))
                throw new DirectoryNotFoundException("Выбранная папка Skyrim Special Edition не найдена.");
            if (Overlaps(game, storage))
                throw new InvalidDataException("Папка игры и рабочая папка не должны находиться друг внутри друга.");
            DownloadPathSafety.EnsureNoLinks(game);
            DownloadPathSafety.EnsureNoLinks(storage);
            var cleanInstall = request.Mode == InstallationMode.SteamDepots;
            // A clean run has its own empty workspace. Never reuse an earlier payload,
            // even when the installed files or a verified cache already match.
            var downloadStorage = cleanInstall
                ? Path.Combine(storage, "Temp", "clean-" + Guid.NewGuid().ToString("N")) : storage;
            DownloadPathSafety.EnsureNoLinks(downloadStorage);
            var gameLanguage = request.SelectedGameLanguage;
            var depots = VersionCatalogService.GetDepots(request.Version,
                request.Mode == InstallationMode.RuntimeOnly ? GameLocalizationCatalog.English : gameLanguage);
            var diagnosticRoot = Path.Combine(storage, "Transactions");
            TransitionStatistics? statistics = null;
            var patchProgress = new ForwardProgress<PatchProgress>(value => progress?.Report(ToProgress(value, statistics)));

            var hasRetryDiagnostic = await InspectInterruptionsAsync(diagnosticRoot, game, progress, cancellationToken).ConfigureAwait(false);
            // A recorded interruption can leave the executable missing or empty. Verified files
            // from the selected full installation repair it during this retry.
            var executable = Path.Combine(game, "SkyrimSE.exe");
            var hasInstalledExecutable = File.Exists(executable) &&
                (request.Mode == InstallationMode.RuntimeOnly || !hasRetryDiagnostic || new FileInfo(executable).Length > 0);
            if (!hasInstalledExecutable && !hasRetryDiagnostic)
                throw new DirectoryNotFoundException("В выбранной папке отсутствует установленная Skyrim Special Edition.");
            var sourceIdentity = identifyExecutable is null || !hasInstalledExecutable ? null
                : await identifyExecutable(game, cancellationToken).ConfigureAwait(false);
            var sourceVersion = sourceIdentity?.Status == ExecutableIdentityStatus.KnownOfficial
                ? sourceIdentity.Version : hasInstalledExecutable ? getExecutableVersion(game) : null;
            TransitionPackageRecord? builtInPackage = null;
            if (request.Mode == InstallationMode.RuntimeOnly && request.TransitionBundlePath is null)
            {
                // Resolve the packaged pair from the installed executable's identity.
                // A missing source requires a full installation instead of a binary transition.
                var lookupVersion = NormalizeZeroRevision(sourceVersion);
                builtInPackage = lookupVersion is null ? null : transitionPackages.Find(lookupVersion,
                    request.Version.Id, "RuntimeOnly", false, depots);
                if (builtInPackage is null)
                    throw new InvalidDataException($"Готовый переход исполняемых файлов {sourceVersion ?? "неизвестная версия"} → {request.Version.Id} отсутствует. Выберите автоматическую установку полной версии.");
                request = request with { TransitionBundlePath = builtInPackage.Path, TransitionBundleSha256 = builtInPackage.Sha256 };
            }
            DownloadResult downloaded;
            if (request.Mode == InstallationMode.RuntimeOnly && request.TransitionBundlePath is { } bundlePath)
            {
                progress?.Report(new("PreparingTransition", "Проверка бинарного перехода…"));
                var metadata = await BinaryTransitionBundle.InspectAsync(bundlePath, request.TransitionBundleSha256!, cancellationToken).ConfigureAwait(false);
                ValidateBundleIdentity(metadata.Identity, sourceVersion, request, depots);
                var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in metadata.Files)
                {
                    if (file.SourceRelativePath is not { } relative) continue;
                    var resolved = DownloadPathSafety.ResolvePayloadPath(game, relative);
                    sourceFiles[relative] = resolved;
                }
                if (builtInPackage is not null)
                    await TransitionPackageStore.ValidateSourceAsync(builtInPackage, sourceFiles, cancellationToken).ConfigureAwait(false);
                var staging = Path.Combine(storage, "Transitions", "prepared-" + Guid.NewGuid().ToString("N"), "content");
                DownloadPathSafety.EnsureNoLinks(staging);
                var prepared = await BinaryTransitionBundle.PrepareAsync(bundlePath, sourceFiles, staging, metadata.Identity,
                    request.TransitionBundleSha256!, new ForwardProgress<DownloadProgress>(value =>
                        progress?.Report(new("PreparingTransition", value.Message, value.Percentage))), cancellationToken).ConfigureAwait(false);
                if (builtInPackage is not null) TransitionPackageStore.ValidatePrepared(builtInPackage, prepared.Files);
                downloaded = new([staging], depots, prepared.Files, false);
                statistics = new(TargetBytes: prepared.Files.Aggregate(0L, (sum, file) => checked(sum + file.Length)),
                    ReusedInstalledBytes: prepared.ReusedBytes, SteamDownloadedBytes: 0,
                    BinaryPayloadBytes: prepared.PayloadBytes, Mode: request.Mode);
            }
            else
            {
                progress?.Report(new("Downloading", "Подготовка выбранной версии…"));
                downloaded = await download(new(downloadStorage, request.Version.Id, depots,
                    InstalledGameDirectory: game,
                    ReuseInstalledFiles: request.Mode is not (InstallationMode.SteamDepots or InstallationMode.LocalDepots),
                    LocalFilesOnly: request.Mode == InstallationMode.LocalDepots,
                    ForceFreshDownload: cleanInstall),
                    new ForwardProgress<DownloadProgress>(value => progress?.Report(new("Downloading", value.Message, value.Percentage,
                        Statistics: value.Statistics is null ? null : ToStatistics(value.Statistics, request.Mode)))),
                    cancellationToken).ConfigureAwait(false);
                statistics = downloaded.Statistics is null ? new(Mode: request.Mode)
                    : ToStatistics(downloaded.Statistics, request.Mode);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDepots(depots, downloaded.Depots);
            if (cleanInstall) ValidateCleanDownload(downloadStorage, downloaded);
            await ValidateExecutableAsync(downloaded.SourceDirectories, request.Version.Id, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Verifying", "Проверка скачанных файлов и установленной игры…"));
            var plan = request.Mode == InstallationMode.RuntimeOnly
                ? await engine.PrepareRuntimeAsync(downloaded.SourceDirectories, game, cancellationToken,
                    allowMissingExecutable: hasRetryDiagnostic).ConfigureAwait(false)
                : cleanInstall
                    ? await engine.PrepareCleanAsync(downloaded.SourceDirectories, game, cancellationToken).ConfigureAwait(false)
                    : await engine.PrepareAsync(downloaded.SourceDirectories, game, cancellationToken).ConfigureAwait(false);
            ValidateInventory(plan, downloaded.Files);
            statistics = statistics with
            {
                TargetBytes = statistics.TargetBytes == 0
                    ? plan.Files.Aggregate(0L, (sum, file) => checked(sum + file.Length)) : statistics.TargetBytes,
                WrittenBytes = plan.TotalBytes,
                SkippedBytes = plan.Files.Where(file => file.Action == PatchFileAction.Skip)
                    .Aggregate(0L, (sum, file) => checked(sum + file.Length))
            };
            progress?.Report(new("Prepared", "Установочные файлы проверены. Подготовка к установке…", Statistics: statistics));
            var profile = getProfileDirectory?.Invoke();
            // Inspect the profile before changing game files. The atomic rename is done only
            // after a successful installation, including a run where all depot files match.
            var catalog = !request.RenameContentCatalog || string.IsNullOrWhiteSpace(profile) ? null
                : ContentCatalogCompatibility.Prepare(profile, request.Version.Id);
            if (catalog is not null) ContentCatalogCompatibility.ValidateRenameDestination(catalog);
            var localizationProfile = getLocalizationProfileDirectory?.Invoke();
            var defaultIni = downloaded.SourceDirectories.LastOrDefault(source => File.Exists(Path.Combine(source, "Skyrim_Default.ini")));
            var localization = string.IsNullOrWhiteSpace(localizationProfile) || request.GameLanguage is null || request.Mode == InstallationMode.RuntimeOnly
                ? Array.Empty<GameLocalizationIniPlan>() : GameLocalizationProfile.Prepare(localizationProfile, gameLanguage,
                    defaultIni is null ? null : Path.Combine(defaultIni, "Skyrim_Default.ini"),
                    downloaded.Files.FirstOrDefault(file => file.RelativePath.Equals("Skyrim_Default.ini", StringComparison.OrdinalIgnoreCase))?.Sha256);
            var installed = await engine.ApplyAsync(plan, diagnosticRoot,
                new PatchApplyOptions(sourceVersion, request.Version.Id, gameLanguage == GameLocalizationCatalog.Russian,
                    request.Mode == InstallationMode.RuntimeOnly ? null : gameLanguage),
                patchProgress, cancellationToken).ConfigureAwait(false);
            string? catalogRenamed = null;
            if (catalog is not null)
            {
                progress?.Report(new("Compatibility", "Переименование ContentCatalog.txt в ContentCatalog.bak…", Statistics: statistics));
                try { catalogRenamed = ContentCatalogCompatibility.Apply(catalog); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    throw new IOException($"Файлы Skyrim {request.Version.Id} установлены, но каталог Creation Club не подготовлен. " +
                        "Повторите установку этой версии.\n" + error.Message, error);
                }
            }
            try { GameLocalizationProfile.Apply(localization); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new IOException($"Файлы Skyrim {request.Version.Id} установлены, но настройки локализации не обновлены. " +
                    "Повторите установку выбранной локализации.\n" + error.Message, error);
            }
            if (cleanInstall) DeleteCleanWorkspace(storage, downloadStorage);
            statistics = statistics with { WrittenBytes = installed.BytesCopied };
            return new(installed, plan, downloaded.FromCache, sourceVersion, request.Version.Id, catalogRenamed, statistics);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<bool> InspectInterruptionsAsync(string diagnosticRoot, string game,
        IProgress<AutomaticPatchProgress>? progress, CancellationToken token)
    {
        var diagnostics = await engine.ListDiagnosticsAsync(diagnosticRoot, token).ConfigureAwait(false);
        var matching = diagnostics.Where(item => item.GameDirectory is { } target &&
            string.Equals(Path.TrimEndingDirectorySeparator(target), game, StringComparison.OrdinalIgnoreCase)).ToArray();
        var damaged = matching.FirstOrDefault(item => item.State == "Invalid");
        if (damaged is not null)
            throw new InvalidDataException("Невозможно продолжить установку: повреждена незавершённая операция для этой игры.\n" +
                damaged.TransactionDirectory + (damaged.Error is null ? "" : "\n" + damaged.Error));
        var hasRetryDiagnostic = matching.Any(item => item.State == "Interrupted" && item.CanRetry);
        if (hasRetryDiagnostic)
            progress?.Report(new("Retrying", "Предыдущая установка прервана. Повторная установка выбранной версии…"));
        return hasRetryDiagnostic;
    }

    private async Task ValidateExecutableAsync(IReadOnlyList<string> sources, string expectedVersion, CancellationToken token)
    {
        foreach (var source in sources) DownloadPathSafety.EnsureNoLinks(source);
        var executableSource = sources.LastOrDefault(source => File.Exists(Path.Combine(source, "SkyrimSE.exe")))
            ?? throw new InvalidDataException("В скачанных файлах отсутствует SkyrimSE.exe. Игра не изменена.");
        DownloadPathSafety.EnsureNoLinks(Path.Combine(executableSource, "SkyrimSE.exe"));
        var actual = getExecutableVersion(executableSource);
        if (!VersionsMatch(expectedVersion, actual))
            throw new InvalidDataException($"Версия скачанного SkyrimSE.exe: {actual ?? "не определена"}; ожидается {expectedVersion}. Игра не изменена.");
        if (identifyExecutable is null) return;
        var identity = await identifyExecutable(executableSource, token).ConfigureAwait(false);
        if (identity.Status == ExecutableIdentityStatus.KnownVersionHashMismatch ||
            identity.Status == ExecutableIdentityStatus.KnownOfficial && !VersionsMatch(expectedVersion, identity.Version))
            throw new InvalidDataException("Хеш подготовленного SkyrimSE.exe не соответствует проверенной выбранной сборке. Игра не изменена.");
    }

    private static void ValidateDepots(IReadOnlyList<DepotManifest> expected, IReadOnlyList<DepotManifest> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidDataException("Скачанный комплект depot не соответствует выбранной версии и локализации. Игра не изменена.");
    }

    private static void ValidateCleanDownload(string workspace, DownloadResult result)
    {
        if (result.FromCache || result.Statistics is { ReusedInstalledBytes: > 0 } or { ReusedCacheBytes: > 0 } or { ImportedLocalBytes: > 0 })
            throw new InvalidDataException("Чистая установка требует новой загрузки всех файлов. Игра не изменена.");
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
        if (result.FreshSteamDirectory is { } steam)
        {
            if (!Path.IsPathFullyQualified(steam) || result.SourceDirectories.Count != result.Depots.Count ||
                result.SourceDirectories.Where((source, index) => !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)),
                    Path.Combine(Path.GetFullPath(steam), "steamapps", "content", "app_489830", "depot_" + result.Depots[index].DepotId),
                    StringComparison.OrdinalIgnoreCase)).Any())
                throw new InvalidDataException("Папки новой загрузки Steam не соответствуют выбранным depot. Игра не изменена.");
            DownloadPathSafety.EnsureNoLinks(steam);
        }
        else if (result.SourceDirectories.Count == 0 || result.SourceDirectories.Any(source =>
            !Path.GetFullPath(source).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Файлы чистой установки должны находиться в проверенных папках новой загрузки. Игра не изменена.");
        foreach (var source in result.SourceDirectories) DownloadPathSafety.EnsureNoLinks(source);
    }

    private static void DeleteCleanWorkspace(string storage, string workspace)
    {
        var parent = Path.Combine(storage, "Temp");
        if (!string.Equals(Path.GetDirectoryName(workspace), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(workspace).StartsWith("clean-", StringComparison.Ordinal))
            throw new InvalidDataException("Недопустимая временная папка чистой установки.");
        if (!Directory.Exists(workspace)) return;
        DownloadPathSafety.EnsureNoLinks(workspace);
        // Do not follow a junction inserted into our temporary tree during installation.
        var directories = new Stack<string>();
        directories.Push(workspace);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                DownloadPathSafety.EnsureNoLinks(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
            }
        }
        Directory.Delete(workspace, recursive: true);
    }

    private static void ValidateInventory(PatchPlan plan, IReadOnlyList<DownloadedFile> files)
    {
        var inventory = new Dictionary<string, DownloadedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!inventory.TryAdd(relative, file))
                throw new InvalidDataException("Список проверенных файлов содержит повторяющийся путь. Игра не изменена.");
        }
        if (inventory.Count != plan.FileCount || plan.Files.Any(file =>
            !inventory.TryGetValue(file.RelativePath, out var expected) || expected.Length != file.Length ||
            !string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Скачанные файлы изменились после проверки. Повторите установку; игра не изменена.");
    }

    private static AutomaticPatchProgress ToProgress(PatchProgress value, TransitionStatistics? statistics)
    {
        var message = value.Stage switch
        {
            "Validating" => "Проверка перед установкой…",
            "Removing" => "Удаление файлов с совпадающими именами…",
            "Applying" => "Установка выбранной версии…",
            "Completed" => "Операция завершена",
            _ => value.Stage
        };
        double? percentage = value.Stage == "Removing" && value.FilesTotal > 0 ? value.FilesCompleted * 100d / value.FilesTotal
            : value.BytesTotal > 0 ? value.BytesCompleted * 100d / value.BytesTotal
            : value.FilesTotal > 0 ? value.FilesCompleted * 100d / value.FilesTotal : null;
        var currentStatistics = statistics;
        if (currentStatistics is not null)
            currentStatistics = value.Stage switch
            {
                "Validating" => currentStatistics with { WrittenBytes = 0 },
                "Applying" => currentStatistics with { WrittenBytes = value.BytesCompleted },
                "Completed" => currentStatistics with { WrittenBytes = value.BytesCompleted },
                _ => currentStatistics
            };
        return new(value.Stage, message, percentage, value.RelativePath, currentStatistics);
    }

    private static TransitionStatistics ToStatistics(DownloadStatistics value, InstallationMode mode) =>
        new(value.TargetBytes, value.ReusedInstalledBytes, value.ReusedCacheBytes, value.ImportedLocalBytes,
            value.SteamRequestedBytes, value.SteamDownloadedBytes, Mode: mode);

    private static bool VersionsMatch(string? expectedVersion, string? actualVersion) =>
        Version.TryParse(expectedVersion, out var expected) && Version.TryParse(actualVersion, out var actual) &&
        actual.Major == expected.Major && actual.Minor == expected.Minor && actual.Build == expected.Build &&
        Math.Max(actual.Revision, 0) == Math.Max(expected.Revision, 0);

    private static void ValidateMode(AutomaticPatchRequest request)
    {
        if (!Enum.IsDefined(request.Mode)) throw new InvalidDataException("Неизвестный режим установки.");
        if (request.Mode == InstallationMode.Automatic &&
            (request.TransitionBundlePath is not null || request.TransitionBundleSha256 is not null))
            throw new InvalidDataException("Полные бинарные переходы пока требуют независимой проверки состава по манифестам Steam. " +
                "Выберите полную установку без пакета или RuntimeOnly.");
        if (request.Mode == InstallationMode.RuntimeOnly && request.GameLanguage is null && request.IncludeRussianLocalization)
            throw new InvalidDataException("Режим исполняемых файлов сохраняет текущую локализацию: отключите русскую локализацию для этого перехода.");
        if (request.Mode == InstallationMode.SteamDepots) return;
        if (request.TransitionBundlePath is not null || request.TransitionBundleSha256 is not null)
        {
            if (string.IsNullOrWhiteSpace(request.TransitionBundlePath) || !Path.IsPathFullyQualified(request.TransitionBundlePath) ||
                !SteamChunkReuseService.IsHash(request.TransitionBundleSha256, 64))
                throw new InvalidDataException("Для бинарного перехода нужны полный путь и проверенный SHA256 пакета.");
        }
    }

    private static string? NormalizeZeroRevision(string? version) => Version.TryParse(version, out var parsed)
        ? parsed.Revision == 0 ? parsed.ToString(3) : version : null;

    private static void ValidateBundleIdentity(BinaryTransitionIdentity identity, string? sourceVersion,
        AutomaticPatchRequest request, IReadOnlyList<DepotManifest> depots)
    {
        var expectedMode = request.Mode == InstallationMode.RuntimeOnly ? "RuntimeOnly" : "Complete";
        if (identity.Mode != expectedMode || !VersionsMatch(identity.SourceVersion, sourceVersion) ||
            identity.TargetVersion != request.Version.Id || identity.IncludeRussianLocalization !=
                (request.Mode != InstallationMode.RuntimeOnly && request.SelectedGameLanguage == GameLocalizationCatalog.Russian) ||
            !identity.Depots.SequenceEqual(depots))
            throw new InvalidDataException("Пакет бинарного перехода не соответствует установленной версии, выбранной цели, локализации или режиму. Игра не изменена.");
    }

    private static string CanonicalDirectory(string path, string error)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException(error);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Корень диска нельзя использовать как папку игры или рабочую папку.");
        return full;
    }

    private static bool Overlaps(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
