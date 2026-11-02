namespace CodexStatusbar;

internal static class RolloutPathPolicy
{
    /// <summary>Database paths are untrusted; only regular files inside this sessions root qualify.</summary>
    public static bool IsAllowed(string sessionRoot, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot));
            var candidate = Path.GetFullPath(path);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !candidate.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                return false;
            }

            // Lexical containment alone does not stop junction/symlink escapes. Reject reparse
            // points at every component, including the configured root. This is not a defense
            // against a same-user process racing filesystem changes after validation.
            var current = candidate;
            while (true)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current)!;
                if (string.IsNullOrEmpty(current))
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

}
