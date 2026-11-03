using System.Diagnostics;

namespace CodexStatusbar;

internal static class RestartArguments
{
    internal static ProcessStartInfo Create(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}

internal static class InteractionSafetySelfTest
{
    internal static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var args = new[] { "--settings", "C:\\synthetic profile\\设置.json", "trailing\\", "quoted\"value", "", "--restart-wait" };
        var start = RestartArguments.Create("synthetic.exe", args);
        var defaults = OverlaySettings.CreateDefault();
        return new (string Name, bool Passed)[]
        {
            ("restart preserves each synthetic argument without launching", start.ArgumentList.SequenceEqual(args) && start.Arguments.Length == 0 && !start.UseShellExecute),
            ("global position hotkey defaults off", !defaults.PositionHotkeyEnabled),
            ("missing hotkey setting remains off", !OverlaySettings.ParseJson("{}").Settings.PositionHotkeyEnabled),
            ("explicit hotkey setting round trips", OverlaySettings.ParseJson("{\"positionHotkeyEnabled\":true}").Settings.PositionHotkeyEnabled),
            ("diagnostics block and disk budgets finite", DebugDiagnostics.MaximumBlockCharacters == 16384 && DebugDiagnostics.MaximumLogBytes == 1048576)
        };
    }
}
