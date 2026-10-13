namespace FSRestore.Models;

public sealed record OpenAppInfo(string ProcessName, string? ProcessPath, string FriendlyName)
{
    public string DisplayName => FriendlyName;
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool RunAtStartup { get; set; }

    // Games: when one of these loses foreground / minimizes, restore targets.
    public List<string> GameProcesses { get; set; } = [];

    // Targets: these apps get restored to fullscreen after a game minimizes.
    public List<string> TargetProcesses { get; set; } = [];

    // Process name → exe path, captured when the app was running. Lets us
    // show icons/names for added processes even after they close.
    public Dictionary<string, string> KnownProcessPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
