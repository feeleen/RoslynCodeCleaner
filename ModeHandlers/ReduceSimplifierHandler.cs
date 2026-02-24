using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Simplification;

static class ReduceSimplifierHandler
{
    internal static async Task RunAsync(Solution solution, StreamWriter resultWriter)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[INFO] Simplifying solution...");

        try
        {
            int filesChanged = 0;
            int totalReductions = 0;

            foreach (var projectId in solution.ProjectIds)
            {
                var project = solution.GetProject(projectId);
                if (project == null) continue;

                foreach (var documentId in project.DocumentIds)
                {
                    var document = project.GetDocument(documentId);
                    if (document == null) continue;

                    var originalText = await document.GetTextAsync();

                    // Simplifier.ReduceAsync works with a document
                    var reducedDocument = await Simplifier.ReduceAsync(document);
                    var reducedText = await reducedDocument.GetTextAsync();

                    if (originalText.ToString() != reducedText.ToString())
                    {
                        // Save the simplified version
                        var filePath = document.FilePath;
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            await System.IO.File.WriteAllTextAsync(filePath, reducedText.ToString());
                            filesChanged++;

                            var reductionCount = Math.Abs(reducedText.Length - originalText.Length);
                            totalReductions += reductionCount;

                            var msg = $"[SIMPLIFIED] {System.IO.Path.GetFileName(filePath)} ({reductionCount} chars)";
                            Console.WriteLine(msg);
                            lock (resultWriter) resultWriter.WriteLine(msg);
                        }
                    }
                }
            }

            sw.Stop();
            Console.WriteLine($"[INFO] Simplification complete: {filesChanged} files changed, ~{totalReductions} characters reduced in {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Simplification failed: {ex.Message}");
            lock (resultWriter) resultWriter.WriteLine($"[ERROR] Simplification failed: {ex.Message}");
        }
    }
}
