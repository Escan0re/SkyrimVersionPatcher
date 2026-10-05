namespace SkyrimVersionPatcher.Core.Downloading;

internal static class DownloadPathSafety
{
    internal static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            // Exists follows a junction and returns false for a dangling target.
            // Inspect the entry itself so it cannot become an escape after validation.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Путь содержит ссылку или junction: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string ResolvePayloadPath(string root, string relativePath)
    {
        var parts = relativePath?.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            parts!.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Некорректный путь в кэше.");
        var normalized = string.Join(Path.DirectorySeparatorChar, parts!);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы кэша.");
        EnsureNoLinks(full);
        return full;
    }
}
