using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

static class AnalysisHelpers
{
    // Cached settings (loaded once at startup)
    private static readonly Settings _cachedSettings;

    // Global exclusions (can be overridden by profile)
    internal static List<string> IgnoredFolders = [];
    internal static List<string> IgnoredAttributes = [];
    internal static HashSet<string> SkipBaseTypeNames = new(StringComparer.OrdinalIgnoreCase);
    internal static NamePatterns? AdditionalIgnorePatterns;
    internal static NamePatterns? AdditionalIncludePatterns;

    private const string settingsFileName = "appsettings.json";

    static AnalysisHelpers()
    {
        _cachedSettings = LoadSettings();
    }

    /// <summary>
    /// Resets exclusions to empty defaults (no profile)
    /// </summary>
    internal static void ResetToDefaults()
    {
        IgnoredFolders = [];
        IgnoredAttributes = [];
        SkipBaseTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AdditionalIgnorePatterns = null;
        AdditionalIncludePatterns = null;
    }

    /// <summary>
    /// Applies settings from profile
    /// </summary>
    internal static void ApplyProfile(Profile profile)
    {
        IgnoredFolders = profile.IgnoredFolders ?? [];
        IgnoredAttributes = profile.IgnoredAttributes ?? [];
        SkipBaseTypeNames = profile.SkipBaseTypeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AdditionalIgnorePatterns = profile.AdditionalIgnorePatterns;
        AdditionalIncludePatterns = profile.AdditionalIncludePatterns;

        AdditionalIgnorePatterns?.Initialize();
        AdditionalIncludePatterns?.Initialize();
    }

    private static Settings LoadSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, settingsFileName);

        if (!File.Exists(settingsPath))
            settingsPath = settingsFileName;

        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<Settings>(json);
                if (settings != null)
                    return settings;
            }
            catch
            {
				Console.WriteLine($"[INFO] {settingsFileName} file not found, using defaults..");
			}
        }

        return new Settings();
    }

    internal static Settings GetSettings() => _cachedSettings;

    internal static string GetLogFilePath(string? solutionPath = null)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var logFileName = $"log-{solutionName}-{timestamp}.log";
            return Path.Combine(Directory.GetCurrentDirectory(), logFileName);
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "default.log");
    }

    internal static bool IsIgnoredPath(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');
        return IgnoredFolders.Any(folder =>
        {
            var normalizedFolder = folder.Replace('\\', '/');
            return normalizedPath.Contains($"/{normalizedFolder}/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Returns true if any reference location is OUTSIDE the given declaration node.
    /// Works for both types (TypeDeclarationSyntax) and methods (MethodDeclarationSyntax).
    /// </summary>
    internal static bool HasExternalReferences(
        IEnumerable<ReferencedSymbol> references,
        SyntaxNode declarationNode,
        SyntaxTree declarationTree)
    {
        foreach (var refSymbol in references)
        {
            foreach (var location in refSymbol.Locations)
            {
                // NOTE: Do NOT skip IsCandidateLocation — in projects with compilation errors,
                // legitimate references are downgraded to "candidate" status.
                var refLocation = location.Location;

                // Different file → external reference
                if (refLocation.SourceTree != declarationTree)
                    return true;

                // Same file but outside the declaration span → external reference
                if (!declarationNode.Span.Contains(refLocation.SourceSpan))
                    return true;
            }
        }

        return false;
    }

    internal static void ReportProgress(ref long processed, long total)
    {
        var count = Interlocked.Increment(ref processed);
        if (count % 50 == 0)
            Console.WriteLine($"[INFO] Progress: {count}/{total} analyzed");
    }

    /// <summary>
    /// Returns true if <paramref name="word"/> appears in <paramref name="text"/>
    /// at a word boundary (not as a substring of a larger identifier).
    /// E.g. "Save" in "GetMethod(\"Save\")" → true, but "Save" in "SaveAndUpdate" → false.
    /// </summary>
    internal static bool IsWordBoundaryMatch(string text, string word)
    {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !IsWordChar(text[idx - 1]);
            bool rightOk = idx + word.Length >= text.Length || !IsWordChar(text[idx + word.Length]);
            if (leftOk && rightOk)
                return true;
            idx += 1;
        }
        return false;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

	internal static bool IsIgnoredAttributes(string attributeName)
	{
		return IgnoredAttributes.Any(attr =>
		{
			return attr.Equals(attributeName, StringComparison.OrdinalIgnoreCase);
		});
	}

	/// <summary>
	/// Returns true if any of the class's base types or interfaces match the configurable skip list
	/// or are used as parameter/field/property types elsewhere (likely DI-resolved).
	/// Uses typeAncestors as fallback when semantic chain breaks due to compilation errors.
	/// </summary>
	internal static bool HasPolymorphicBaseType(
        INamedTypeSymbol classSymbol,
        ConcurrentDictionary<string, byte> typesUsedAsParameters,
        ConcurrentDictionary<string, HashSet<string>> typeAncestors)
    {
        bool Matches(string name) => SkipBaseTypeNames.Contains(name) || typesUsedAsParameters.ContainsKey(name);

        var baseType = classSymbol.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            if (Matches(baseType.Name))
                return true;

            if (baseType.TypeKind == TypeKind.Error)
            {
                if (typeAncestors.TryGetValue(baseType.Name, out var ancestors))
                    foreach (var ancestor in ancestors)
                        if (Matches(ancestor))
                            return true;
                break;
            }

            baseType = baseType.BaseType;
        }

        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.ContainingNamespace?.ToString()?.StartsWith(nameof(System)) == true)
                continue;

            if (Matches(iface.Name))
                return true;
        }

        if (typeAncestors.TryGetValue(classSymbol.Name, out var classAncestors))
            foreach (var ancestor in classAncestors)
                if (Matches(ancestor))
                    return true;

        return false;
    }

    /// <summary>
    /// Collects all string literals from all syntax trees with their syntactic context.
    /// Shared between types and methods analyzers for --verify-text filtering.
    /// </summary>
    internal static async Task<List<StringLiteralEntry>> CollectStringLiteralsAsync(
        ConcurrentDictionary<ProjectId, Compilation> compilations)
    {
        var result = new ConcurrentQueue<StringLiteralEntry>();

        await Parallel.ForEachAsync(compilations, async (kvp, ct) =>
        {
            foreach (var tree in kvp.Value.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(ct);
                var fp = tree.FilePath;
                foreach (var token in root.DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.StringLiteralToken) || token.ValueText.Length < 3)
                        continue;

                    string? callingMethodName = null;
                    bool isActivatorContext = false;
                    bool isAttributeArgument = false;

                    var parent = token.Parent;
                    var argNode = parent?.Parent;
                    if (argNode is ArgumentSyntax)
                    {
                        var invocation = argNode.Parent?.Parent;
                        if (invocation is InvocationExpressionSyntax inv)
                        {
                            callingMethodName = inv.Expression switch
                            {
                                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                                IdentifierNameSyntax id => id.Identifier.Text,
                                _ => null
                            };
                        }
                        else if (argNode.Parent?.Parent is ObjectCreationExpressionSyntax objCreation)
                        {
                            var ctorTypeName = objCreation.Type switch
                            {
                                IdentifierNameSyntax id => id.Identifier.Text,
                                QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                                _ => null
                            };
                            if (ctorTypeName != null && (ctorTypeName.Contains(nameof(Activator)) || ctorTypeName.Contains("TypeLoader")))
                                isActivatorContext = true;
                        }
                    }
                    else if (parent?.Parent is AttributeArgumentSyntax)
                    {
                        isAttributeArgument = true;
                    }

                    result.Enqueue(new StringLiteralEntry(token.ValueText, fp, callingMethodName, isActivatorContext, isAttributeArgument));
                }
            }
        });

        return result.ToList();
    }

    /// <summary>
    /// Extracts type names used polymorphically across the codebase:
    /// parameters, fields, properties, generic type arguments, typeof(), and base type lists.
    /// </summary>
    internal static IEnumerable<string> ExtractDeclaredTypeNames(SyntaxNode root)
    {
        var seen = new HashSet<string>();

        foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (param.Type != null)
                foreach (var name in ExtractTypeNames(param.Type))
                    if (seen.Add(name)) yield return name;
        }

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var name in ExtractTypeNames(field.Declaration.Type))
                if (seen.Add(name)) yield return name;
        }

        foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            foreach (var name in ExtractTypeNames(prop.Type))
                if (seen.Add(name)) yield return name;
        }

        foreach (var typeArgList in root.DescendantNodes().OfType<TypeArgumentListSyntax>())
        {
            foreach (var typeArg in typeArgList.Arguments)
                foreach (var name in ExtractTypeNames(typeArg))
                    if (seen.Add(name)) yield return name;
        }

        foreach (var typeOfExpr in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            foreach (var name in ExtractTypeNames(typeOfExpr.Type))
                if (seen.Add(name)) yield return name;
        }

        foreach (var baseList in root.DescendantNodes().OfType<BaseListSyntax>())
        {
            foreach (var baseType in baseList.Types)
                foreach (var name in ExtractTypeNames(baseType.Type))
                    if (seen.Add(name)) yield return name;
        }
    }

    /// <summary>
    /// Recursively extracts simple type names from a TypeSyntax node,
    /// handling generics like IEnumerable&lt;MyType&gt; and nullable MyType?.
    /// </summary>
    internal static IEnumerable<string> ExtractTypeNames(TypeSyntax type)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                yield return id.Identifier.Text;
                break;
            case GenericNameSyntax generic:
                yield return generic.Identifier.Text;
                foreach (var arg in generic.TypeArgumentList.Arguments)
                    foreach (var name in ExtractTypeNames(arg))
                        yield return name;
                break;
            case QualifiedNameSyntax qualified:
                foreach (var name in ExtractTypeNames(qualified.Right))
                    yield return name;
                break;
            case NullableTypeSyntax nullable:
                foreach (var name in ExtractTypeNames(nullable.ElementType))
                    yield return name;
                break;
            case ArrayTypeSyntax array:
                foreach (var name in ExtractTypeNames(array.ElementType))
                    yield return name;
                break;
        }
    }
}

internal record StringLiteralEntry(
    string Value, string FilePath,
    string? CallingMethodName, bool IsActivatorContext, bool IsAttributeArgument);
