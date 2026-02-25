/// <summary>
/// Application root settings
/// </summary>
internal class Settings
{
    // Default active profile name
    public string? ActiveProfile { get; set; }

    // Collection of profiles
    public Dictionary<string, Profile>? Profiles { get; set; }

    // Fallback solution path (if profile doesn't specify one)
    public string? SolutionPath { get; set; }
}
