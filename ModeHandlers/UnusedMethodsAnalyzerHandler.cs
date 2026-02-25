using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Concurrent;
using System.Diagnostics;

static class UnusedMethodsAnalyzerHandler
{
    // Method names that are framework-reserved and should never be flagged as unused
    static readonly HashSet<string> FrameworkMethodNames = new(StringComparer.Ordinal)
    {
        "Main", "Configure", "ConfigureServices", "ConfigureWebHost", "ConfigureWebHostDefaults",
        "Dispose", "DisposeAsync",
        "ToString", "Equals", "GetHashCode", "CompareTo", "Clone", "MemberwiseClone",
        "GetObjectData",
        "OnConfiguring", "OnModelCreating",
        "InvokeAsync", "Invoke"
    };

    // Attributes that mark methods as framework-used (not referenced in code but called by runtime)
    static readonly HashSet<string> SkipMethodAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "Route", "ApiExplorerSettings", "NonAction",
        "JsonConstructor", "OnDeserialized", "OnDeserializing", "OnSerialized", "OnSerializing",
        "Fact", "Theory", "Test", "TestMethod", "TestCase",
        "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown",
        "GlobalSetup", "GlobalCleanup", "IterationSetup", "IterationCleanup", "Benchmark"
    };

    internal static async Task<List<(string name, string containingType, string signature, string filePath, int lineNumber, string fullLine)>> FindAsync(
        AnalysisContext context, StreamWriter resultWriter, bool verifyText = false)
    {
        var unusedMethods = new ConcurrentQueue<(string name, string containingType, string signature, string filePath, int lineNumber, string fullLine)>();
        var sw = Stopwatch.StartNew();

        try
        {
            // Phase A: Collect method candidates
            Console.WriteLine($"[INFO] Collecting method candidates...");
            var candidates = new List<(IMethodSymbol symbol, MethodDeclarationSyntax decl, SyntaxTree tree, string filePath)>();

            foreach (var project in context.Solution.Projects)
            {
                if (!context.Compilations.TryGetValue(project.Id, out var compilation))
                    continue;

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    string filePath = syntaxTree.FilePath;
                    if (AnalysisHelpers.IsIgnoredPath(filePath))
                        continue;

                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync();

                    foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol symbol)
                            continue;

                        if (!ShouldAnalyzeMethod(symbol))
                            continue;

                        candidates.Add((symbol, methodDecl, syntaxTree, filePath));
                    }
                }
            }

            Console.WriteLine($"[INFO] Found {candidates.Count} method candidates in {sw.Elapsed.TotalSeconds:F1}s");
            sw.Restart();

            // Phase B: Check references in parallel
            Console.WriteLine($"[INFO] Analyzing method references...");
            // Reduced parallelism to prevent deadlock with SymbolFinder
            int maxParallelism = Math.Min(Environment.ProcessorCount / 2, 4);
            if (maxParallelism < 2) maxParallelism = 2;
            long processed = 0;

            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                async (candidate, ct) =>
                {
                    var (symbol, methodDecl, syntaxTree, filePath) = candidate;
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var refs = await SymbolFinder.FindReferencesAsync(symbol, context.Solution, ct);
                        if (AnalysisHelpers.HasExternalReferences(refs, methodDecl, syntaxTree))
                        {
                            AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                            return;
                        }

                        // Method has no external references → unused
                        var lineSpan = syntaxTree.GetLineSpan(methodDecl.Span);
                        int lineNumber = lineSpan.StartLinePosition.Line + 1;
                        string fullLine = syntaxTree.GetText().Lines[lineNumber - 1].ToString().Trim();
                        string containingType = symbol.ContainingType?.Name ?? "?";
                        string signature = BuildMethodSignature(symbol);

                        unusedMethods.Enqueue((symbol.Name, containingType, signature, filePath, lineNumber, fullLine));
                        var msg = $"[UNUSED] [method] {signature} at {filePath} (Line {lineNumber})";
                        Console.WriteLine(msg);
                        lock (resultWriter) resultWriter.WriteLine(msg);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Error analyzing method {symbol.Name} in {filePath}: {ex.Message}");
                    }

                    AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                });

            Console.WriteLine($"[INFO] Reference analysis done in {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to analyze methods: {ex.Message}");
        }

        var result = unusedMethods.ToList();

        // Phase C: Optional text-based verification (string literals with reflection patterns)
        if (verifyText && result.Count > 0)
        {
            Console.WriteLine($"[INFO] Phase C: Text verification of {result.Count} unused methods...");

            var stringLiterals = await AnalysisHelpers.CollectStringLiteralsAsync(context.Compilations);
            var methodReflectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GetMethod", "GetMethods", "GetMember", "GetMembers",
                "InvokeMember" // note: "Invoke" excluded — too generic (delegates, event handlers)
            };

            int reflectionCount = stringLiterals.Count(s =>
                s.CallingMethodName != null && methodReflectionNames.Contains(s.CallingMethodName));
            Console.WriteLine($"[INFO] Collected {stringLiterals.Count} string literals ({reflectionCount} in reflection context)");

            var verified = new List<(string name, string containingType, string signature, string filePath, int lineNumber, string fullLine)>();
            int keptByStringLiteral = 0;

            foreach (var item in result)
            {
                // For methods: only check string literals (skip identifier check — method names are too common)
                // Reflection context: require exact match (not substring or word-boundary)
                // Non-reflection: only ClassName.MethodName pattern (bare exact match like "Add"=="Add" is too noisy)
                bool foundInStrings = false;
                string? matchExample = null;

                foreach (var sl in stringLiterals)
                {
                    if (string.Equals(sl.FilePath, item.filePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!sl.Value.Contains(item.name, StringComparison.Ordinal))
                        continue;

                    bool isReflection = sl.CallingMethodName != null
                        && methodReflectionNames.Contains(sl.CallingMethodName);

                    if (isReflection)
                    {
                        // Reflection context: require exact match — GetMethod("Save") means val IS "Save"
                        if (string.Equals(sl.Value, item.name, StringComparison.Ordinal))
                        {
                            foundInStrings = true;
                            matchExample = $"[reflection] \"{sl.Value}\" in {Path.GetFileName(sl.FilePath)}";
                            break;
                        }
                        continue;
                    }

                    // Non-reflection: only ClassName.MethodName pattern
                    if (sl.Value.Contains($"{item.containingType}.{item.name}", StringComparison.Ordinal))
                    {
                        foundInStrings = true;
                        matchExample = $"[qualified] \"{sl.Value}\" in {Path.GetFileName(sl.FilePath)}";
                        break;
                    }
                }

                if (foundInStrings)
                {
                    var msg = $"[KEPT-BY-STRING] {item.signature} — {matchExample}";
                    Console.WriteLine(msg);
                    lock (resultWriter) resultWriter.WriteLine(msg);
                    keptByStringLiteral++;
                    continue;
                }

                verified.Add(item);
            }

            Console.WriteLine($"[INFO] Text verification: {keptByStringLiteral} kept by string literals, {verified.Count} confirmed unused");
            result = verified;
        }

        Console.WriteLine($"[INFO] Total unused methods: {result.Count}");
        return result;
    }

    /// <summary>
    /// Returns true if the method should be analyzed for unused detection.
    /// Returns false for methods that should be skipped (interface impls, overrides, framework methods, etc.).
    /// </summary>
    static bool ShouldAnalyzeMethod(IMethodSymbol symbol)
    {
        // Only ordinary methods (skip constructors, operators, property accessors, finalizers, event accessors)
        if (symbol.MethodKind != MethodKind.Ordinary)
            return false;

        // Skip override methods — may be called polymorphically via base reference
        if (symbol.IsOverride)
            return false;

        // Skip abstract methods — implementation in derived classes
        if (symbol.IsAbstract)
            return false;

        // Skip partial methods
        if (symbol.IsPartialDefinition || symbol.PartialImplementationPart != null || symbol.PartialDefinitionPart != null)
            return false;

        // Skip explicit interface implementations
        if (symbol.ExplicitInterfaceImplementations.Length > 0)
            return false;

        // Skip implicit interface implementations
        if (symbol.ContainingType != null)
        {
            foreach (var iface in symbol.ContainingType.AllInterfaces)
            {
                foreach (var member in iface.GetMembers(symbol.Name).OfType<IMethodSymbol>())
                {
                    var impl = symbol.ContainingType.FindImplementationForInterfaceMember(member);
                    if (SymbolEqualityComparer.Default.Equals(impl, symbol))
                        return false;
                }
            }
        }

        // Skip framework-reserved method names
        if (FrameworkMethodNames.Contains(symbol.Name))
            return false;

        // Skip methods with framework attributes
        foreach (var attr in symbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (attrName == null) continue;
            var baseName = attrName.EndsWith("Attribute") ? attrName[..^9] : attrName;
            if (SkipMethodAttributeNames.Contains(baseName) || SkipMethodAttributeNames.Contains(attrName))
                return false;
        }

        // Skip public methods in controller/hub classes (actions are called by routing, not code)
        if (symbol.DeclaredAccessibility == Accessibility.Public && IsControllerAction(symbol))
            return false;

        // Skip methods in classes whose name matches AdditionalIgnorePatterns
        if (symbol.ContainingType != null
            && AnalysisHelpers.AdditionalIgnorePatterns?.ShouldIgnore(symbol.ContainingType.Name) == true)
            return false;

        return true;
    }

    /// <summary>
    /// Returns true if the method's containing type inherits from a controller/hub base type.
    /// </summary>
    static bool IsControllerAction(IMethodSymbol symbol)
    {
        var containingType = symbol.ContainingType;
        if (containingType == null)
            return false;

        var baseType = containingType.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            if (AnalysisHelpers.SkipBaseTypeNames.Contains(baseType.Name))
                return true;
            baseType = baseType.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Builds a human-readable method signature: "ClassName.MethodName&lt;T&gt;(ParamType1, ParamType2)"
    /// </summary>
    static string BuildMethodSignature(IMethodSymbol symbol)
    {
        var typeName = symbol.ContainingType?.Name ?? "?";
        var typeParams = symbol.TypeParameters.Length > 0
            ? $"<{string.Join(", ", symbol.TypeParameters.Select(t => t.Name))}>"
            : "";
        var paramList = string.Join(", ", symbol.Parameters.Select(p => p.Type.Name));
        return $"{typeName}.{symbol.Name}{typeParams}({paramList})";
    }

    /// <summary>
    /// Removes unused methods from files using Roslyn syntax tree manipulation.
    /// </summary>
    internal static async Task AutoDelete(
        List<(string name, string containingType, string signature, string filePath, int lineNumber, string fullLine)> unusedMethods,
        StreamWriter resultWriter,
        AnalysisContext context)
    {
        var editGroups = unusedMethods
            .GroupBy(m => m.filePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"[INFO] Editing {editGroups.Count} files to remove unused methods...");
        int edited = 0;
        var editedContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in editGroups)
        {
            try
            {
                var text = await File.ReadAllTextAsync(group.Key);
                var tree = CSharpSyntaxTree.ParseText(text);
                var root = await tree.GetRootAsync();

                var nodesToRemove = new List<SyntaxNode>();
                foreach (var item in group)
                {
                    var methodNode = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(n =>
                            n.Identifier.Text == item.name
                            && tree.GetLineSpan(n.Span).StartLinePosition.Line == item.lineNumber - 1);

                    if (methodNode != null)
                        nodesToRemove.Add(methodNode);
                    else
                        Console.WriteLine($"[WARN] Could not locate {item.signature} at line {item.lineNumber} in {group.Key}");
                }

                if (nodesToRemove.Count == 0)
                    continue;

                var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                if (newRoot == null)
                    continue;

                var newText = newRoot.ToFullString();
                while (newText.Contains("\r\n\r\n\r\n"))
                    newText = newText.Replace("\r\n\r\n\r\n", "\r\n\r\n");
                while (newText.Contains("\n\n\n"))
                    newText = newText.Replace("\n\n\n", "\n\n");

                await File.WriteAllTextAsync(group.Key, newText);
                edited++;
                editedContents[group.Key] = newText;

                var names = string.Join(", ", group.Select(m => m.signature));
                var msg = $"[EDITED] {group.Key} — removed methods: {names}";
                Console.WriteLine(msg);
                resultWriter.WriteLine(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to edit {group.Key}: {ex.Message}");
            }
        }

        Console.WriteLine($"[INFO] Edited {edited}/{editGroups.Count} files");

        // Cleanup usings orphaned by method removal
        if (editedContents.Count > 0)
        {
            var updatedSolution = UnusedUsingsCleanerHandler.BuildUpdatedSolution(
                context.Solution,
                deletedFiles: Array.Empty<string>(),
                editedFiles: editedContents);

            await UnusedUsingsCleanerHandler.CleanupOrphanedNamespaceUsingsAsync(updatedSolution, resultWriter);
        }
    }
}
