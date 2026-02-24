using System.Text.RegularExpressions;

/// <summary>
/// Additional ignore patterns
/// </summary>
internal class AdditionalIgnorePatterns
{
    private List<Func<string, bool>> _checks = [];

    public string[]? StartsWith { get; set; }
    public string[]? EndsWith { get; set; }
    public string[]? Contains { get; set; }
    public string[]? Regexes { get; set; }

    internal void Initialize()
    {
        var checks = new List<Func<string, bool>>();

        // Pre-compile StartsWith checks
        if (StartsWith != null && StartsWith.Length > 0)
        {
            var prefixes = StartsWith;
            checks.Add(name => prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        // Pre-compile EndsWith checks
        if (EndsWith != null && EndsWith.Length > 0)
        {
            var suffixes = EndsWith;
            checks.Add(name => suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        }

        // Pre-compile Contains checks
        if (Contains != null && Contains.Length > 0)
        {
            var substrings = Contains;
            checks.Add(name => substrings.Any(c => name.Contains(c, StringComparison.OrdinalIgnoreCase)));
        }

        // Pre-compile Regex checks
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

    internal bool ShouldIgnore(string name)
    {
        foreach (var check in _checks)
        {
            if (check(name))
                return true;
        }
        return false;
    }
}
