/// <summary>
/// Settings profile — complete snapshot of all parameters
/// </summary>
internal class Profile
{
	// Main settings
	public string? SolutionPath { get; set; }
	public string? Mode { get; set; }
	public bool AutoDelete { get; set; }
	public bool VerifyText { get; set; }
	public string? LogPath { get; set; }

	// Exclusions
	public List<string>? IgnoredFolders { get; set; }
	public List<string>? IgnoredAttributes { get; set; }
	public HashSet<string>? SkipBaseTypeNames { get; set; }
	public NamePatterns? AdditionalIgnorePatterns { get; set; }
	public NamePatterns? AdditionalIncludePatterns { get; set; }
}