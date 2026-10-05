namespace SkyrimVersionPatcher.Core.Patching;

/// <summary>A validated snapshot. Prepare a fresh plan if source or game files change.</summary>
public sealed class PatchPlan
{
    internal PatchPlan(string gameDirectory, IReadOnlyList<string> sources, IReadOnlyList<PatchFile> files,
        bool allowMissingExecutable = false, bool isCleanInstall = false,
        IReadOnlyList<CleanRemoval>? cleanRemovals = null, IReadOnlyList<PatchFile>? stagedFiles = null)
    {
        GameDirectory = gameDirectory;
        SourceDirectories = sources;
        Files = files;
        AllowMissingExecutable = allowMissingExecutable;
        IsCleanInstall = isCleanInstall;
        CleanRemovals = cleanRemovals ?? Array.Empty<CleanRemoval>();
        StagedFiles = stagedFiles ?? files;
    }

    public string GameDirectory { get; }
    public IReadOnlyList<PatchFile> Files { get; }
    /// <summary>All files are installed after recursively removing matching game-file names.</summary>
    public bool IsCleanInstall { get; }
    public int FileCount => Files.Count;
    /// <summary>Bytes that will be installed; clean installation includes identical files.</summary>
    public long TotalBytes => Files.Where(file => file.Action != PatchFileAction.Skip).Sum(file => file.Length);
    public long SourceBytes => Files.Sum(file => file.Length);
    public int AddedFileCount => Files.Count(file => file.Action == PatchFileAction.Add);
    public int ReplacedFileCount => Files.Count(file => file.Action == PatchFileAction.Replace);
    public int SkippedFileCount => Files.Count(file => file.Action == PatchFileAction.Skip);
    public int ChangedFileCount => AddedFileCount + ReplacedFileCount;
    public int ExistingFileCount => Files.Count(file => file.ReplacesExistingFile);
    internal IReadOnlyList<string> SourceDirectories { get; }
    internal bool AllowMissingExecutable { get; }
    internal IReadOnlyList<CleanRemoval> CleanRemovals { get; }
    internal IReadOnlyList<PatchFile> StagedFiles { get; }
}

internal sealed record CleanRemoval(string RelativePath, string OriginalHash);

public sealed class PatchFile
{
    internal PatchFile(string relativePath, string sourcePath, long length, string sourceHash,
        DateTime sourceLastWriteUtc, string? originalHash, bool forceReplacement = false)
    {
        RelativePath = relativePath;
        SourcePath = sourcePath;
        Length = length;
        SourceHash = sourceHash;
        SourceLastWriteUtc = sourceLastWriteUtc;
        OriginalHash = originalHash;
        ForceReplacement = forceReplacement;
    }

    public string RelativePath { get; }
    public string SourcePath { get; }
    public long Length { get; }
    public string Sha256 => SourceHash;
    public PatchFileAction Action => OriginalHash is null ? PatchFileAction.Add
        : !ForceReplacement && string.Equals(OriginalHash, SourceHash, StringComparison.Ordinal) ? PatchFileAction.Skip : PatchFileAction.Replace;
    /// <summary>An existing file differs. Without a pristine baseline its mod origin is unknown.</summary>
    public bool PossibleModCollision => OriginalHash is not null
        && !string.Equals(OriginalHash, SourceHash, StringComparison.Ordinal);
    public bool ReplacesExistingFile => OriginalHash is not null;
    internal string SourceHash { get; }
    internal DateTime SourceLastWriteUtc { get; }
    internal string? OriginalHash { get; }
    private bool ForceReplacement { get; }
}

public enum PatchFileAction { Add, Replace, Skip }

public sealed record PatchApplyOptions(string? SourceVersion = null, string? TargetVersion = null,
    bool? IncludeRussianLocalization = null, string? GameLanguage = null);

public sealed record PatchProgress(string Stage, string? RelativePath, int FilesCompleted,
    int FilesTotal, long BytesCompleted, long BytesTotal);

/// <summary>TransactionDirectory is empty after a completed installation.</summary>
public sealed record PatchResult(string TransactionDirectory, int FilesCopied, long BytesCopied);

/// <summary>Interrupted installations retain only diagnostics for a fresh installation attempt.</summary>
public sealed record InstallationDiagnostic(string TransactionDirectory, string? GameDirectory,
    string State, string? Error, bool CanRetry);

/// <summary>Installation stopped after game-file mutation began.</summary>
public sealed class PatchIncompleteInstallationException : IOException
{
    public PatchIncompleteInstallationException(string transactionDirectory, Exception originalError)
        : base("Установка прервана. Часть файлов уже изменена; повторите установку выбранной версии.", originalError)
    {
        TransactionDirectory = transactionDirectory;
        WasCancelled = originalError is OperationCanceledException;
    }

    /// <summary>Contains only diagnostic journals, never saved game files.</summary>
    public string TransactionDirectory { get; }
    public bool WasCancelled { get; }
}
