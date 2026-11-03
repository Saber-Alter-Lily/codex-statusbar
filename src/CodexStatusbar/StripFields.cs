namespace CodexStatusbar;

[Flags]
internal enum StripField
{
    None = 0, Speed = 1, Total = 2, CumulativeCache = 4, Ttft = 8, RequestContext = 16
}

internal static class StripFieldRules
{
    public const StripField Default = StripField.Speed | StripField.Total | StripField.CumulativeCache | StripField.Ttft;
    public const StripField Supported = Default | StripField.RequestContext;
    public static StripField Sanitize(StripField fields) => (fields & Supported) is var value && value != StripField.None ? value : Default;
}
