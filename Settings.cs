/// <summary>
/// Application root settings
/// </summary>
internal class Settings
{
    // Default active profile name
    public string? ActiveProfile { get; set; }

    // Collection of profiles
    public Dictionary<string, Profile>? Profiles { get; set; }

    // Default settings (if no profile is specified)
    public string? SolutionPath { get; set; }
    public List<string>? IgnoredFolders { get; set; }
    public List<string>? IgnoredAttributes { get; set; }
    public HashSet<string>? SkipBaseTypeNames { get; set; }
    public AdditionalIgnorePatterns? AdditionalIgnorePatterns { get; set; }
}
