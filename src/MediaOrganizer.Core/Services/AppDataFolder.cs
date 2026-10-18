namespace MediaOrganizer.Core.Services;

/// <summary>The folder holding settings, caches and logs. Setting the
/// environment variable <see cref="OverrideVariable"/> redirects all of it, so
/// a run can use a throwaway folder and never touch real data.</summary>
public static class AppDataFolder
{
    public const string OverrideVariable = "MEDIAORGANIZER_DATA";

    public static string Root { get; } = Resolve(Environment.GetEnvironmentVariable(OverrideVariable));

    internal static string Resolve(string? overrideValue) =>
        !string.IsNullOrWhiteSpace(overrideValue)
            ? overrideValue
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaOrganizer");
}
