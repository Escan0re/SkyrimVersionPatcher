using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkyrimVersionPatcher.Core.Patching;

/// <summary>
/// Verified replacement of the files supplied by Steam depots. Unrelated files are retained.
/// Copies use CreateNew and moves never overwrite; existing game files are deleted first.
/// </summary>
public sealed class PatchEngine
{
    private const string JournalName = "journal.json";
    private const string SecondaryJournalName = "journal.secondary.json";
    private const long DiskReserveBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> RuntimePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "SkyrimSE.exe", "SkyrimSELauncher.exe", Path.Combine("Data", "Skyrim - Shaders.bsa")
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Func<string, long> availableFreeSpace;

    /// <param name="availableFreeSpace">Optional disk-capacity provider, for hosts and tests.</param>
    public PatchEngine(Func<string, long>? availableFreeSpace = null) =>
        this.availableFreeSpace = availableFreeSpace ?? ReadAvailableFreeSpace;

    public Task<PatchPlan> PrepareAsync(IEnumerable<string> sourceDirectories, string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);
        var sources = sourceDirectories.ToArray();
        return Task.Run(() => Prepare(sources, gameDirectory, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Installs every staged file, including identical files. All game files with matching names
    /// are removed recursively before any staged file is moved to its depot-relative location.
    /// </summary>
    public Task<PatchPlan> PrepareCleanAsync(IEnumerable<string> sourceDirectories, string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);
        var sources = sourceDirectories.ToArray();
        return Task.Run(() => Prepare(sources, gameDirectory, cancellationToken, cleanInstall: true), cancellationToken);
    }

    /// <summary>
    /// Explicit runtime-only installation into an existing game. Only SkyrimSE.exe, its launcher
    /// and Skyrim - Shaders.bsa are accepted; masters, localization, mods and other assets remain untouched.
    /// </summary>
    public Task<PatchPlan> PrepareRuntimeAsync(IEnumerable<string> sourceDirectories, string gameDirectory,
        CancellationToken cancellationToken = default, bool allowMissingExecutable = false)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);
        var sources = sourceDirectories.ToArray();
        return Task.Run(() => Prepare(sources, gameDirectory, cancellationToken, runtimeOnly: true,
            allowMissingExecutable: allowMissingExecutable), cancellationToken);
    }

    public Task<PatchResult> ApplyAsync(PatchPlan plan, string diagnosticRoot,
        IProgress<PatchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ApplyAsync(plan, diagnosticRoot, new PatchApplyOptions(), progress, cancellationToken);

    public Task<PatchResult> ApplyAsync(PatchPlan plan, string diagnosticRoot, PatchApplyOptions options,
        IProgress<PatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Apply(plan, diagnosticRoot, options, progress, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<InstallationDiagnostic>> ListDiagnosticsAsync(string diagnosticRoot,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<InstallationDiagnostic>>(() =>
    {
        var root = Canonical(diagnosticRoot);
        CheckAncestors(root);
        if (!Directory.Exists(root)) return Array.Empty<InstallationDiagnostic>();
        var result = new List<InstallationDiagnostic>();
        foreach (var directory in Directory.EnumerateDirectories(root, "transaction-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = InspectDiagnostic(directory);
            if (diagnostic is not null) result.Add(diagnostic);
        }
        return result.OrderBy(item => item.TransactionDirectory, StringComparer.OrdinalIgnoreCase).ToArray();
    }, cancellationToken);

    private static PatchPlan Prepare(string[] sourceDirectories, string gameDirectory, CancellationToken token,
        bool runtimeOnly = false, bool allowMissingExecutable = false, bool cleanInstall = false)
    {
        var game = Canonical(gameDirectory);
        RequireDirectory(game);
        if (runtimeOnly) RequireInstalledGameBase(game, allowMissingExecutable);
        var sources = sourceDirectories.Select(Canonical).ToArray();
        if (sources.Length == 0) throw new InvalidDataException("Не выбраны скачанные файлы.");
        var files = new Dictionary<string, PatchFile>(StringComparer.OrdinalIgnoreCase);
        var stagedFiles = new List<PatchFile>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            RequireDirectory(source);
            RejectOverlap(game, source);
            if (sources.Count(other => Overlaps(source, other)) != 1)
                throw new InvalidDataException("Каталоги скачанных файлов пересекаются или повторяются.");
            var sourceFiles = EnumerateSafeFiles(source, includeMetadata: runtimeOnly).ToArray();
            if (sourceFiles.Length == 0) throw new InvalidDataException($"Каталог скачанных файлов пуст: {source}");
            foreach (var sourcePath in sourceFiles)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, sourcePath);
                if (runtimeOnly && !RuntimePaths.Contains(relative))
                    throw new InvalidDataException($"Режим исполняемых файлов не допускает этот файл: {relative}");
                var target = ResolveRelative(game, relative);
                CheckAncestors(target);
                if (Directory.Exists(target)) throw new IOException($"Вместо файла существует каталог: {target}");
                var sourceInfo = new FileInfo(sourcePath);
                var sourceHash = HashFile(sourcePath, token);
                var existing = File.Exists(target) ? new FileInfo(target) : null;
                var item = new PatchFile(relative, sourcePath, sourceInfo.Length, sourceHash,
                    sourceInfo.LastWriteTimeUtc, existing is null ? null : HashFile(target, token), cleanInstall);
                stagedFiles.Add(item);
                // Steam's localization depot can replace Skyrim_Default.ini. Apply sources in
                // catalog order: base depots first, optional localization last.
                files[relative] = item;
            }
        }
        if (runtimeOnly && !files.ContainsKey("SkyrimSE.exe"))
            throw new InvalidDataException("Для перехода исполняемых файлов необходим целевой SkyrimSE.exe.");
        if (!runtimeOnly && (!files.ContainsKey("SkyrimSE.exe") || !files.ContainsKey(Path.Combine("Data", "Skyrim.esm"))))
            throw new InvalidDataException("Неполный комплект депо: необходимы SkyrimSE.exe и Data\\Skyrim.esm. Выберите все основные депо.");
        return new PatchPlan(game, Array.AsReadOnly(sources),
            Array.AsReadOnly(files.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray()),
            allowMissingExecutable, cleanInstall,
            cleanInstall ? SnapshotCleanRemovals(game, files.Values, token) : null,
            cleanInstall ? stagedFiles.AsReadOnly() : null);
    }

    private static IReadOnlyList<CleanRemoval> SnapshotCleanRemovals(string game,
        IEnumerable<PatchFile> files, CancellationToken token)
    {
        var names = files.Select(file => Path.GetFileName(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removals = new List<CleanRemoval>();
        foreach (var path in EnumerateSafeFiles(game, includeMetadata: true))
        {
            token.ThrowIfCancellationRequested();
            if (!names.Contains(Path.GetFileName(path))) continue;
            var relative = Path.GetRelativePath(game, path);
            ResolveRelative(game, relative);
            removals.Add(new(relative, HashFile(path, token)));
        }
        return removals.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private PatchResult Apply(PatchPlan plan, string diagnosticRoot, PatchApplyOptions options,
        IProgress<PatchProgress>? progress, CancellationToken token)
    {
        using var gameLock = GameLock.Acquire(plan.GameDirectory);
        EnsureGameClosed();
        var diagnostics = Canonical(diagnosticRoot);
        RejectOverlap(plan.GameDirectory, diagnostics);
        foreach (var source in plan.SourceDirectories) RejectOverlap(source, diagnostics);
        CheckAncestors(diagnostics);
        RequireDirectory(plan.GameDirectory);
        // Only PrepareRuntimeAsync creates a plan without the game master. Recheck its
        // existing base before the transaction in case the game was removed after planning.
        if (!plan.Files.Any(file => file.RelativePath.Equals(Path.Combine("Data", "Skyrim.esm"), StringComparison.OrdinalIgnoreCase)))
            RequireInstalledGameBase(plan.GameDirectory, plan.AllowMissingExecutable);
        Report(progress, "Validating", null, 0, plan.FileCount, 0, plan.TotalBytes);
        ValidatePlan(plan, token);
        foreach (var directory in Directory.Exists(diagnostics) ? Directory.EnumerateDirectories(diagnostics, "transaction-*") : [])
        {
            var prior = InspectDiagnostic(directory);
            if (prior?.State == "Invalid" && string.Equals(prior.GameDirectory, plan.GameDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Повреждён журнал операции для этой папки игры: {directory}. {prior.Error}");
        }

        var changed = plan.Files.Where(file => file.Action != PatchFileAction.Skip).ToArray();
        if (changed.Length == 0)
        {
            ClearCoveredDiagnostics(plan, diagnostics);
            Report(progress, "Completed", null, 0, 0, 0, 0);
            return new("", 0, 0);
        }
        EnsureDiskSpace(plan, diagnostics);
        Directory.CreateDirectory(diagnostics);

        var transaction = Path.Combine(diagnostics, $"transaction-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transaction);
        var journal = new TransactionJournal
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            GameDirectory = plan.GameDirectory,
            CreatedAt = DateTimeOffset.UtcNow,
            SourceVersion = options.SourceVersion,
            TargetVersion = options.TargetVersion,
            IncludeRussianLocalization = options.IncludeRussianLocalization,
            GameLanguage = options.GameLanguage,
            Files = (plan.IsCleanInstall
                ? plan.CleanRemovals.Select(file => file.RelativePath).Concat(changed.Select(file => file.RelativePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                : changed.Select(file => file.RelativePath))
                .Select(relative => new JournalFile { RelativePath = relative }).ToList()
        };
        try
        {
            SaveJournal(transaction, journal);
            // Recheck every staged file and matching destination before the first deletion.
            if (plan.IsCleanInstall) ValidatePlan(plan, token);
            else ValidateDestinations(plan, token);
            token.ThrowIfCancellationRequested();
            journal.State = "Applying";
            SaveJournal(transaction, journal);
            long copied = 0;
            if (plan.IsCleanInstall)
            {
                for (var i = 0; i < plan.CleanRemovals.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var removal = plan.CleanRemovals[i];
                    var target = ResolveRelative(plan.GameDirectory, removal.RelativePath);
                    VerifyCleanRemoval(removal, target, token);
                    journal.AffectedCount = i + 1;
                    SaveJournal(transaction, journal);
                    DeleteFileIfPresent(target);
                    Report(progress, "Removing", removal.RelativePath, i + 1, plan.CleanRemovals.Count, 0, plan.TotalBytes);
                }
                // Check the complete tree again. No staged file may be installed while a
                // matching filename remains, including one created since validation.
                if (SnapshotCleanRemovals(plan.GameDirectory, plan.Files, token).Count != 0)
                    throw new IOException("В папке игры появились файлы после проверки. Повторите чистую установку.");
            }
            for (var i = 0; i < changed.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = changed[i];
                var target = ResolveRelative(plan.GameDirectory, item.RelativePath);
                if (plan.IsCleanInstall)
                {
                    CheckAncestors(target);
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new IOException($"Целевой путь появился после удаления: {item.RelativePath}");
                }
                else VerifyDestination(item, target, token);
                CheckAncestors(item.SourcePath);
                journal.AffectedCount = plan.IsCleanInstall ? journal.Files.Count : i + 1;
                SaveJournal(transaction, journal); // durable intent before deleting or creating a game file
                CreateSafeParent(target);
                if (plan.IsCleanInstall) MoveNewVerified(item, target, token);
                else
                {
                    DeleteFileIfPresent(target);
                    CopyNewVerified(item.SourcePath, target, item.SourceHash, token,
                        bytes => Report(progress, "Applying", item.RelativePath, i, changed.Length, copied + bytes, plan.TotalBytes));
                }
                File.SetLastWriteTimeUtc(target, item.SourceLastWriteUtc);
                copied += item.Length;
                Report(progress, "Applying", item.RelativePath, i + 1, changed.Length, copied, plan.TotalBytes);
            }
            token.ThrowIfCancellationRequested();
            journal.State = "Completed";
            SaveJournal(transaction, journal);
            Report(progress, "Completed", null, changed.Length, changed.Length, plan.TotalBytes, plan.TotalBytes);
            DeleteDiagnosticJournal(transaction);
            ClearCoveredDiagnostics(plan, diagnostics);
            return new("", changed.Length, plan.TotalBytes);
        }
        catch (Exception error)
        {
            if (journal.AffectedCount == 0)
            {
                DeleteDiagnosticJournal(transaction);
                throw;
            }
            journal.State = "Interrupted";
            // The prior Applying checkpoint already identifies all possible partial writes.
            try { SaveJournal(transaction, journal); }
            catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException) { }
            throw new PatchIncompleteInstallationException(transaction, error);
        }
    }

    private static void ValidatePlan(PatchPlan plan, CancellationToken token)
    {
        foreach (var source in plan.SourceDirectories)
        {
            RequireDirectory(source);
            RejectOverlap(plan.GameDirectory, source);
        }
        foreach (var file in plan.StagedFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(file.SourcePath) || HashFile(file.SourcePath, token) != file.SourceHash)
                throw new IOException($"Скачанный файл изменился. Повторите проверку: {file.RelativePath}");
        }
        ValidateDestinations(plan, token);
        if (plan.IsCleanInstall) ValidateCleanRemovals(plan, token);
    }

    private static void ValidateCleanRemovals(PatchPlan plan, CancellationToken token)
    {
        var current = SnapshotCleanRemovals(plan.GameDirectory, plan.Files, token);
        if (current.Count != plan.CleanRemovals.Count || current.Zip(plan.CleanRemovals).Any(pair =>
            !string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase)
            || pair.First.OriginalHash != pair.Second.OriginalHash))
            throw new IOException("Файлы игры изменились после проверки. Подготовьте новый план чистой установки.");
        // Detect a locked matching file before deleting any other file.
        foreach (var removal in plan.CleanRemovals)
        {
            token.ThrowIfCancellationRequested();
            var path = ResolveRelative(plan.GameDirectory, removal.RelativePath);
            CheckAncestors(path);
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private static void VerifyCleanRemoval(CleanRemoval file, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckAncestors(target);
        if (!File.Exists(target) || HashFile(target, token) != file.OriginalHash)
            throw new IOException($"Файл игры изменился после проверки. Подготовьте новый план: {file.RelativePath}");
    }

    private static void MoveNewVerified(PatchFile file, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckAncestors(file.SourcePath);
        CheckAncestors(target);
        if (HashFile(file.SourcePath, token) != file.SourceHash)
            throw new IOException($"Скачанный файл изменился. Повторите проверку: {file.RelativePath}");
        // File.Move also supports different volumes. It never overwrites a game file.
        File.Move(file.SourcePath, target, overwrite: false);
        if (HashFile(target, token) != file.SourceHash)
            throw new IOException($"Содержимое файла изменилось во время перемещения: {file.RelativePath}");
    }

    private static void ValidateDestinations(PatchPlan plan, CancellationToken token)
    {
        foreach (var file in plan.Files)
            VerifyDestination(file, ResolveRelative(plan.GameDirectory, file.RelativePath), token);
    }

    private static void VerifyDestination(PatchFile file, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckAncestors(target);
        if (Directory.Exists(target)) throw new IOException($"Вместо файла существует каталог: {target}");
        var present = File.Exists(target);
        if (present != file.ReplacesExistingFile || (present && HashFile(target, token) != file.OriginalHash))
            throw new IOException($"Файл игры изменился после проверки. Подготовьте новый план: {file.RelativePath}");
    }

    private static void CopyNewVerified(string source, string destination, string expectedHash, CancellationToken token, Action<long>? progress = null)
    {
        CheckAncestors(source);
        CheckAncestors(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long bytes = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            bytes += read;
            progress?.Invoke(bytes);
        }
        token.ThrowIfCancellationRequested();
        output.Flush(true);
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.Ordinal))
            throw new IOException($"Содержимое файла изменилось во время копирования: {source}");
    }

    private static string HashFile(string path, CancellationToken token)
    {
        CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root, bool includeMetadata = false)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Ссылки и точки повторной обработки не поддерживаются: {entry}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!includeMetadata && string.Equals(Path.GetFileName(entry), ".DepotDownloader", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var file in EnumerateSafeFiles(entry, includeMetadata)) yield return file;
            }
            else yield return entry;
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указан путь.", nameof(path));
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("Пути устройств не поддерживаются.", nameof(path));
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Корень диска нельзя использовать как каталог операции.", nameof(path));
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split(['/', '\\']).Any(IsUnsafePathComponent))
            throw new InvalidDataException($"Недопустимый относительный путь: {relative}");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы каталога операции.");
        return result;
    }

    private static bool IsUnsafePathComponent(string part)
    {
        if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(character => character < 32 || "<>:\"|?*".Contains(character))) return true;
        // Win32 device aliases must not be interpreted as game files, including aliases with extensions.
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    private static bool Overlaps(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectOverlap(string first, string second)
    {
        if (Overlaps(first, second)) throw new InvalidDataException($"Каталоги операции не должны пересекаться: {first}; {second}");
    }

    private static void CheckAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Ссылки и точки повторной обработки не поддерживаются: {current}");
            if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase) && (attributes & FileAttributes.Directory) == 0)
                throw new IOException($"Родительский путь является файлом: {current}");
        }
    }

    private static void RequireDirectory(string path)
    {
        CheckAncestors(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Каталог не найден: {path}");
    }

    private static void RequireInstalledGameBase(string game, bool allowMissingExecutable = false)
    {
        foreach (var relative in new[] { "SkyrimSE.exe", Path.Combine("Data", "Skyrim.esm") })
        {
            var path = ResolveRelative(game, relative);
            CheckAncestors(path);
            if (allowMissingExecutable && relative == "SkyrimSE.exe"
                && (!File.Exists(path) || new FileInfo(path).Length == 0)) continue;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("Переход исполняемых файлов требует существующую копию игры с SkyrimSE.exe и Data\\Skyrim.esm.");
        }
    }

    private static void CreateSafeParent(string path)
    {
        CheckAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckAncestors(path);
    }

    private static void DeleteFileIfPresent(string path)
    {
        CheckAncestors(path);
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path); // Intentionally separate from copying: never copy with overwrite.
    }

    private void EnsureDiskSpace(PatchPlan plan, string diagnostics)
    {
        var gameRoot = Path.GetPathRoot(plan.GameDirectory)!;
        var diagnosticVolume = Path.GetPathRoot(diagnostics)!;
        // Staged files on the game volume are renamed, so they do not need a second
        // payload-sized allocation. Different-volume moves still copy their payload.
        var allocationBytes = plan.IsCleanInstall
            ? plan.Files.Where(file => !string.Equals(Path.GetPathRoot(file.SourcePath), gameRoot,
                StringComparison.OrdinalIgnoreCase)).Sum(file => file.Length)
            : plan.TotalBytes;
        CheckFreeSpace(gameRoot, checked(allocationBytes + DiskReserveBytes));
        if (!string.Equals(gameRoot, diagnosticVolume, StringComparison.OrdinalIgnoreCase))
            CheckFreeSpace(diagnosticVolume, DiskReserveBytes);
    }

    private void CheckFreeSpace(string root, long required)
    {
        if (availableFreeSpace(root) < required)
            throw new IOException($"Недостаточно места на диске {root}: необходимо минимум {required:N0} байт для установки.");
    }

    private static long ReadAvailableFreeSpace(string root)
    {
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException($"Диск недоступен: {root}");
        return drive.AvailableFreeSpace;
    }

    private static void EnsureGameClosed()
    {
        foreach (var name in new[] { "SkyrimSE", "SkyrimSELauncher", "skse64_loader" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0) throw new IOException("Закройте Skyrim и его лаунчер перед заменой файлов.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static TransactionJournal ReadJournal(string transaction)
    {
        RequireDirectory(transaction);
        var candidates = new List<(TransactionJournal Journal, bool Secondary)>();
        var errors = new List<string>();
        foreach (var name in new[] { JournalName, SecondaryJournalName })
        {
            var path = Path.Combine(transaction, name);
            try
            {
                CheckAncestors(path);
                var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Журнал пуст.");
                ValidateJournal(transaction, journal);
                candidates.Add((journal, name == SecondaryJournalName));
            }
            catch (Exception error) when (IsJournalError(error)) { errors.Add($"{name}: {error.Message}"); }
        }
        if (candidates.Count == 0)
            throw new InvalidDataException($"Не удалось прочитать журнал операции {transaction}. {string.Join(" ", errors)}");
        if (candidates.Count == 2 && !SameJournalIdentity(candidates[0].Journal, candidates[1].Journal))
            throw new InvalidDataException($"Контрольные журналы относятся к разным операциям: {transaction}");
        if (candidates.Count == 2 && candidates[0].Journal.Sequence == candidates[1].Journal.Sequence
            && JsonSerializer.Serialize(candidates[0].Journal, JsonOptions) != JsonSerializer.Serialize(candidates[1].Journal, JsonOptions))
            throw new InvalidDataException($"Журналы одного контрольного состояния противоречат друг другу: {transaction}");
        var chosen = candidates.OrderByDescending(item => item.Journal.Sequence).ThenBy(item => item.Secondary).First();
        return chosen.Journal;
    }

    private static bool IsJournalError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException or NullReferenceException;

    private static bool SameJournalIdentity(TransactionJournal first, TransactionJournal second) =>
        first.FormatVersion == second.FormatVersion && first.TransactionId == second.TransactionId
        && first.GameDirectory == second.GameDirectory && first.CreatedAt == second.CreatedAt
        && first.SourceVersion == second.SourceVersion && first.TargetVersion == second.TargetVersion
        && first.IncludeRussianLocalization == second.IncludeRussianLocalization
        && first.GameLanguage == second.GameLanguage
        && JsonSerializer.Serialize(first.Files, JsonOptions) == JsonSerializer.Serialize(second.Files, JsonOptions);

    private static InstallationDiagnostic? InspectDiagnostic(string directory)
    {
        try
        {
            var journal = ReadJournal(directory);
            if (journal.AffectedCount == 0 || journal.State == "Completed") return null;
            return new(directory, journal.GameDirectory, "Interrupted",
                "Часть файлов уже изменена; повторите установку выбранной версии.", true);
        }
        catch (Exception error) when (IsJournalError(error) || error is OverflowException)
        {
            return new(directory, TryReadGameDirectory(directory), "Invalid", error.Message, false);
        }
    }
    private static string? TryReadGameDirectory(string directory)
    {
        foreach (var name in new[] { SecondaryJournalName, JournalName })
        {
            try
            {
                var path = Path.Combine(directory, name);
                CheckAncestors(path);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (!json.RootElement.TryGetProperty("GameDirectory", out var property) || property.ValueKind != JsonValueKind.String) continue;
                var game = Canonical(property.GetString()!);
                RejectOverlap(game, Canonical(directory));
                return game;
            }
            catch (Exception error) when (IsJournalError(error) || error is InvalidOperationException) { }
        }
        return null;
    }

    private static void ValidateJournal(string transaction, TransactionJournal journal)
    {
        if (journal.FormatVersion != 4 || journal.State is not ("Preparing" or "Applying" or "Completed" or "Interrupted")
            || journal.Files is null || journal.Files.Count == 0
            || journal.AffectedCount < 0 || journal.AffectedCount > journal.Files.Count)
            throw new InvalidDataException("Повреждён журнал установки.");
        if (journal.CreatedAt == default || !Guid.TryParseExact(journal.TransactionId, "N", out _) || journal.Sequence < 1
            || journal.State == "Preparing" && journal.AffectedCount != 0
            || journal.State == "Completed" && journal.AffectedCount != journal.Files.Count
            || journal.State == "Interrupted" && journal.AffectedCount == 0)
            throw new InvalidDataException("Повреждено контрольное состояние журнала установки.");
        var game = Canonical(journal.GameDirectory);
        if (!string.Equals(game, journal.GameDirectory, StringComparison.Ordinal)) throw new InvalidDataException("Недопустимый путь игры в журнале.");
        RejectOverlap(game, Canonical(transaction));
        CheckAncestors(game);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            var target = ResolveRelative(game, file.RelativePath);
            if (!seen.Add(target)) throw new InvalidDataException("Повторная запись в журнале установки.");
        }
    }
    private static void SaveJournal(string transaction, TransactionJournal journal)
    {
        CheckAncestors(transaction);
        journal.Sequence = checked(journal.Sequence + 1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        // Persist the secondary first. After a crash it is current or conservatively ahead of
        // primary, never an older intent that would omit a file already deleted from the game.
        // No game mutation starts until both checkpoints have returned successfully.
        WriteJournalReplica(transaction, SecondaryJournalName, bytes);
        WriteJournalReplica(transaction, JournalName, bytes);
    }

    private static void DeleteDiagnosticJournal(string transaction)
    {
        try
        {
            CheckAncestors(transaction);
            var diagnosticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { JournalName, SecondaryJournalName, JournalName + ".tmp", SecondaryJournalName + ".tmp" };
            // Never remove an existing artifact that contains payloads or other user files.
            if (Directory.EnumerateFileSystemEntries(transaction).Any(path =>
                !diagnosticNames.Contains(Path.GetFileName(path)) || Directory.Exists(path))) return;
            foreach (var name in new[] { JournalName, SecondaryJournalName, JournalName + ".tmp", SecondaryJournalName + ".tmp" })
            {
                var path = Path.Combine(transaction, name);
                CheckAncestors(path);
                File.Delete(path);
            }
            Directory.Delete(transaction); // only the now-empty metadata directory
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover diagnostic contains no game payload.
        }
    }

    private static void ClearCoveredDiagnostics(PatchPlan plan, string diagnostics)
    {
        if (!Directory.Exists(diagnostics)) return;
        var installedPaths = plan.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedNames = plan.Files.Select(file => Path.GetFileName(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(diagnostics, "transaction-*"))
            {
                try
                {
                    var journal = ReadJournal(directory);
                    if (string.Equals(journal.GameDirectory, plan.GameDirectory, StringComparison.OrdinalIgnoreCase)
                        && journal.Files.Take(journal.AffectedCount).All(file => plan.IsCleanInstall
                            ? installedNames.Contains(Path.GetFileName(file.RelativePath)) : installedPaths.Contains(file.RelativePath)))
                        DeleteDiagnosticJournal(directory);
                }
                catch (Exception error) when (IsJournalError(error)) { }
            }
        }
        catch (Exception error) when (IsJournalError(error)) { }
    }

    private static void WriteJournalReplica(string transaction, string name, byte[] bytes)
    {
        var temporary = Path.Combine(transaction, name + ".tmp");
        var destination = Path.Combine(transaction, name);
        CheckAncestors(temporary);
        CheckAncestors(destination);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, destination, true);
    }

    private static void Report(IProgress<PatchProgress>? progress, string stage, string? path,
        int completed, int total, long bytes, long byteTotal) => progress?.Report(new(stage, path, completed, total, bytes, byteTotal));

    private sealed class GameLock : IDisposable
    {
        private readonly Mutex mutex;
        private GameLock(Mutex mutex) => this.mutex = mutex;
        public static GameLock Acquire(string game)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())));
            var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Local\\" : "") + "SkyrimVersionPatcher-" + hash);
            try
            {
                try
                {
                    if (!mutex.WaitOne(0)) throw new IOException("Для этого каталога игры уже выполняется другая операция.");
                }
                catch (AbandonedMutexException) { /* The crashed owner no longer holds the mutex. */ }
                return new GameLock(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private sealed class TransactionJournal
    {
        public TransactionJournal() { }
        public int FormatVersion { get; set; } = 4;
        public string TransactionId { get; set; } = "";
        public long Sequence { get; set; }
        public string GameDirectory { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string? SourceVersion { get; set; }
        public string? TargetVersion { get; set; }
        public bool? IncludeRussianLocalization { get; set; }
        public string? GameLanguage { get; set; }
        public string State { get; set; } = "Preparing";
        public int AffectedCount { get; set; }
        public List<JournalFile> Files { get; set; } = [];
    }

    private sealed class JournalFile
    {
        public JournalFile() { }
        public string RelativePath { get; set; } = "";
    }
}
