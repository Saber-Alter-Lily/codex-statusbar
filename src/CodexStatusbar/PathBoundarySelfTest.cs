using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>Pure injected probes plus newly-created local files; never accesses a network share.</summary>
internal static class PathBoundarySelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        var rejected = new[] { @"\\synthetic.invalid\share\session.jsonl", "//synthetic.invalid/share/session.jsonl",
            @"\\?\C:\synthetic\session.jsonl", @"\\.\C:\synthetic\session.jsonl", @"\??\C:\synthetic\session.jsonl",
            @"C:synthetic\session.jsonl", @"C:\synthetic\session.jsonl:stream", @"\synthetic\session.jsonl" };
        foreach (var path in rejected)
        {
            var calls = 0;
            var accepted = LocalPathPolicy.ValidateWithProbes(path,
                _ => { calls++; return DriveType.Fixed; }, _ => { calls++; return FileAttributes.Normal; });
            checks.Add(("unsafe path rejected without filesystem probe: " + checks.Count, !accepted && calls == 0));
        }

        const string localFile = @"C:\synthetic\sessions\one.jsonl";
        static FileAttributes Ordinary(string path) => path.EndsWith(".jsonl", StringComparison.Ordinal)
            ? FileAttributes.Normal : FileAttributes.Directory;
        checks.Add(("local file and every ordinary ancestor accepted", LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, Ordinary, requireFile: true)));
        checks.Add(("local removable storage accepted", LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Removable, Ordinary, requireFile: true)));
        foreach (var kind in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory })
        {
            var calls = 0;
            var accepted = LocalPathPolicy.ValidateWithProbes(localFile, _ => kind,
                _ => { calls++; return FileAttributes.Normal; });
            checks.Add(("unsafe drive rejected before metadata: " + kind, !accepted && calls == 0));
        }
        checks.Add(("reparse ancestor above sessions root rejected", !LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, path => path == @"C:\synthetic"
                ? FileAttributes.Directory | FileAttributes.ReparsePoint : Ordinary(path))));
        checks.Add(("reparse file rejected", !LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, path => path == localFile
                ? FileAttributes.ReparsePoint : Ordinary(path))));
        checks.Add(("directory cannot stand in for a rollout file", !LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, _ => FileAttributes.Directory, requireFile: true)));
        static FileAttributes MissingLeaf(string path) => path == localFile
            ? throw new FileNotFoundException() : FileAttributes.Directory;
        checks.Add(("new local leaf can be normalized without creating it", LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, MissingLeaf, allowMissing: true)));
        checks.Add(("missing rollout file rejected", !LocalPathPolicy.ValidateWithProbes(
            localFile, _ => DriveType.Fixed, MissingLeaf, requireFile: true)));
        checks.Add(("unresolved parent traversal rejected by probe boundary", !LocalPathPolicy.ValidateWithProbes(
            @"C:\synthetic\..\one.jsonl", _ => DriveType.Fixed, Ordinary)));
        checks.Add(("native resolver allows only known bare OS names", NativeLibraryPolicy.IsSystemLibrary("WINSQLITE3.DLL")
            && NativeLibraryPolicy.IsSystemLibrary("kernel32.dll")
            && !NativeLibraryPolicy.IsSystemLibrary(@"..\winsqlite3.dll")
            && !NativeLibraryPolicy.IsSystemLibrary("unknown.dll")));
        checks.Add(("application default native search is System32 only",
            typeof(NativeLibraryPolicy).Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths
                == DllImportSearchPath.System32));

        var root = Path.Combine(Path.GetTempPath(), "statusbar-path-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "one.jsonl");
            checks.Add(("new settings leaf allowed without creating it", LocalPathPolicy.IsAllowedWritePath(file)
                && !File.Exists(file)));
            File.WriteAllText(file, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"synthetic-path\"}}\n");
            checks.Add(("synthetic local root accepted", LocalPathPolicy.IsAllowedDirectory(root)));
            checks.Add(("synthetic rollout remains inside supplied root", RolloutPathPolicy.IsAllowed(root, file)));
            checks.Add(("discovery accepts root itself", RolloutPathPolicy.IsSafeDirectory(root, root)));
            checks.Add(("discovery rejects root parent", !RolloutPathPolicy.IsSafeDirectory(root, Path.GetDirectoryName(root)!)));
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                checks.Add(("opened handle matches expected local file", LocalPathPolicy.MatchesOpenedFile(stream, file)));
                checks.Add(("opened handle rejects a different pathname", !LocalPathPolicy.MatchesOpenedFile(stream,
                    Path.Combine(root, "different.jsonl"))));
            }
            var reader = new RolloutReader(file, maxTailBytes: 1024, sessionRoot: root);
            reader.Initialize();
            var bytes = reader.ParsedBytes;
            checks.Add(("synthetic cached reader initialized through local handle checks", bytes > 0
                && reader.State.SessionMetaId == "synthetic-path"));
            File.Delete(file);
            Directory.CreateDirectory(file);
            checks.Add(("cached reader rejects a path replaced with a directory on reopen",
                reader.TryUpdate() == RolloutUpdateResult.Unchanged && reader.ParsedBytes == bytes
                && !LocalPathPolicy.IsAllowedFile(file)));
            checks.Add(("write path rejects an existing directory", !LocalPathPolicy.IsAllowedWritePath(file)));
        }
        finally { Directory.Delete(root, recursive: true); }
        return checks;
    }
}
