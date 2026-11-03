using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CodexStatusbar;

/// <summary>Applies only to this application's P/Invokes, not framework/native bundle loading.</summary>
internal static class NativeLibraryPolicy
{
    [ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(
        typeof(NativeLibraryPolicy).Assembly, Resolve);

    internal static bool IsSystemLibrary(string name) => name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("user32.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("psapi.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("winsqlite3.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("gdi32.dll", StringComparison.OrdinalIgnoreCase);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsSystemLibrary(name)) return IntPtr.Zero;
        // The absolute path avoids probing the application directory or native extraction cache
        // for these OS libraries. System32 also constrains their dependency resolution.
        // NativeLibrary.Load(string, Assembly, DllImportSearchPath?) does not invoke registered
        // DllImportResolvers, so this does not recurse into Resolve (per the .NET API contract).
        var systemDirectory = Environment.SystemDirectory;
        if (!LocalPathPolicy.IsLocalAbsolutePathSyntax(systemDirectory))
            throw new DllNotFoundException("The Windows system directory is unavailable.");
        return NativeLibrary.Load(Path.Combine(systemDirectory, name), assembly, DllImportSearchPath.System32);
    }
}
