# Roslyn Code Cleaner

A powerful .NET tool for detecting and removing unused code (types, methods, and using directives) from your solutions using Roslyn analyzers.

## ⚠️ Warning

**Make sure you have committed all your changes before using this tool!** This tool modifies and deletes files automatically when using `--auto-delete` mode.

## Features

- 🔍 **Unused Types Detection** — Find unused classes, interfaces, structs, and records
- 🔍 **Unused Methods Detection** — Find unused methods within classes
- 🧹 **Unused Using Directives Cleanup** — Remove unused `using` statements
- 🔧 **Code Simplification** — Simplify type names and code using Roslyn Simplifier API
- 📊 **Text Verification** — Experimental feature. Excludes items found in string literals (reflection-safe)
- ⚡ **Parallel Processing** — Fast analysis using multi-threading
- 🎯 **Profile Support** — Predefined configurations for different scenarios

## Installation

```bash
# Clone the repository
git clone <repository-url>
cd RoslynCodeCleaner

# Build the project
dotnet build -c Release
```

## Usage

### Basic Commands

With basic usage - There is no filtering, it works as is.. Don't use it for complex projects. Use profiles instead.

```bash
# Show help
RoslynCodeCleaner.exe --help

# Analyze unused types (default mode)
RoslynCodeCleaner.exe --sln "MySolution.sln"

# Analyze unused methods
RoslynCodeCleaner.exe --sln "MySolution.sln" --mode methods

# Auto-delete unused code
RoslynCodeCleaner.exe --sln "MySolution.sln" --mode methods --auto-delete

# With text verification (excludes string literal matches)
RoslynCodeCleaner.exe --sln "MySolution.sln" --mode methods --verify-text --auto-delete

# Clean up unused using directives only
RoslynCodeCleaner.exe --sln "MySolution.sln" --mode usings

# Simplify code (Roslyn Reduce API)
RoslynCodeCleaner.exe --sln "MySolution.sln" --mode reduce
```

### Using Profiles

Profiles allow you to save and reuse configurations, apply custom filtering, defined in `appsettings.json`:

```bash
# Use predefined profile
RoslynCodeCleaner.exe --profile types

# Use methods-auto profile (methods + auto-delete)
RoslynCodeCleaner.exe --profile methods-auto

# Use full profile (methods + auto-delete + verify-text)
RoslynCodeCleaner.exe --profile full

# Override profile settings
RoslynCodeCleaner.exe --profile methods-auto --sln "Other.slnx"
```

## Command-Line Options

| Option | Description |
|--------|-------------|
| `--profile <name>` | Use predefined profile from appsettings.json |
| `--sln <path>` | Path to solution file (.sln or .slnx) |
| `--mode <type>` | Analysis mode: `methods`, `types`, `usings`, or `reduce` |
| `--auto-delete` | Automatically delete found unused code |
| `--verify-text` | Exclude items found in string literals |
| `--log-path <path>` | Custom log file path |
| `--help`, `-h` | Show help message |

## Configuration (appsettings.json)

### Profile Structure

```json
{
  "ActiveProfile": "types",

  "Profiles": {
    "types": {
      "SolutionPath": "C:\\Projects\\MySolution.slnx",
      "Mode": "types",
      "AutoDelete": false,
      "VerifyText": false,
      "LogPath": null,
      "IgnoredFolders": [
        "ExternalComponents",
        "Tests"
      ],
      "SkipBaseTypeNames": [
        "Controller", "DbContext", "Migration"
      ],
      "AdditionalIgnorePatterns": {
        "StartsWith": [ "ActionParameter" ],
        "EndsWith": [ "Mapper", "Profile", "Exception" ],
        "Contains": [],
        "Regexes": []
      },
      "AdditionalIncludePatterns": {
        "StartsWith": [],
        "EndsWith": [ "Repository", "Handler" ],
        "Contains": [],
        "Regexes": []
      }
    }
  }
}
```

### Configuration Options

| Setting | Description |
|---------|-------------|
| `ActiveProfile` | Default profile to use when `--profile` is not specified |
| `SolutionPath` | Fallback solution path (can be overridden by `--sln`) |
| `IgnoredFolders` | Folders to skip during analysis |
| `IgnoredAttributes` | Attributes that mark types to skip |
| `SkipBaseTypeNames` | Base types that are always skipped (DI/infrastructure types) |
| `AdditionalIgnorePatterns` | Blacklist — skip classes matching these name patterns |
| `AdditionalIncludePatterns` | Whitelist — only analyze classes matching these name patterns |

When both `AdditionalIncludePatterns` and `AdditionalIgnorePatterns` are set, the whitelist is applied first (only matching classes are considered), then the blacklist further excludes from that set. In methods mode, both filters apply to the **containing class name**, not the method name.

### Name Patterns (used by both Ignore and Include)

| Pattern | Description | Example |
|---------|-------------|---------|
| `StartsWith` | Match types starting with these prefixes | `["Action", "Base"]` |
| `EndsWith` | Match types ending with these suffixes | `["Service", "Repository"]` |
| `Contains` | Match types containing these substrings | `["Helper", "Utility"]` |
| `Regexes` | Match types by regex patterns | `["^.*Dto$", "^.*ViewModel$"]` |

## Output

### Console Output

```
[INFO] Mode: methods
[INFO] Log file: log-MySolution-20260222-143022.log
[INFO] Auto-delete mode ENABLED — unused methods will be removed from files
[INFO] Starting Unused Methods Detection: 22.02.2026 14:30:22
[INFO] Collecting method candidates...
[INFO] Found 15234 method candidates in 3.2s
[INFO] Analyzing method references...
[INFO] Progress: 500/15234 analyzed
[UNUSED] Method MyClass.UnusedMethod at C:\Projects\MyClass.cs (Line 42)
[INFO] Finished Processing: 22.02.2026 14:35:47
```

### Log File

The tool creates a log file with detailed information about all found unused items and actions taken.

## How It Works

1. **Type/Method Detection** — Uses Roslyn to find all public types/methods
2. **Reference Analysis** — Checks if types/methods are referenced elsewhere
3. **Polymorphic Detection** — Skips types used via DI/reflection (controllers, services, etc.)
4. **Text Verification** (optional) — Excludes items found in string literals
5. **Action Determination** — Decides whether to EDIT (remove type) or DELETE (remove file)
6. **Auto-Delete** (optional) — Removes unused code from files

## Safety Features

- ✅ **Verification Step** — Checks that removal doesn't introduce compilation errors
- ✅ **Profile-Based** — Test with different configurations before auto-delete
- ✅ **Logging** — All actions are logged for review
- ✅ **Ignored Folders** — Exclude test projects and external code
- ✅ **Base Type Skipping** — Skip infrastructure types (controllers, services, etc.)

## Best Practices

1. **Commit First** — Always commit your changes before running
2. **Test Without Auto-Delete** — Run without `--auto-delete` first to review findings
3. **Use Profiles** — Create profiles for different projects/scenarios
4. **Review Logs** — Check the log file before applying changes
5. **Start Small** — Test on a small solution first

## Requirements

- .NET 9.0 SDK or later
- Visual Studio 2022 17.8+ (for .slnx support)
- MSBuild (included with .NET SDK)

## Known Limitations

- **Cross-Project References** — May not detect all cross-project usages if projects have build errors
- **Reflection** — Types used via reflection may be falsely marked as unused (use `--verify-text`)
- **Generated Code** — Auto-generated files (*.g.cs, *.Designer.cs) are skipped
- **External Packages** — Does not analyze NuGet package dependencies

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

MIT License — See [LICENSE](LICENSE) file for details.

## Support

For issues and feature requests, please create an issue in the GitHub repository.
