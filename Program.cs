using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

class Program
{
    static async Task Main(string[] args)
    {
        var appName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;

        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp(appName);
            return;
        }

        MSBuildLocator.RegisterDefaults();

        // 1. Load settings
        var settings = AnalysisHelpers.GetSettings();

        // 2. Determine profile name (--profile or ActiveProfile from settings)
        string? profileName = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                profileName = args[i + 1];
                break;
            }
        }
        profileName ??= settings.ActiveProfile;

        // 3. Get profile
        Profile? profile = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            if (settings.Profiles == null || !settings.Profiles.TryGetValue(profileName, out profile))
            {
                Console.WriteLine($"[ERROR] Profile '{profileName}' not found in appsettings.json");
                return;
            }
            Console.WriteLine($"[INFO] Using profile: {profileName}");

            // Apply profile settings
            AnalysisHelpers.ApplyProfile(profile);
        }
        else
        {
            // Reset to default settings
            AnalysisHelpers.ResetToDefaults();
        }

        // 4. Apply profile settings
        string? solutionPath = profile?.SolutionPath;
        string mode = profile?.Mode?.ToLowerInvariant() ?? "types";
        bool autoDelete = profile?.AutoDelete == true;
        bool verifyText = profile?.VerifyText == true;
        string? logPath = profile?.LogPath;

        // 5. Command-line arguments override profile
        // --sln
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--sln", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                solutionPath = args[i + 1];
                break;
            }
        }
        solutionPath ??= settings.SolutionPath;

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            Console.WriteLine("[ERROR] Solution path is required. Use --help for usage information.");
            return;
        }

        // --mode
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                mode = args[i + 1].ToLowerInvariant();
                break;
            }
        }
        if (mode != "methods" && mode != "types" && mode != "usings" && mode != "reduce")
        {
            Console.WriteLine("[ERROR] Invalid mode. Use 'methods', 'types', 'usings' or 'reduce'.");
            return;
        }

        // --auto-delete, --verify-text, --log-path
        if (args.Contains("--auto-delete", StringComparer.OrdinalIgnoreCase))
            autoDelete = true;
        if (args.Contains("--verify-text", StringComparer.OrdinalIgnoreCase))
            verifyText = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--log-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                logPath = args[i + 1];
                break;
            }
        }
        logPath ??= AnalysisHelpers.GetLogFilePath(solutionPath);

        bool methodsMode = mode == "methods";
        bool typesMode = mode == "types";
        bool usingsMode = mode == "usings";
        bool reduceMode = mode == "reduce";

        Console.WriteLine($"[INFO] Mode: {mode}");
        Console.WriteLine($"[INFO] Log file: {logPath}");
        if (autoDelete)
        {
            Console.WriteLine(methodsMode
                ? "[INFO] Auto-delete mode ENABLED — unused methods will be removed from files"
                : "[INFO] Auto-delete mode ENABLED — files where ALL types are unused will be deleted");
        }

        if (verifyText)
        {
            Console.WriteLine("[INFO] Text verification ENABLED — items found in code/strings will be excluded");
        }

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            using var resultWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            Console.WriteLine($"[INFO] Starting Unused {mode} Detection: {DateTime.Now}");

            AnalysisContext? context = null;
            if (methodsMode || typesMode)
            {
                context = await AnalysisContext.BuildAsync(solution, resultWriter);

                if (methodsMode)
                    await RunMethodsAnalysis(context, resultWriter, verifyText, autoDelete);
                else
                    await RunTypesAnalysis(context, resultWriter, verifyText, autoDelete);
            }

            // Mode reduce — code simplification via Roslyn Simplifier
            if (reduceMode)
            {
                Console.WriteLine("[INFO] Running code simplification (Simplifier.ReduceAsync)...");
                await ReduceSimplifierHandler.RunAsync(solution, resultWriter);
            }

            // Cleanup unused usings
            if (usingsMode)
            {
                Console.WriteLine("[INFO] Running unused using cleanup...");
                await UnusedUsingsCleanerHandler.CleanupAsync(solution, resultWriter, context?.Compilations);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error: {ex.Message}\n{ex.StackTrace}");
        }

        Console.WriteLine($"[INFO] Finished Processing: {DateTime.Now}");
    }

    static async Task RunMethodsAnalysis(AnalysisContext context, StreamWriter resultWriter, bool verifyText, bool autoDelete)
    {
        var unusedMethods = await UnusedMethodsAnalyzerHandler.FindAsync(context, resultWriter, verifyText);

        resultWriter.WriteLine();
        resultWriter.WriteLine("=== SUMMARY — UNUSED METHODS ===");
        foreach (var group in unusedMethods.GroupBy(m => m.filePath).OrderBy(g => g.Key))
        {
            resultWriter.WriteLine(group.Key);
            foreach (var m in group)
            {
                resultWriter.WriteLine($"    - {m.signature} (Line {m.lineNumber})");
            }
        }

        if (autoDelete && unusedMethods.Count > 0)
        {
            await UnusedMethodsAnalyzerHandler.AutoDelete(unusedMethods, resultWriter, context);
        }
    }

    static async Task RunTypesAnalysis(AnalysisContext context, StreamWriter resultWriter, bool verifyText, bool autoDelete)
    {
        var unusedClasses = await UnusedTypesAnalyzerHandler.FindAsync(context, resultWriter, verifyText);

        resultWriter.WriteLine();
        resultWriter.WriteLine("=== SUMMARY WITH ACTIONS ===");
        foreach (var group in unusedClasses.GroupBy(c => c.filePath).OrderBy(g => g.Key))
        {
            var action = group.First().action;
            resultWriter.WriteLine($"[{action}] {group.Key}");
            foreach (var c in group)
                resultWriter.WriteLine($"    - {c.name} (Line {c.lineNumber})");
        }

        if (autoDelete)
            await UnusedTypesAnalyzerHandler.AutoDelete(unusedClasses, resultWriter, context);
    }

    static void PrintHelp(string appName)
    {
        Console.WriteLine($$"""
            Roslyn Unused Code Remover — Detect and remove unused code in .NET solutions. Make sure you have committed all your changes in code before using this tool.

            USAGE:
                {{appName}} [options]

            OPTIONS:
                --profile <name>      Use predefined profile from appsettings.json.
                                      If not specified, uses "ActiveProfile" from settings.
                --sln <path>          Path to solution file (overrides profile).
                --mode <type>         Analysis mode: 'methods', 'types', 'usings' or 'reduce' (overrides profile).
                --auto-delete         Auto-delete flag (overrides profile).
                --verify-text         Text verification flag (overrides profile).
                --log-path <path>     Log file path (overrides profile).
                --help, -h            Show this help message.

            PROFILES:
                Profiles are defined in appsettings.json and contain all settings:
                - SolutionPath, Mode, AutoDelete, VerifyText, LogPath
                - IgnoredFolders, SkipBaseTypeNames, AdditionalIgnorePatterns

            EXAMPLES:
                # Use 'types' profile (all settings from profile)
                {{appName}} --profile types

                # Use 'methods-auto' profile
                {{appName}} --profile methods-auto

                # Use profile with override
                {{appName}} --profile methods-auto --sln "Other.slnx"

                # Use 'full' profile with custom log
                {{appName}} --profile full --log-path "D:\\logs\\custom.log"

                # No profile (command-line only)
                {{appName}} --sln "MySolution.sln" --mode methods --auto-delete

                # Show help
                {{appName}} --help

            AVAILABLE PROFILES (from appsettings.json):
                - types           Basic types analysis
                - methods         Methods analysis
                - methods-auto    Methods + auto-delete
                - full            Methods + auto-delete + verify-text
                - quick           Quick scan with minimal exclusions
            """);
    }
}
