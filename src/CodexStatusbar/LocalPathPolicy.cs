using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexStatusbar;

/// <summary>
/// Local-disk boundary checked before filesystem probes. This is not a sandbox against another
/// process running as the same user and racing path or volume changes.
/// </summary>
internal static class LocalPathPolicy
{
    public static string NormalizeLocalPath(string path)
    {
        if (!TryNormalize(path, out var normalized)
            || !Validate(normalized, allowMissing: true, requireDirectory: false, requireFile: false))
        {
            throw new ArgumentException("A local disk path without links or alternate data streams is required.", nameof(path));
        }
        return normalized;
    }

    public static bool IsAllowedFile(string path) => TryNormalize(path, out var normalized)
        && Validate(normalized, allowMissing: false, requireDirectory: false, requireFile: true);

    public static bool IsAllowedWritePath(string path) => TryNormalize(path, out var normalized)
        && Validate(normalized, allowMissing: true, requireDirectory: false, requireFile: true);

    public static bool IsAllowedDirectory(string path, bool allowMissing = false) =>
        TryNormalize(path, out var normalized)
        && Validate(normalized, allowMissing, requireDirectory: true, requireFile: false);

    /// <summary>Pure Windows syntax check; never probes the supplied drive or share.</summary>
    internal static bool IsLocalAbsolutePathSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var windows = path.Replace('/', '\\');
        return windows.Length >= 3 && char.IsAsciiLetter(windows[0]) && windows[1] == ':'
            && windows[2] == '\\' && windows.IndexOf(':', 2) < 0
            && windows.IndexOf('\0') < 0;
    }

    private static bool TryNormalize(string path, out string normalized)
    {
        normalized = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        // Reject UNC, NT/device namespaces and drive-relative names before any metadata access.
        var windows = path.Replace('/', '\\');
        if (windows.StartsWith('\\') || windows.IndexOf('\0') >= 0
            || (windows.Length >= 2 && windows[1] == ':'
                && (windows.Length < 3 || windows[2] != '\\'))) return false;
        try
        {
            normalized = Path.GetFullPath(path);
            return IsLocalAbsolutePathSyntax(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool Validate(string path, bool allowMissing, bool requireDirectory, bool requireFile) =>
        ValidateWithProbes(path, root => new DriveInfo(root).DriveType, File.GetAttributes,
            allowMissing, requireDirectory, requireFile);

    /// <summary>Injectable probes keep synthetic rejection tests off real drives and networks.</summary>
    internal static bool ValidateWithProbes(string path, Func<string, DriveType> driveType,
        Func<string, FileAttributes> attributes, bool allowMissing = false,
        bool requireDirectory = false, bool requireFile = false)
    {
        if (!IsLocalAbsolutePathSyntax(path)) return false;
        var windows = path.Replace('/', '\\');
        var root = windows[..3];
        try
        {
            // GetDriveType through DriveInfo does not enumerate or open the share. Reject mapped
            // network drives before File.GetAttributes, File.Exists, SQLite or directory watching.
            if (driveType(root) is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom
                or DriveType.Ram)) return false;
            var components = windows[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Any(component => component is "." or "..")) return false;
            var current = root;
            for (var index = -1; index < components.Length; index++)
            {
                if (index >= 0) current = current.TrimEnd('\\') + "\\" + components[index];
                FileAttributes flags;
                try { flags = attributes(current); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return allowMissing;
                }
                if ((flags & FileAttributes.ReparsePoint) != 0) return false;
                var isDirectory = (flags & FileAttributes.Directory) != 0;
                var isLast = index == components.Length - 1;
                if (!isLast && !isDirectory) return false;
                if (isLast && ((requireDirectory && !isDirectory) || (requireFile && isDirectory))) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>After opening, verify the handle still names the expected local file before reading.</summary>
    internal static bool MatchesOpenedFile(FileStream stream, string expectedPath)
    {
        if (!TryNormalize(expectedPath, out var normalized)) return false;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= 32768) return false;
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }
        var finalPath = buffer.ToString();
        // The kernel returns a DOS extended prefix for ordinary local handles. Never accept this
        // namespace in caller input; only remove it from the trusted handle query result.
        if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        return IsLocalAbsolutePathSyntax(finalPath)
            && string.Equals(finalPath, normalized, StringComparison.OrdinalIgnoreCase)
            && IsAllowedFile(normalized);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path,
        uint length, uint flags);
}
