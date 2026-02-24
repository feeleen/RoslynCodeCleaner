using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Concurrent;
using System.Diagnostics;

record AnalysisContext(
    Solution Solution,
    ConcurrentDictionary<ProjectId, Compilation> Compilations,
    HashSet<ProjectId> ProjectsWithErrors,
    ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> IdentifierToFiles)
{
    /// <summary>
    /// Shared infrastructure: compiles all projects, detects errors, builds identifier index.
    /// </summary>
    public static async Task<AnalysisContext> BuildAsync(Solution solution, StreamWriter resultWriter)
    {
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        var identifierToFiles = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        var sw = Stopwatch.StartNew();

        // Phase 1: Pre-compile all projects in parallel
        Console.WriteLine($"[INFO] Pre-compiling {solution.ProjectIds.Count} projects in parallel...");

        await Parallel.ForEachAsync(solution.Projects, async (project, ct) =>
        {
            try
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation != null && compilation.SyntaxTrees.Any())
                {
                    compilations[project.Id] = compilation;
                    Console.WriteLine($"[INFO] Compiled: {project.Name} ({compilation.SyntaxTrees.Count()} files)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to compile project {project.Name}: {ex.Message}");
            }
        });

        Console.WriteLine($"[INFO] Compilation done in {sw.Elapsed.TotalSeconds:F1}s");

        // Pre-compute error set to avoid calling GetDiagnostics() multiple times
        var projectsWithErrors = new HashSet<ProjectId>();
        foreach (var kvp in compilations)
        {
            var diags = kvp.Value.GetDeclarationDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (diags.Count > 0)
            {
                var project = solution.GetProject(kvp.Key);
                Console.WriteLine($"[WARN] {project?.Name}: {diags.Count} compilation errors");
                projectsWithErrors.Add(kvp.Key);
            }
        }
        if (projectsWithErrors.Count > 0)
            Console.WriteLine($"[WARN] {projectsWithErrors.Count} projects have compilation errors — reference detection may be incomplete");

        // Phase 2.5: Build identifier-to-files index for fast pre-filtering
        Console.WriteLine($"[INFO] Building identifier index...");

        await Parallel.ForEachAsync(compilations, async (kvp, ct) =>
        {
            var compilation = kvp.Value;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(ct);
                var fp = tree.FilePath;
                var seen = new HashSet<string>();

                foreach (var token in root.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken) && seen.Add(token.Text))
                        identifierToFiles.GetOrAdd(token.Text, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[fp] = 0;
                }
            }
        });

        Console.WriteLine($"[INFO] Index built: {identifierToFiles.Count} unique identifiers in {sw.Elapsed.TotalSeconds:F1}s");

        return new AnalysisContext(solution, compilations, projectsWithErrors, identifierToFiles);
    }
}
