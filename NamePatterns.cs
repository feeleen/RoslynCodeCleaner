using System.Text.RegularExpressions;

/// <summary>
/// Name-based pattern matcher for StartsWith / EndsWith / Contains / Regexes.
/// Used by both AdditionalIgnorePatterns (blacklist) and AdditionalIncludePatterns (whitelist).
/// </summary>
internal class NamePatterns
{
	private List<Func<string, bool>> _checks = [];

	public string[]? StartsWith { get; set; }
	public string[]? EndsWith { get; set; }
	public string[]? Contains { get; set; }
	public string[]? Regexes { get; set; }

	internal void Initialize()
	{
		var checks = new List<Func<string, bool>>();

		if (StartsWith != null && StartsWith.Length > 0)
		{
			var prefixes = StartsWith;
			checks.Add(name => prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
		}

		if (EndsWith != null && EndsWith.Length > 0)
		{
			var suffixes = EndsWith;
			checks.Add(name => suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
		}

		if (Contains != null && Contains.Length > 0)
		{
			var substrings = Contains;
			checks.Add(name => substrings.Any(c => name.Contains(c, StringComparison.OrdinalIgnoreCase)));
		}

		if (Regexes != null && Regexes.Length > 0)
		{
			foreach (var pattern in Regexes)
			{
				try
				{
					var regex = new Regex(pattern, RegexOptions.Compiled);
					checks.Add(name => regex.IsMatch(name));
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[WARNING] Invalid regex pattern '{pattern}': {ex.Message}");
				}
			}
		}

		_checks = checks;
	}

	/// <summary>
	/// Returns true if the name matches any of the configured patterns.
	/// </summary>
	internal bool Matches(string name)
	{
		foreach (var check in _checks)
		{
			if (check(name))
				return true;
		}
		return false;
	}
}
