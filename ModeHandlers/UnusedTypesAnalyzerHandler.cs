using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Concurrent;
using System.Diagnostics;

static class UnusedTypesAnalyzerHandler
{
    internal static async Task<List<(string name, string filePath, int lineNumber, string fullLine, string action)>> FindAsync(
        AnalysisContext context, StreamWriter resultWriter, bool verifyText = false)
    {
        var unusedClasses = new ConcurrentQueue<(string name, string filePath, int lineNumber, string fullLine)>();
        var typeCountPerFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();

        try
        {
            // Phase 2: Collect all candidate type symbols — classes, interfaces, structs, records
            // Also counts total public types per file for DELETE vs EDIT decision
            Console.WriteLine($"[INFO] Collecting type candidates...");
            var candidates = new List<(INamedTypeSymbol symbol, TypeDeclarationSyntax decl, SyntaxTree tree, string filePath)>();

            foreach (var project in context.Solution.Projects)
            {
                if (!context.Compilations.TryGetValue(project.Id, out var compilation))
                    continue;

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    string filePath = syntaxTree.FilePath;
                    bool isIgnored = AnalysisHelpers.IsIgnoredPath(filePath);

                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync();

                    int publicTypeCount = 0;
                    foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                        if (typeSymbol == null || typeSymbol.DeclaredAccessibility != Accessibility.Public)
                            continue;

                        publicTypeCount++;

                        if (isIgnored)
                            continue;

                        // Name-based filters (apply to classes only — interfaces/structs/records have different naming)
                        if (typeDecl is ClassDeclarationSyntax)
                        {
                            var name = typeSymbol.Name;
                            // Whitelist: if configured, only analyze matching classes
                            if (AnalysisHelpers.AdditionalIncludePatterns != null
                                && !AnalysisHelpers.AdditionalIncludePatterns.Matches(name))
                                continue;
                            // Blacklist: skip matching classes
                            if (AnalysisHelpers.AdditionalIgnorePatterns?.Matches(name) == true)
                                continue;
                        }

                        candidates.Add((typeSymbol, typeDecl, syntaxTree, filePath));
                    }

                    // Count enums and delegates too — they aren't candidates for removal
                    // but must be counted for the DELETE vs EDIT decision
                    foreach (var _ in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                        publicTypeCount++;
                    foreach (var _ in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
                        publicTypeCount++;

                    if (publicTypeCount > 0)
                        typeCountPerFile[filePath] = publicTypeCount;
                }
            }

            Console.WriteLine($"[INFO] Found {candidates.Count} candidate types (classes + interfaces + structs + records)");

            // Phase 2.6: Auto-detect types used polymorphically (as parameter/field/property types)
            Console.WriteLine($"[INFO] Detecting polymorphic base types...");
            var typesUsedAsParameters = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(context.Compilations, async (kvp, ct) =>
            {
                var compilation = kvp.Value;
                foreach (var tree in compilation.SyntaxTrees)
                {
                    var root = await tree.GetRootAsync(ct);
                    foreach (var typeName in AnalysisHelpers.ExtractDeclaredTypeNames(root))
                        typesUsedAsParameters.TryAdd(typeName, 0);
                }
            });

            Console.WriteLine($"[INFO] Found {typesUsedAsParameters.Count} types used in polymorphic declarations in {sw.Elapsed.TotalSeconds:F1}s");

            // Phase 2.7: Build type ancestry lookup
            Console.WriteLine($"[INFO] Building type ancestry lookup...");
            var typeAncestors = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in context.Compilations)
            {
                var compilation = kvp.Value;
                if (context.ProjectsWithErrors.Contains(kvp.Key))
                    continue;

                foreach (var tree in compilation.SyntaxTrees)
                {
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync();

                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        if (model.GetDeclaredSymbol(td) is not INamedTypeSymbol symbol)
                            continue;

                        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var bt = symbol.BaseType;
                        while (bt != null && bt.SpecialType != SpecialType.System_Object)
                        {
                            ancestors.Add(bt.Name);
                            bt = bt.BaseType;
                        }
                        foreach (var iface in symbol.AllInterfaces)
                        {
                            if (iface.ContainingNamespace?.ToString()?.StartsWith("System") != true)
                                ancestors.Add(iface.Name);
                        }

                        if (ancestors.Count > 0)
                            typeAncestors.TryAdd(symbol.Name, ancestors);
                    }
                }
            }

            Console.WriteLine($"[INFO] Type ancestry lookup built: {typeAncestors.Count} types in {sw.Elapsed.TotalSeconds:F1}s");
            sw.Restart();

            // Phase 2.8: Detect assembly scanning patterns
            Console.WriteLine($"[INFO] Detecting assembly scanning patterns...");
            var assemblyScanTypes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(context.Compilations, async (kvp, ct) =>
            {
                foreach (var tree in kvp.Value.SyntaxTrees)
                {
                    var root = await tree.GetRootAsync(ct);
                    var fileName = Path.GetFileName(tree.FilePath);

                    foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                    {
                        var methodName = memberAccess.Name.Identifier.Text;
                        if (methodName is not ("IsAssignableFrom" or "IsSubclassOf" or "IsInstanceOfType"))
                            continue;

                        if (memberAccess.Expression is TypeOfExpressionSyntax typeOfLeft)
                        {
                            foreach (var name in AnalysisHelpers.ExtractTypeNames(typeOfLeft.Type))
                                assemblyScanTypes.TryAdd(name, $"typeof({name}).{methodName} in {fileName}");
                        }
                    }

                    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        string? methodName = invocation.Expression switch
                        {
                            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                            IdentifierNameSyntax id => id.Identifier.Text,
                            _ => null
                        };
                        if (methodName == null)
                            continue;

                        if (methodName is "IsSubclassOf" or "IsAssignableFrom" or "IsInstanceOfType"
                            or "GetCustomAttribute" or "GetCustomAttributes" or "IsDefined")
                        {
                            foreach (var arg in invocation.ArgumentList.Arguments)
                            {
                                if (arg.Expression is TypeOfExpressionSyntax typeOfArg)
                                    foreach (var name in AnalysisHelpers.ExtractTypeNames(typeOfArg.Type))
                                        assemblyScanTypes.TryAdd(name, $".{methodName}(typeof({name})) in {fileName}");
                            }
                        }

                        if (methodName.StartsWith("GetCustomAttribute") || methodName == "IsDefined"
                            || methodName == "OfType" || methodName == "GetService" || methodName == "GetRequiredService")
                        {
                            if (invocation.Expression is MemberAccessExpressionSyntax ma2
                                && ma2.Name is GenericNameSyntax genericName)
                            {
                                foreach (var typeArg in genericName.TypeArgumentList.Arguments)
                                    foreach (var name in AnalysisHelpers.ExtractTypeNames(typeArg))
                                        assemblyScanTypes.TryAdd(name, $".{methodName}<{name}>() in {fileName}");
                            }
                        }
                    }
                }
            });

            if (assemblyScanTypes.Count > 0)
            {
                Console.WriteLine($"[INFO] Detected {assemblyScanTypes.Count} types used in assembly scanning:");
                foreach (var kvp2 in assemblyScanTypes.OrderBy(x => x.Key))
                {
                    Console.WriteLine($"  [SCAN] {kvp2.Key} — {kvp2.Value}");
                    lock (resultWriter) resultWriter.WriteLine($"[SCAN-SKIP] {kvp2.Key} — {kvp2.Value}");
                    typesUsedAsParameters.TryAdd(kvp2.Key, 0);
                }
            }
            Console.WriteLine($"[INFO] Assembly scanning detection done in {sw.Elapsed.TotalSeconds:F1}s");
            sw.Restart();

            // Phase 3: Check references in parallel
            Console.WriteLine($"[INFO] Analyzing references...");
            // Reduced parallelism to prevent deadlock with SymbolFinder
            int maxParallelism = Math.Min(Environment.ProcessorCount / 2, 4);
            if (maxParallelism < 2) maxParallelism = 2;
            long processed = 0;
            int skippedByIndex = 0;
            int skippedByBaseType = 0;
            int analyzedSemantic = 0;
            long lastProgressReport = 0; // atomic via Interlocked

            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                async (candidate, ct) =>
                {
                    var (typeSymbol, typeDecl, syntaxTree, filePath) = candidate;
                    bool isClass = typeDecl is ClassDeclarationSyntax;
                    string typeKind = typeDecl switch
                    {
                        InterfaceDeclarationSyntax => "interface",
                        StructDeclarationSyntax => "struct",
                        RecordDeclarationSyntax => "record",
                        _ => "class"
                    };
                    try
                    {
                        if (isClass)
                        {
                            if (typeSymbol.GetAttributes().Any(a =>
								AnalysisHelpers.IsIgnoredAttributes(a.AttributeClass?.Name ?? string.Empty)))
                            {
                                Interlocked.Increment(ref skippedByBaseType);
                                AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                                return;
                            }

                            if (AnalysisHelpers.HasPolymorphicBaseType(typeSymbol, typesUsedAsParameters, typeAncestors))
                            {
                                Interlocked.Increment(ref skippedByBaseType);
                                AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                                return;
                            }
                        }

                        ct.ThrowIfCancellationRequested();
                        var typeRefs = await SymbolFinder.FindReferencesAsync(typeSymbol, context.Solution, ct);
                        if (AnalysisHelpers.HasExternalReferences(typeRefs, typeDecl, syntaxTree))
                        {
                            AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                            return;
                        }

                        bool nameUsedElsewhere = context.IdentifierToFiles.TryGetValue(typeSymbol.Name, out var fileSet)
                            && fileSet.Keys.Any(f => !string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));

                        bool isStaticClass = isClass && typeDecl.Modifiers.Any(SyntaxKind.StaticKeyword);

                        if (nameUsedElsewhere || isStaticClass)
                        {
                            Interlocked.Increment(ref analyzedSemantic);

                            foreach (var member in typeSymbol.GetMembers().Where(m => !m.IsImplicitlyDeclared))
                            {
                                bool memberNameUsedElsewhere = context.IdentifierToFiles.TryGetValue(member.Name, out var memberFileSet)
                                    && memberFileSet.Keys.Any(f => !string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));

                                if (!memberNameUsedElsewhere)
                                    continue;

                                // Skip standard methods — they almost always have false positives
                                if (member.Name is "ToString" or "GetHashCode" or "Equals" or "Finalize" or "GetType")
                                {
                                    continue;
                                }

                                // Debug: log before finding references for type member
                                var memberStopwatch = Stopwatch.StartNew();
                                Console.WriteLine($"[DEBUG] Finding refs for member '{member.Name}' ({member.Kind}) in {typeSymbol.Name}");

                                // Find references with timeout
                                using var ctsMember = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                                try
                                {
                                    var refs = await SymbolFinder.FindReferencesAsync(member, context.Solution, ctsMember.Token);
                                    memberStopwatch.Stop();

                                    if (memberStopwatch.Elapsed.TotalSeconds > 2)
                                    {
                                        Console.WriteLine($"[WARNING] Slow member: '{member.Name}' in {typeSymbol.Name} took {memberStopwatch.Elapsed.TotalSeconds:F1}s");
                                    }

                                    if (AnalysisHelpers.HasExternalReferences(refs, typeDecl, syntaxTree))
                                    {
                                        AnalysisHelpers.ReportProgress(ref processed, candidates.Count);
                                        return;
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    Console.WriteLine($"[WARNING] Timeout (>2min) finding refs for '{member.Name}' in {typeSymbol.Name} — skipping member");
                                }
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref skippedByIndex);
                        }

                        var lineSpan = syntaxTree.GetLineSpan(typeDecl.Span);
                        int lineNumber = lineSpan.StartLinePosition.Line + 1;
                        string fullLine = syntaxTree.GetText().Lines[lineNumber - 1].ToString();

                        unusedClasses.Enqueue((typeSymbol.Name, filePath, lineNumber, fullLine));
                        int totalTypesInFile = typeCountPerFile.GetValueOrDefault(filePath, 1);
                        var msg = $"[UNUSED] [{typeKind}] {typeSymbol.Name} at {filePath} (Line {lineNumber}) [Types in file: {totalTypesInFile}]";
                        Console.WriteLine(msg);
                        lock (resultWriter)
                            resultWriter.WriteLine(msg);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Error analyzing {typeKind} {typeSymbol.Name} in {filePath}: {ex.Message}");
                    }

                    AnalysisHelpers.ReportProgress(ref processed, candidates.Count);

                    // Debug: output progress every 100 items
                    var currentProcessed2 = Interlocked.Read(ref processed);
                    var lastReport = Interlocked.Read(ref lastProgressReport);
                    if (currentProcessed2 - lastReport >= 100)
                    {
                        if (Interlocked.CompareExchange(ref lastProgressReport, currentProcessed2, lastReport) == lastReport)
                            Console.WriteLine($"[DEBUG] Progress: {currentProcessed2}/{candidates.Count}");
                    }
                });

            Console.WriteLine($"[INFO] Skipped by base type: {skippedByBaseType}, index skipped (fast path): {skippedByIndex}, semantic analyzed: {analyzedSemantic}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to process solution: {ex.Message}");
        }

        // Determine DELETE vs EDIT action per unused class
        var rawList = unusedClasses.ToList();
        var unusedCountPerFile = rawList.GroupBy(c => c.filePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var result = rawList.Select(c =>
        {
            int totalTypes = typeCountPerFile.GetValueOrDefault(c.filePath, 1);
            int unusedInFile = unusedCountPerFile.GetValueOrDefault(c.filePath, 0);
            string action = unusedInFile >= totalTypes ? "DELETE" : "EDIT";
            return (c.name, c.filePath, c.lineNumber, c.fullLine, action);
        }).ToList();

        // Phase 4: Optional text-based verification
        if (verifyText && result.Count > 0)
        {
            Console.WriteLine($"[INFO] Phase 4: Text verification of {result.Count} unused types...");

            var stringLiterals = await AnalysisHelpers.CollectStringLiteralsAsync(context.Compilations);
            var typeReflectionMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GetType", "CreateInstance", "CreateInstanceFrom",
                "GetInterface", "GetTypeByName", "Load",
                "Invoke", "InvokeMember"
            };

            int reflectionCount = stringLiterals.Count(s =>
                (s.CallingMethodName != null && typeReflectionMethods.Contains(s.CallingMethodName))
                || s.IsActivatorContext || s.IsAttributeArgument);
            Console.WriteLine($"[INFO] Collected {stringLiterals.Count} string literals ({reflectionCount} in reflection context)");

            var verified = new List<(string name, string filePath, int lineNumber, string fullLine, string action)>();
            int keptByIdentifier = 0, keptByStringLiteral = 0, verifyProcessed = 0;

            foreach (var item in result)
            {
                verifyProcessed++;
                if (verifyProcessed % 50 == 0)
                    Console.WriteLine($"[INFO] Phase 4 progress: {verifyProcessed}/{result.Count} types verified");

                bool identifierInOtherFiles = context.IdentifierToFiles.TryGetValue(item.name, out var files)
                    && files.Keys.Any(f => !string.Equals(f, item.filePath, StringComparison.OrdinalIgnoreCase));

                if (identifierInOtherFiles)
                {
                    var otherFiles = files!.Keys
                        .Where(f => !string.Equals(f, item.filePath, StringComparison.OrdinalIgnoreCase))
                        .Take(3).ToList();
                    var msg = $"[KEPT-BY-CODE] {item.name} — identifier found in: {string.Join(", ", otherFiles.Select(Path.GetFileName))}";
                    Console.WriteLine(msg);
                    lock (resultWriter) resultWriter.WriteLine(msg);
                    keptByIdentifier++;
                    continue;
                }

                bool foundInStrings = false;
                string? matchExample = null;

                foreach (var sl in stringLiterals)
                {
                    if (string.Equals(sl.FilePath, item.filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!sl.Value.Contains(item.name, StringComparison.Ordinal))
                        continue;

                    bool isReflection = (sl.CallingMethodName != null && typeReflectionMethods.Contains(sl.CallingMethodName))
                        || sl.IsActivatorContext || sl.IsAttributeArgument;

                    if (isReflection)
                    {
                        foundInStrings = true;
                        matchExample = $"[reflection] \"{sl.Value}\" in {Path.GetFileName(sl.FilePath)}";
                        break;
                    }

                    var val = sl.Value;
                    bool isQualified =
                        string.Equals(val, item.name, StringComparison.Ordinal)
                        || val.Contains($".{item.name}", StringComparison.Ordinal)
                        || val.Contains($"{item.name},", StringComparison.Ordinal)
                        || val.Contains($"{item.name}`", StringComparison.Ordinal);

                    if (isQualified)
                    {
                        foundInStrings = true;
                        matchExample = $"[qualified] \"{val}\" in {Path.GetFileName(sl.FilePath)}";
                        break;
                    }
                }

                if (foundInStrings)
                {
                    var msg = $"[KEPT-BY-STRING] {item.name} — {matchExample}";
                    Console.WriteLine(msg);
                    lock (resultWriter) resultWriter.WriteLine(msg);
                    keptByStringLiteral++;
                    continue;
                }

                verified.Add(item);
            }

            Console.WriteLine($"[INFO] Text verification: {keptByIdentifier} kept by code refs, {keptByStringLiteral} kept by string literals, {verified.Count} confirmed unused");
            result = verified;

            // Recompute actions after filtering
            var newUnusedPerFile = result.GroupBy(c => c.filePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            result = result.Select(c =>
            {
                int totalTypes = typeCountPerFile.GetValueOrDefault(c.filePath, 1);
                int unusedInFile = newUnusedPerFile.GetValueOrDefault(c.filePath, 0);
                string action = unusedInFile >= totalTypes ? "DELETE" : "EDIT";
                return (c.name, c.filePath, c.lineNumber, c.fullLine, action);
            }).ToList();
        }

        int deleteCount = result.Count(r => r.action == "DELETE");
        int editCount = result.Count(r => r.action == "EDIT");
        Console.WriteLine($"[INFO] Total unused types: {result.Count} (DELETE: {deleteCount}, EDIT: {editCount})");
        
        return result;
    }

    internal static async Task AutoDelete(
        List<(string name, string filePath, int lineNumber, string fullLine, string action)> unusedClasses,
        StreamWriter resultWriter,
        AnalysisContext context)
    {
        // 1. Delete files where ALL types are unused
        var deleteFiles = unusedClasses
            .Where(c => c.action == "DELETE")
            .Select(c => c.filePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"[INFO] Deleting {deleteFiles.Count} files...");
        int deleted = 0;
        foreach (var file in deleteFiles)
        {
            try
            {
                File.Delete(file);
                deleted++;
                var msg = $"[DELETED] {file}";
                Console.WriteLine(msg);
                resultWriter.WriteLine(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to delete {file}: {ex.Message}");
            }
        }
        Console.WriteLine($"[INFO] Deleted {deleted}/{deleteFiles.Count} files");

        // 2. Remove unused types from multi-class files (action=EDIT)
        var editGroups = unusedClasses
            .Where(c => c.action == "EDIT")
            .GroupBy(c => c.filePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var editedContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (editGroups.Count > 0)
        {
            Console.WriteLine($"[INFO] Editing {editGroups.Count} multi-type files...");
            int edited = 0;

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
                        var typeNode = root.DescendantNodes()
                            .OfType<TypeDeclarationSyntax>()
                            .FirstOrDefault(n =>
                                n.Identifier.Text == item.name
                                && tree.GetLineSpan(n.Span).StartLinePosition.Line == item.lineNumber - 1);

                        if (typeNode != null)
                            nodesToRemove.Add(typeNode);
                        else
                            Console.WriteLine($"[WARN] Could not find {item.name} at line {item.lineNumber} in {group.Key}");
                    }

                    if (nodesToRemove.Count == 0)
                        continue;

                    var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                    if (newRoot == null)
                        continue;

                    var newText = newRoot.ToFullString();
                    newText = CleanupOrphanedRegions(newText);
                    while (newText.Contains("\r\n\r\n\r\n"))
                        newText = newText.Replace("\r\n\r\n\r\n", "\r\n\r\n");
                    while (newText.Contains("\n\n\n"))
                        newText = newText.Replace("\n\n\n", "\n\n");

                    await File.WriteAllTextAsync(group.Key, newText);
                    edited++;
                    editedContents[group.Key] = newText;

                    var names = string.Join(", ", group.Select(g => g.name));
                    var msg = $"[EDITED] {group.Key} — removed: {names}";
                    Console.WriteLine(msg);
                    resultWriter.WriteLine(msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to edit {group.Key}: {ex.Message}");
                }
            }
            Console.WriteLine($"[INFO] Edited {edited}/{editGroups.Count} files");
        }

        // 3. Build updated solution snapshot and clean orphaned namespace usings
        var deletedFilesSet = new HashSet<string>(deleteFiles, StringComparer.OrdinalIgnoreCase);
        var updatedSolution = UnusedUsingsCleanerHandler.BuildUpdatedSolution(
            context.Solution, deletedFilesSet, editedContents);

        await UnusedUsingsCleanerHandler.CleanupOrphanedNamespaceUsingsAsync(updatedSolution, resultWriter);
    }

    /// <summary>
    /// Removes orphaned #region / #endregion directives left after type node removal.
    /// Forward pass: #endregion with no matching #region above → orphaned.
    /// Backward pass: #region with no matching #endregion below → orphaned.
    /// </summary>
    internal static string CleanupOrphanedRegions(string text)
    {
        var lines = text.Split('\n');
        var linesToRemove = new HashSet<int>();

        // Forward pass: find orphaned #endregion (no matching #region before it)
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("#region"))
                depth++;
            else if (trimmed.StartsWith("#endregion"))
            {
                if (depth == 0)
                    linesToRemove.Add(i);
                else
                    depth--;
            }
        }

        // Backward pass: find orphaned #region (no matching #endregion after it)
        depth = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (linesToRemove.Contains(i)) continue;
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("#endregion"))
                depth++;
            else if (trimmed.StartsWith("#region"))
            {
                if (depth == 0)
                    linesToRemove.Add(i);
                else
                    depth--;
            }
        }

        if (linesToRemove.Count == 0) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!linesToRemove.Contains(i))
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
        }
        return sb.ToString();
    }

}
