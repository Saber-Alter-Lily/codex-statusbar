"""Source-only guards for local path/native boundaries. No application or external path access."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src/CodexStatusbar"
def read(name):
    return (SRC / name).read_text(encoding="utf-8")
checks = 0
def check(condition, description):
    global checks
    assert condition, description
    checks += 1
policy = read("LocalPathPolicy.cs")
body = policy.split("internal static bool ValidateWithProbes", 1)[1]
check(body.index("IsLocalAbsolutePathSyntax(path)") < body.index("driveType(root)") < body.index("attributes(current)"),
      "syntax and drive type precede metadata probes")
check("FileAttributes.ReparsePoint" in body and "index = -1" in body, "all ancestors including volume root checked")
check("DriveType.Network" not in body.split("return false;", 2)[1], "network drive not explicitly permitted")
check("GetFinalPathNameByHandle" in policy and "IsAllowedFile(normalized)" in policy, "opened handle checked and path revalidated")
reader = read("RolloutReader.cs").split("private FileStream? OpenRead()", 1)[1]
check(reader.index("LocalPathPolicy.IsAllowedFile(Path)") < reader.index("new FileStream("), "cached opens revalidate before open")
check("LocalPathPolicy.MatchesOpenedFile(stream, Path)" in reader, "cached reader checks resulting handle before reading")
check("RolloutPathPolicy.IsAllowed(_sessionRoot, Path)" in reader, "reader retains configured root boundary")
db = read("StateDatabase.cs")
check(db.index("LocalPathPolicy.IsAllowedFile(databasePath)") < db.index("sqlite3_open_v2(databasePathPointer"), "SQLite validates before native open")
native = read("NativeLibraryPolicy.cs")
check("[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]" in native, "assembly defaults to System32")
check("Path.Combine(systemDirectory, name)" in native and "NativeLibrary.SetDllImportResolver" in native,
      "known OS imports use absolute system directory")
check("SetDefaultDllDirectories" not in native, "framework/global DLL search policy unchanged")
tests = read("PathBoundarySelfTest.cs")
check("ValidateWithProbes" in tests and "calls == 0" in tests, "rejected paths test zero metadata probes")
check("GetTempPath()" in tests and "Directory.Delete(root" in tests, "runtime file tests use new disposable synthetic directory")
print(f"PASS: {checks} path/native source guards (not compilation or runtime validation)")
