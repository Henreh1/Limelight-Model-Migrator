using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LimelightModelMigrator;

internal sealed class PatcherService(Action<string> log)
{
    public const string CanonicalLegacyAsset = "/Game/Pagoda/Characters/Player/Meshes/SK_Charlie";
    public const string NoHairAsset = "/Game/Pagoda/Characters/Player/Meshes/PBAP_NoHairForCSM";
    public static IReadOnlyList<string> NoHairAssets { get; } =
    [
        NoHairAsset,
        "/Game/Pagoda/Characters/Player/Meshes/SK_Charlie_ATTACH_Hair_Default",
        "/Game/Pagoda/Characters/Common/Skeletons/SKEL_Humanoid_Head",
    ];
    private const string ChunkAssignmentsFileName = "LimelightModelMigrator.json";
    private static readonly string CanonicalGameFolder = CanonicalLegacyAsset.Split('/')[2];

    private readonly Action<string> _log = log;

    public static string? FindEditorExecutable()
    {
        var candidates = new List<string>();
        var configuredEngineRoot = Environment.GetEnvironmentVariable("UE_5_7_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredEngineRoot))
        {
            candidates.Add(EditorExecutableUnder(configuredEngineRoot));
        }

        var launcherInstallations = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "UnrealEngineLauncher",
            "LauncherInstalled.dat");
        candidates.AddRange(DiscoverEditorCandidatesFromLauncherFile(launcherInstallations));

        foreach (var programFilesRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var epicRoot = Path.Combine(programFilesRoot, "Epic Games");
            if (Directory.Exists(epicRoot))
            {
                candidates.AddRange(
                    Directory.EnumerateDirectories(epicRoot, "UE_5.7*")
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(EditorExecutableUnder));
            }
        }

        try
        {
            using var buildsKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (buildsKey is not null)
            {
                foreach (var valueName in buildsKey.GetValueNames())
                {
                    if (buildsKey.GetValue(valueName) is string root)
                    {
                        candidates.Add(EditorExecutableUnder(root));
                    }
                }
            }
        }
        catch
        {
            // The manual picker remains available if registry discovery is unavailable.
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => File.Exists(candidate) && IsUnreal57Editor(candidate));
    }

    private static string EditorExecutableUnder(string engineInstallRoot) =>
        Path.Combine(
            engineInstallRoot.Trim().Trim('"'),
            "Engine",
            "Binaries",
            "Win64",
            "UnrealEditor-Cmd.exe");

    internal static IReadOnlyList<string> DiscoverEditorCandidatesFromLauncherFile(string launcherFile)
    {
        if (!File.Exists(launcherFile))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(launcherFile));
            if (!document.RootElement.TryGetProperty("InstallationList", out var installations) ||
                installations.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<string>();
            foreach (var installation in installations.EnumerateArray())
            {
                if (!installation.TryGetProperty("InstallLocation", out var locationProperty))
                {
                    continue;
                }

                var installLocation = locationProperty.GetString();
                var appName = installation.TryGetProperty("AppName", out var appNameProperty)
                    ? appNameProperty.GetString()
                    : null;
                var artifactId = installation.TryGetProperty("ArtifactId", out var artifactProperty)
                    ? artifactProperty.GetString()
                    : null;
                var looksLike57 = (appName?.StartsWith("UE_5.7", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                  (artifactId?.StartsWith("UE_5.7", StringComparison.OrdinalIgnoreCase) ?? false);
                if (looksLike57 && !string.IsNullOrWhiteSpace(installLocation))
                {
                    results.Add(EditorExecutableUnder(installLocation));
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static bool IsUnreal57Editor(string editorExecutable)
    {
        if (!File.Exists(editorExecutable) ||
            !string.Equals(
                Path.GetFileName(editorExecutable),
                "UnrealEditor-Cmd.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var editorDirectory = Path.GetDirectoryName(Path.GetFullPath(editorExecutable))!;
            var engineDirectory = Path.GetFullPath(Path.Combine(editorDirectory, "..", ".."));
            var buildVersionPath = Path.Combine(engineDirectory, "Build", "Build.version");
            using var document = JsonDocument.Parse(File.ReadAllText(buildVersionPath));
            return document.RootElement.TryGetProperty("MajorVersion", out var major) &&
                   document.RootElement.TryGetProperty("MinorVersion", out var minor) &&
                   major.GetInt32() == 5 &&
                   minor.GetInt32() == 7;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAnyUnrealEditorOpen()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return process.ProcessName.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<PatchRunResult> RunAsync(PatchOptions options)
    {
        Validate(options);

        if (options.UpdateCharacterLabel && options.ModChunkId is null)
        {
            options = options with
            {
                ModChunkId = ResolveOrSuggestChunkId(options.ProjectFile, options.SourceAsset),
            };
        }

        if (options.UpdateCharacterLabel && options.ModChunkId is null)
        {
            throw new InvalidOperationException(
                "A safe mod chunk could not be selected automatically. Enter a positive chunk ID and try again.");
        }

        if (options.ModChunkId is not null)
        {
            _log($"Selected {options.ModDisplayName} in mod chunk {options.ModChunkId.Value}...");
        }
        if (!string.IsNullOrWhiteSpace(options.SupportBlueprintAsset))
        {
            _log("Including glasses blueprint: " + options.SupportBlueprintAsset + "...");
        }
        if (options.IncludeNoHair)
        {
            ValidateNoHairAssetSet(options.ProjectFile, options.NoHairSourceDirectory);
            _log("Including the complete No hair support set...");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var projectDirectory = Path.GetDirectoryName(options.ProjectFile)!;
        var backupDirectory = Path.Combine(projectDirectory, "LimelightModelMigrator_Backups", timestamp);
        Directory.CreateDirectory(backupDirectory);

        var permanentResultPath = Path.Combine(backupDirectory, "patch_result.json");
        var permanentScriptPath = Path.Combine(backupDirectory, "patch_script.py");
        var fullLogPath = Path.Combine(backupDirectory, "unreal_output.log");
        var packageLogPath = Path.Combine(backupDirectory, "package_output.log");
        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "LimelightModelMigrator",
            "run-" + Guid.NewGuid().ToString("N"));
        var resultPath = Path.Combine(workDirectory, "patch_result.json");
        var scriptPath = Path.Combine(workDirectory, "patch_script.py");
        var originalDescriptorBytes = await File.ReadAllBytesAsync(options.ProjectFile);
        string? restoreWarning = null;

        try
        {
            Directory.CreateDirectory(workDirectory);

            _log("Creating a safety backup...");
            CreateBackup(options, backupDirectory);

            if (options.IncludeNoHair)
            {
                _log("Installing No hair support into the selected project...");
                InstallNoHairAssetSet(options.ProjectFile, options.NoHairSourceDirectory);
            }

            _log("Configuring self-contained material shaders for the cooked mod...");
            EnsureInlineMaterialShaders(projectDirectory);

            _log("Preparing Unreal Editor scripting support...");
            PrepareProjectDescriptorForMigration(options.ProjectFile, options.EditorExecutable);

            var script = PatcherScript.Build(options, resultPath);
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));
            await File.WriteAllTextAsync(permanentScriptPath, script, new UTF8Encoding(false));

            _log("Starting Unreal Engine 5.7. This can take a minute...");
            var exitCode = await RunEditorAsync(options, scriptPath, fullLogPath);

            EnginePatchResult? engineResult = null;
            if (File.Exists(resultPath))
            {
                File.Copy(resultPath, permanentResultPath, true);
                var resultJson = await File.ReadAllTextAsync(resultPath);
                engineResult = JsonSerializer.Deserialize<EnginePatchResult>(resultJson);
            }

            if (engineResult is null)
            {
                var usefulError = await FindUsefulUnrealErrorAsync(fullLogPath);
                return new PatchRunResult
                {
                    Success = false,
                    BackupDirectory = backupDirectory,
                    FullLogPath = fullLogPath,
                    Error = usefulError ??
                        $"Unreal Editor ended without producing a patch result (exit code {exitCode}).",
                };
            }

            if (engineResult.Success && options.UpdateCharacterLabel && options.ModChunkId is not null)
            {
                try
                {
                    RememberChunkAssignment(options.ProjectFile, options.SourceAsset, options.ModChunkId.Value);
                }
                catch (Exception error)
                {
                    _log("Migration succeeded, but its automatic chunk could not be remembered: " + error.Message);
                    engineResult.Warnings.Add(
                        "The chunk assignment could not be remembered automatically; keep using chunk " +
                        options.ModChunkId.Value + " for this mod.");
                }
            }

            CookPackageResult? packageResult = null;
            if (engineResult.Success && options.RunMode == PatchRunMode.PatchCookAndPackage)
            {
                if (options.ModChunkId is null)
                {
                    packageResult = new CookPackageResult
                    {
                        Success = false,
                        LogPath = packageLogPath,
                        Error = "No mod chunk was selected for cooking and packaging.",
                    };
                }
                else
                {
                    packageResult = await CookAndPackageSelectedChunkAsync(
                        options,
                        options.ModChunkId.Value,
                        timestamp,
                        packageLogPath);
                }

                if (!packageResult.Success)
                {
                    return new PatchRunResult
                    {
                        Success = false,
                        BackupDirectory = backupDirectory,
                        FullLogPath = packageResult.LogPath,
                        EngineResult = engineResult,
                        PackageResult = packageResult,
                        Error = "The model migration completed, but cooking and packaging stopped: " +
                                packageResult.Error,
                    };
                }
            }

            return new PatchRunResult
            {
                Success = engineResult.Success &&
                          (options.RunMode == PatchRunMode.PatchOnly || packageResult?.Success == true),
                BackupDirectory = backupDirectory,
                FullLogPath = fullLogPath,
                EngineResult = engineResult,
                PackageResult = packageResult,
                Error = engineResult.Error,
            };
        }
        catch (Exception error)
        {
            return new PatchRunResult
            {
                Success = false,
                BackupDirectory = backupDirectory,
                FullLogPath = fullLogPath,
                Error = error.Message,
            };
        }
        finally
        {
            try
            {
                await File.WriteAllBytesAsync(options.ProjectFile, originalDescriptorBytes);
            }
            catch (Exception error)
            {
                restoreWarning =
                    "The original .uproject descriptor could not be restored automatically: " + error.Message;
                _log(restoreWarning);
            }

            if (restoreWarning is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(backupDirectory, "DESCRIPTOR_RESTORE_WARNING.txt"),
                    restoreWarning);
            }

            TryDeleteWorkingDirectory(workDirectory);
        }
    }

    private static void Validate(PatchOptions options)
    {
        if (!File.Exists(options.ProjectFile) ||
            !string.Equals(Path.GetExtension(options.ProjectFile), ".uproject", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a valid Unreal .uproject file.");
        }

        if (!File.Exists(options.EditorExecutable) ||
            !string.Equals(Path.GetFileName(options.EditorExecutable), "UnrealEditor-Cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose the Unreal Engine 5.7 UnrealEditor-Cmd.exe file.");
        }

        if (!IsUnreal57Editor(options.EditorExecutable))
        {
            throw new InvalidOperationException(
                "The selected editor is not an Unreal Engine 5.7 installation. Choose that engine's UnrealEditor-Cmd.exe file.");
        }

        if (!options.PatchBody && !options.PatchPreview && !options.PatchCosmetics)
        {
            throw new InvalidOperationException("Select at least one mesh target to patch.");
        }

        if (options.RunMode == PatchRunMode.PatchCookAndPackage && !options.UpdateCharacterLabel)
        {
            throw new InvalidOperationException(
                "Patch + package requires the selected mod packaging label to be enabled.");
        }

        if (options.IncludeNoHair && !options.UpdateCharacterLabel)
        {
            throw new InvalidOperationException(
                "The No hair option requires mod chunk packaging to remain enabled.");
        }

        var sourceFile = AssetPathToFile(options.ProjectFile, options.SourceAsset);
        if (!File.Exists(sourceFile))
        {
            throw new InvalidOperationException(
                "The selected legacy model could not be found in this project: " + options.SourceAsset);
        }

        if (!string.IsNullOrWhiteSpace(options.SupportBlueprintAsset))
        {
            var supportFile = AssetPathToFile(options.ProjectFile, options.SupportBlueprintAsset);
            if (!File.Exists(supportFile))
            {
                throw new InvalidOperationException(
                    "The selected glasses blueprint could not be found in this project: " +
                    options.SupportBlueprintAsset);
            }
        }
    }

    public static IReadOnlyList<LegacyModCandidate> DiscoverLegacyMods(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var contentDirectory = Path.Combine(projectDirectory, "Content");
        if (!Directory.Exists(contentDirectory))
        {
            return [];
        }

        var entries = Directory
            .EnumerateFiles(contentDirectory, "*SK_Charlie.uasset", SearchOption.AllDirectories)
            .Select(file =>
            {
                var fullPath = Path.GetFullPath(file);
                var relativePath = Path.GetRelativePath(contentDirectory, fullPath);
                var relativeAsset = Path.ChangeExtension(relativePath, null)!
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var assetPath = "/Game/" + relativeAsset;
                return (DisplayName: InferModName(projectFile, relativePath), AssetPath: assetPath, FilePath: fullPath);
            })
            .OrderBy(candidate =>
                string.Equals(candidate.AssetPath, CanonicalLegacyAsset, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remembered = LoadChunkAssignments(projectFile);
        var usedChunks = DiscoverUsedChunkIds(projectFile);
        foreach (var chunk in remembered.Values.Where(IsValidChunkId))
        {
            usedChunks.Add(chunk);
        }

        var detectedChunks = entries.ToDictionary(
            entry => entry.AssetPath,
            entry => DetectLegacyChunkId(projectFile, entry.AssetPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in detectedChunks.Values.OfType<int>())
        {
            usedChunks.Add(chunk);
        }

        var discovered = new List<LegacyModCandidate>(entries.Count);
        foreach (var entry in entries)
        {
            var detected = detectedChunks[entry.AssetPath];
            if (detected is not null)
            {
                discovered.Add(new LegacyModCandidate(
                    entry.DisplayName,
                    entry.AssetPath,
                    entry.FilePath,
                    detected,
                    ChunkAssignmentKind.Detected));
                continue;
            }

            if (remembered.TryGetValue(entry.AssetPath, out var rememberedChunk) &&
                IsValidChunkId(rememberedChunk))
            {
                discovered.Add(new LegacyModCandidate(
                    entry.DisplayName,
                    entry.AssetPath,
                    entry.FilePath,
                    rememberedChunk,
                    ChunkAssignmentKind.Remembered));
                continue;
            }

            var suggested = FindNextAvailableChunk(usedChunks);
            if (suggested is not null)
            {
                usedChunks.Add(suggested.Value);
            }
            discovered.Add(new LegacyModCandidate(
                entry.DisplayName,
                entry.AssetPath,
                entry.FilePath,
                suggested,
                suggested is null ? ChunkAssignmentKind.Unavailable : ChunkAssignmentKind.Suggested));
        }

        return discovered
            .GroupBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Count() == 1
                ? group
                : group.Select((candidate, index) =>
                    candidate with { DisplayName = $"{candidate.DisplayName} ({index + 1})" }))
            .OrderBy(candidate =>
                string.Equals(candidate.AssetPath, CanonicalLegacyAsset, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SupportBlueprintCandidate> DiscoverSupportBlueprints(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var contentDirectory = Path.Combine(projectDirectory, "Content");
        if (!Directory.Exists(contentDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(contentDirectory, "*.uasset", SearchOption.AllDirectories)
            .Where(file => IsGlassesBlueprintName(Path.GetFileNameWithoutExtension(file)))
            .Select(file =>
            {
                var fullPath = Path.GetFullPath(file);
                var relativePath = Path.GetRelativePath(contentDirectory, fullPath);
                var relativeAsset = Path.ChangeExtension(relativePath, null)!
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var assetPath = "/Game/" + relativeAsset;
                return new SupportBlueprintCandidate(
                    FriendlyBlueprintName(Path.GetFileNameWithoutExtension(file)),
                    assetPath,
                    fullPath,
                    DetectLegacyChunkId(projectFile, assetPath));
            })
            .OrderBy(candidate => BlueprintSortOrder(candidate.DisplayName))
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int? DetectLegacyChunkId(
        string projectFile,
        string sourceAsset = CanonicalLegacyAsset)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var savedDirectory = Path.Combine(projectDirectory, "Saved", "Cooked");
        if (!Directory.Exists(savedDirectory))
        {
            return null;
        }

        var sourcePackage = sourceAsset.Split('.', 2)[0];
        foreach (var infoFile in Directory
                     .EnumerateFiles(savedDirectory, "AllChunksInfo.csv", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            foreach (var line in File.ReadLines(infoFile))
            {
                var columns = line.Split(',', 4);
                if (columns.Length >= 2 &&
                    string.Equals(columns[1].Trim().Trim('"'), sourcePackage, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(columns[0].Trim(), out var chunkId) &&
                    chunkId > 0)
                {
                    return chunkId;
                }
            }
        }

        var sourceRelative = sourcePackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? sourcePackage[6..]
            : sourcePackage.TrimStart('/');
        var sourceSuffix = Path.Combine(
            new[] { "Content" }.Concat(sourceRelative.Split('/')).ToArray());
        foreach (var manifest in Directory
                     .EnumerateFiles(savedDirectory, "pakchunk*.txt", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var fileName = Path.GetFileNameWithoutExtension(manifest);
            var idText = fileName["pakchunk".Length..];
            if (!int.TryParse(idText, out var chunkId) || chunkId <= 0)
            {
                continue;
            }

            if (File.ReadLines(manifest).Any(line =>
                    line.Replace('/', Path.DirectorySeparatorChar)
                        .EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                return chunkId;
            }
        }

        return null;
    }

    public static int? SuggestUnusedChunkId(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
        {
            return null;
        }

        var usedChunks = DiscoverUsedChunkIds(projectFile);
        foreach (var chunk in LoadChunkAssignments(projectFile).Values.Where(IsValidChunkId))
        {
            usedChunks.Add(chunk);
        }
        return FindNextAvailableChunk(usedChunks);
    }

    private static int? ResolveOrSuggestChunkId(string projectFile, string sourceAsset)
    {
        var detected = DetectLegacyChunkId(projectFile, sourceAsset);
        if (detected is not null)
        {
            return detected;
        }

        var remembered = LoadChunkAssignments(projectFile);
        if (remembered.TryGetValue(sourceAsset, out var rememberedChunk) &&
            IsValidChunkId(rememberedChunk))
        {
            return rememberedChunk;
        }

        return SuggestUnusedChunkId(projectFile);
    }

    private static HashSet<int> DiscoverUsedChunkIds(string projectFile)
    {
        var result = new HashSet<int>();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var savedDirectory = Path.Combine(projectDirectory, "Saved", "Cooked");
        if (!Directory.Exists(savedDirectory))
        {
            return result;
        }

        foreach (var infoFile in Directory.EnumerateFiles(
                     savedDirectory,
                     "AllChunksInfo.csv",
                     SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(infoFile))
            {
                var columns = line.Split(',', 2);
                if (columns.Length > 0 &&
                    int.TryParse(columns[0].Trim().Trim('"'), out var chunkId) &&
                    IsValidChunkId(chunkId))
                {
                    result.Add(chunkId);
                }
            }
        }

        foreach (var manifest in Directory.EnumerateFiles(
                     savedDirectory,
                     "pakchunk*.txt",
                     SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(manifest);
            var idText = fileName["pakchunk".Length..];
            if (int.TryParse(idText, out var chunkId) && IsValidChunkId(chunkId))
            {
                result.Add(chunkId);
            }
        }

        return result;
    }

    private static int? FindNextAvailableChunk(IReadOnlySet<int> usedChunks)
    {
        for (var candidate = 100; candidate <= 999; candidate++)
        {
            if (!usedChunks.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsValidChunkId(int chunkId) => chunkId is > 0 and <= 999;

    private static Dictionary<string, int> LoadChunkAssignments(string projectFile)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = ChunkAssignmentsPath(projectFile);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var root = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) as JsonObject;
            if (root?["chunkAssignments"] is not JsonObject assignments)
            {
                return result;
            }

            foreach (var pair in assignments)
            {
                if (pair.Value is JsonValue value &&
                    value.TryGetValue<int>(out var chunkId) &&
                    IsValidChunkId(chunkId))
                {
                    result[pair.Key] = chunkId;
                }
            }
        }
        catch (JsonException)
        {
            // A damaged optional assignment file should not stop project discovery.
        }

        return result;
    }

    private static void RememberChunkAssignment(string projectFile, string sourceAsset, int chunkId)
    {
        if (!IsValidChunkId(chunkId))
        {
            return;
        }

        var path = ChunkAssignmentsPath(projectFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var assignments = LoadChunkAssignments(projectFile);
        assignments[sourceAsset] = chunkId;

        var assignmentObject = new JsonObject();
        foreach (var pair in assignments.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            assignmentObject[pair.Key] = pair.Value;
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["chunkAssignments"] = assignmentObject,
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }

    private static string ChunkAssignmentsPath(string projectFile) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(projectFile))!,
        "Config",
        ChunkAssignmentsFileName);

    private static bool IsGlassesBlueprintName(string assetName)
    {
        var compact = Regex.Replace(assetName, "[^A-Za-z0-9]", string.Empty);
        return compact.Contains("PABP", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("NoGlasses", StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyBlueprintName(string assetName)
    {
        var compact = Regex.Replace(assetName, "[^A-Za-z0-9]", string.Empty);
        if (compact.Contains("NoGlasses", StringComparison.OrdinalIgnoreCase))
        {
            return "No Glasses AnimBP";
        }

        var friendly = FriendlyName(assetName)
            .Replace("Pabp", "PABP", StringComparison.OrdinalIgnoreCase)
            .Replace("Anim Bp", "AnimBP", StringComparison.OrdinalIgnoreCase);
        return friendly;
    }

    private static int BlueprintSortOrder(string displayName)
    {
        if (displayName.StartsWith("No Glasses", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (string.Equals(displayName, "PABP Charlie", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return 2;
    }

    private static string InferModName(string projectFile, string relativeAssetFile)
    {
        const string legacySuffix = "SK_Charlie";
        var assetName = Path.GetFileNameWithoutExtension(relativeAssetFile);
        if (assetName.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase) &&
            assetName.Length > legacySuffix.Length)
        {
            var prefix = assetName[..^legacySuffix.Length].Trim(' ', '_', '-');
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                return FriendlyName(prefix);
            }
        }

        var segments = relativeAssetFile
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        var gameFolderIndex = Array.FindIndex(
            segments,
            segment => string.Equals(segment, CanonicalGameFolder, StringComparison.OrdinalIgnoreCase));

        if (gameFolderIndex > 0)
        {
            return FriendlyName(segments[gameFolderIndex - 1]);
        }

        if (string.Equals(assetName, legacySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return "Current SK Charlie";
        }

        return FriendlyName(Path.GetFileNameWithoutExtension(projectFile));
    }

    private static string FriendlyName(string value)
    {
        var spaced = Regex.Replace(
                value.Replace('_', ' ').Replace('-', ' ').Trim(),
                "(?<=[a-z0-9])(?=[A-Z])",
                " ")
            .Trim();
        if (string.IsNullOrWhiteSpace(spaced))
        {
            return "Older model mod";
        }

        if (!spaced.Contains(' ') && spaced.Length <= 3)
        {
            return spaced.ToUpperInvariant();
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static string AssetPathToFile(string projectFile, string assetPath)
    {
        var packagePath = assetPath.Split('.', 2)[0];
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected model must be stored under /Game in this project.");
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var contentDirectory = Path.GetFullPath(Path.Combine(projectDirectory, "Content"));
        var relative = packagePath[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        var candidate = Path.GetFullPath(Path.Combine(contentDirectory, relative));
        var contentPrefix = contentDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected model path leaves the project's Content folder.");
        }

        return candidate;
    }

    private static void CreateBackup(PatchOptions options, string backupDirectory)
    {
        var projectFile = options.ProjectFile;
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        File.Copy(projectFile, Path.Combine(backupDirectory, Path.GetFileName(projectFile)), true);

        var defaultGameConfig = Path.Combine(projectDirectory, "Config", "DefaultGame.ini");
        if (File.Exists(defaultGameConfig))
        {
            CopyWithRelativePath(projectDirectory, defaultGameConfig, backupDirectory);
        }

        var chunkAssignments = ChunkAssignmentsPath(projectFile);
        if (File.Exists(chunkAssignments))
        {
            CopyWithRelativePath(projectDirectory, chunkAssignments, backupDirectory);
        }

        var meshesDirectory = Path.GetDirectoryName(
            AssetPathToFile(projectFile, CanonicalLegacyAsset))!;
        if (Directory.Exists(meshesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(meshesDirectory, "SK_Charlie*", SearchOption.TopDirectoryOnly))
            {
                CopyWithRelativePath(projectDirectory, file, backupDirectory);
            }
        }

        var contentDirectory = Path.Combine(projectDirectory, "Content");
        var selectedSourceFile = AssetPathToFile(projectFile, options.SourceAsset);
        if (File.Exists(selectedSourceFile))
        {
            var selectedDirectory = Path.GetDirectoryName(selectedSourceFile)!;
            var selectedBaseName = Path.GetFileNameWithoutExtension(selectedSourceFile);
            foreach (var file in Directory.EnumerateFiles(
                         selectedDirectory,
                         selectedBaseName + ".*",
                         SearchOption.TopDirectoryOnly))
            {
                CopyWithRelativePath(projectDirectory, file, backupDirectory);
            }

            var selectedRelativePath = Path.GetRelativePath(contentDirectory, selectedSourceFile);
            var selectedSegments = selectedRelativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var gameFolderIndex = Array.FindIndex(
                selectedSegments,
                segment => string.Equals(segment, CanonicalGameFolder, StringComparison.OrdinalIgnoreCase));
            if (gameFolderIndex > 0)
            {
                var storedModRoot = Path.Combine(
                    new[] { contentDirectory }.Concat(selectedSegments.Take(gameFolderIndex)).ToArray());
                foreach (var file in Directory.EnumerateFiles(storedModRoot, "*", SearchOption.AllDirectories))
                {
                    CopyWithRelativePath(projectDirectory, file, backupDirectory);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.SupportBlueprintAsset))
        {
            var supportFile = AssetPathToFile(projectFile, options.SupportBlueprintAsset);
            var supportDirectory = Path.GetDirectoryName(supportFile)!;
            var supportBaseName = Path.GetFileNameWithoutExtension(supportFile);
            foreach (var file in Directory.EnumerateFiles(
                         supportDirectory,
                         supportBaseName + ".*",
                         SearchOption.TopDirectoryOnly))
            {
                CopyWithRelativePath(projectDirectory, file, backupDirectory);
            }
        }

        if (options.IncludeNoHair)
        {
            foreach (var noHairAsset in NoHairAssets)
            {
                var noHairFile = AssetPathToFile(projectFile, noHairAsset);
                if (File.Exists(noHairFile))
                {
                    var noHairDirectory = Path.GetDirectoryName(noHairFile)!;
                    var noHairBaseName = Path.GetFileNameWithoutExtension(noHairFile);
                    foreach (var file in Directory.EnumerateFiles(
                                 noHairDirectory,
                                 noHairBaseName + ".*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        CopyWithRelativePath(projectDirectory, file, backupDirectory);
                    }
                }
            }
        }

        foreach (var materialsDirectory in new[]
                 {
                     Path.Combine(projectDirectory, "Content", CanonicalGameFolder, "Characters", "Materials"),
                     Path.Combine(projectDirectory, "Content", CanonicalGameFolder, "Characters", "Player", "Materials"),
                     Path.Combine(projectDirectory, "Content", "LimelightModelMigrator", "Materials"),
                 })
        {
            if (!Directory.Exists(materialsDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(materialsDirectory, "*", SearchOption.AllDirectories))
            {
                CopyWithRelativePath(projectDirectory, file, backupDirectory);
            }
        }

        if (Directory.Exists(contentDirectory))
        {
            foreach (var labelFile in Directory.EnumerateFiles(contentDirectory, "*Character*.uasset", SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(labelFile)!;
                var baseName = Path.GetFileNameWithoutExtension(labelFile);
                foreach (var companion in Directory.EnumerateFiles(directory, baseName + ".*", SearchOption.TopDirectoryOnly))
                {
                    CopyWithRelativePath(projectDirectory, companion, backupDirectory);
                }
            }
        }

        File.WriteAllText(
            Path.Combine(backupDirectory, "README.txt"),
            "Limelight Model Migrator backup\r\n" +
            "Created: " + DateTime.Now.ToString("O") + "\r\n\r\n" +
            "These are copies of the project descriptor, packaging configuration, chunk assignments, selected PABP, any existing No hair support assets, legacy Charlie meshes, relevant materials, and Character asset label " +
            "from immediately before the patch. Copy them back to their original relative paths to restore them.\r\n");
    }

    internal static IReadOnlyList<string> InstallNoHairAssetSet(
        string projectFile,
        string? sourceDirectory)
    {
        var existingTargets = NoHairAssets
            .Select(assetPath => AssetPathToFile(projectFile, assetPath))
            .ToArray();
        if (existingTargets.All(IsEditorAssetFile))
        {
            return existingTargets;
        }

        var sourceFiles = ResolveNoHairSourceFiles(sourceDirectory);
        for (var index = 0; index < NoHairAssets.Count; index++)
        {
            CopyUnrealAssetAndSidecars(sourceFiles[index], existingTargets[index]);
        }

        var invalidTarget = existingTargets.FirstOrDefault(path => !IsEditorAssetFile(path));
        if (invalidTarget is not null)
        {
            throw new InvalidOperationException(
                "No hair support could not be installed as editable Unreal project assets: " + invalidTarget);
        }

        return existingTargets;
    }

    internal static void ValidateNoHairAssetSet(string projectFile, string? sourceDirectory)
    {
        var targets = NoHairAssets
            .Select(assetPath => AssetPathToFile(projectFile, assetPath))
            .ToArray();
        if (targets.All(IsEditorAssetFile))
        {
            return;
        }

        _ = ResolveNoHairSourceFiles(sourceDirectory);
    }

    internal static IReadOnlyList<string> ResolveNoHairSourceFiles(string? sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException(
                "No hair needs the three assets from the original Unreal project. Choose its source folder, containing " +
                "PBAP_NoHairForCSM, SK_Charlie_ATTACH_Hair_Default, and SKEL_Humanoid_Head. " +
                "A file extracted from the cooked game cannot be installed into a .uproject.");
        }

        var root = Path.GetFullPath(sourceDirectory);
        var resolved = new List<string>();
        foreach (var assetPath in NoHairAssets)
        {
            var packageRelative = assetPath[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
            var exactCandidates = new[]
            {
                Path.Combine(root, "Content", packageRelative),
                Path.Combine(root, packageRelative),
            };
            var sourceFile = exactCandidates.FirstOrDefault(File.Exists);
            if (sourceFile is null)
            {
                var fileName = Path.GetFileName(packageRelative);
                sourceFile = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            if (sourceFile is null)
            {
                throw new InvalidOperationException(
                    "The selected No hair source folder is incomplete. It is missing " +
                    Path.GetFileName(packageRelative) + ".");
            }

            if (!IsEditorAssetFile(sourceFile))
            {
                throw new InvalidOperationException(
                    Path.GetFileName(sourceFile) +
                    " is a cooked game export, not an editable Unreal project asset. " +
                    "Choose the folder from the original No hair Unreal project instead.");
            }

            resolved.Add(sourceFile);
        }

        return resolved;
    }

    internal static bool IsEditorAssetFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Read(header) != header.Length)
            {
                return false;
            }

            return header.SequenceEqual(new byte[] { 0xC1, 0x83, 0x2A, 0x9E });
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyUnrealAssetAndSidecars(string sourceAsset, string destinationAsset)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceAsset)!;
        var destinationDirectory = Path.GetDirectoryName(destinationAsset)!;
        var sourceBaseName = Path.GetFileNameWithoutExtension(sourceAsset);
        Directory.CreateDirectory(destinationDirectory);

        var sourceFiles = Directory.EnumerateFiles(
                sourceDirectory,
                sourceBaseName + ".*",
                SearchOption.TopDirectoryOnly)
            .Where(IsUnrealPackageFile)
            .ToArray();
        var sourceNames = sourceFiles
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Directory.EnumerateFiles(
                     destinationDirectory,
                     sourceBaseName + ".*",
                     SearchOption.TopDirectoryOnly).Where(IsUnrealPackageFile))
        {
            if (!sourceNames.Contains(Path.GetFileName(existing)) &&
                !Path.GetFullPath(existing).Equals(Path.GetFullPath(sourceAsset), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(existing);
            }
        }

        foreach (var sourceFile in sourceFiles)
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            if (!Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(destinationFile), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceFile, destinationFile, true);
            }
        }
    }

    private static bool IsUnrealPackageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".uasset" or ".uexp" or ".ubulk" or ".uptnl";

    private static void EnsureInlineMaterialShaders(string projectDirectory)
    {
        const string sectionName = "/Script/UnrealEd.ProjectPackagingSettings";
        const string settingName = "bShareMaterialShaderCode";

        var configDirectory = Path.Combine(projectDirectory, "Config");
        var configPath = Path.Combine(configDirectory, "DefaultGame.ini");
        Directory.CreateDirectory(configDirectory);

        var lines = File.Exists(configPath)
            ? File.ReadAllLines(configPath).ToList()
            : new List<string>();

        var sectionHeader = "[" + sectionName + "]";
        var sectionStart = lines.FindIndex(line =>
            string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(sectionHeader);
            lines.Add(settingName + "=False");
        }
        else
        {
            var sectionEnd = lines.FindIndex(sectionStart + 1, line =>
            {
                var trimmed = line.Trim();
                return trimmed.StartsWith('[') && trimmed.EndsWith(']');
            });
            if (sectionEnd < 0)
            {
                sectionEnd = lines.Count;
            }

            var settingIndex = -1;
            for (var index = sectionStart + 1; index < sectionEnd; index++)
            {
                var trimmed = lines[index].TrimStart();
                if (trimmed.StartsWith(settingName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    settingIndex = index;
                    break;
                }
            }

            if (settingIndex >= 0)
            {
                lines[settingIndex] = settingName + "=False";
            }
            else
            {
                lines.Insert(sectionEnd, settingName + "=False");
            }
        }

        File.WriteAllLines(configPath, lines, new UTF8Encoding(false));
    }

    private static void CopyWithRelativePath(string projectDirectory, string sourceFile, string backupDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, sourceFile);
        var destination = Path.Combine(backupDirectory, "ProjectFiles", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourceFile, destination, true);
    }

    internal void PrepareProjectDescriptorForMigration(string projectFile, string editorExecutable)
    {
        var descriptorText = File.ReadAllText(projectFile);
        var root = JsonNode.Parse(
            descriptorText,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new InvalidOperationException("The .uproject file is not valid JSON.");

        if (root["Plugins"] is not JsonArray plugins)
        {
            plugins = [];
            root["Plugins"] = plugins;
        }

        var requiredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PythonScriptPlugin",
            "EditorScriptingUtilities",
            "GeometryScripting",
        };
        var availablePlugins = FindAvailablePlugins(projectFile, editorExecutable);

        foreach (var entry in plugins.OfType<JsonObject>())
        {
            var pluginName = entry["Name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(pluginName) || requiredPlugins.Contains(pluginName))
            {
                continue;
            }

            var enabled = entry["Enabled"]?.GetValue<bool>() == true;
            if (enabled && !availablePlugins.Contains(pluginName))
            {
                entry["Enabled"] = false;
                _log($"Temporarily disabled missing plugin {pluginName}; the .uproject will be restored afterward.");
            }
        }

        foreach (var pluginName in requiredPlugins)
        {
            var entry = plugins
                .OfType<JsonObject>()
                .FirstOrDefault(plugin =>
                    string.Equals(
                        plugin["Name"]?.GetValue<string>(),
                        pluginName,
                        StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                plugins.Add(new JsonObject
                {
                    ["Name"] = pluginName,
                    ["Enabled"] = true,
                });
            }
            else
            {
                entry["Enabled"] = true;
            }
        }

        var temporaryPath = projectFile + ".limelight-model-migrator.tmp";
        File.WriteAllText(
            temporaryPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporaryPath, projectFile, true);
    }

    private static HashSet<string> FindAvailablePlugins(string projectFile, string editorExecutable)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editorDirectory = Path.GetDirectoryName(Path.GetFullPath(editorExecutable))!;
        var engineDirectory = Path.GetFullPath(Path.Combine(editorDirectory, "..", ".."));
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var searchRoots = new[]
        {
            Path.Combine(engineDirectory, "Plugins"),
            Path.Combine(engineDirectory, "Platforms"),
            Path.Combine(projectDirectory, "Plugins"),
            Path.Combine(projectDirectory, "Mods"),
        };

        foreach (var root in searchRoots.Where(Directory.Exists))
        {
            try
            {
                foreach (var pluginFile in Directory.EnumerateFiles(root, "*.uplugin", SearchOption.AllDirectories))
                {
                    available.Add(Path.GetFileNameWithoutExtension(pluginFile));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Unreal will report a specific plugin error if an inaccessible location matters.
            }
            catch (IOException)
            {
                // A plugin directory can change while Epic Launcher updates the engine.
            }
        }

        return available;
    }

    private async Task<CookPackageResult> CookAndPackageSelectedChunkAsync(
        PatchOptions options,
        int chunkId,
        string timestamp,
        string packageLogPath)
    {
        try
        {
            var automationToolPath = FindAutomationTool(options.EditorExecutable);
            if (!File.Exists(automationToolPath))
            {
                return new CookPackageResult
                {
                    Success = false,
                    LogPath = packageLogPath,
                    Error = "Unreal Automation Tool was not found beside the selected Unreal Engine 5.7 editor.",
                };
            }

            _log("Patch complete. Cooking the Windows Shipping build...");
            _log("Unreal may take several minutes while it cooks shaders and assets.");
            var packagingStartedUtc = DateTime.UtcNow;
            var exitCode = await RunAutomationToolAsync(
                automationToolPath,
                BuildCookPackageArguments(options),
                Path.GetDirectoryName(options.ProjectFile)!,
                packageLogPath);
            if (exitCode != 0)
            {
                return new CookPackageResult
                {
                    Success = false,
                    LogPath = packageLogPath,
                    Error = await FindUsefulPackageErrorAsync(packageLogPath) ??
                            $"Unreal Automation Tool ended with exit code {exitCode}.",
                };
            }

            _log($"Collecting pakchunk{chunkId} package files...");
            var packageResult = CollectSelectedChunkPackage(
                options.ProjectFile,
                options.ModDisplayName,
                chunkId,
                timestamp,
                packagingStartedUtc,
                packageLogPath);
            _log("Selected chunk package ready: " + packageResult.ZipPath);
            return packageResult;
        }
        catch (Exception error)
        {
            return new CookPackageResult
            {
                Success = false,
                LogPath = packageLogPath,
                Error = error.Message,
            };
        }
    }

    internal static string FindAutomationTool(string editorExecutable)
    {
        var editorDirectory = Path.GetDirectoryName(Path.GetFullPath(editorExecutable))!;
        var engineDirectory = Path.GetFullPath(Path.Combine(editorDirectory, "..", ".."));
        return Path.Combine(
            engineDirectory,
            "Binaries",
            "DotNET",
            "AutomationTool",
            "AutomationTool.exe");
    }

    internal static IReadOnlyList<string> BuildCookPackageArguments(PatchOptions options)
    {
        var projectFile = Path.GetFullPath(options.ProjectFile);
        var editorExecutable = Path.GetFullPath(options.EditorExecutable);
        var targetName = Path.GetFileNameWithoutExtension(projectFile);
        return
        [
            "-ScriptsForProject=" + projectFile,
            "BuildCookRun",
            "-nop4",
            "-utf8output",
            "-unattended",
            "-nocompileeditor",
            "-skipbuildeditor",
            "-cook",
            "-project=" + projectFile,
            "-target=" + targetName,
            "-unrealexe=" + editorExecutable,
            "-platform=Win64",
            "-installed",
            "-SkipCookingErrorSummary",
            "-clientarchitecture=x64",
            "-stage",
            "-package",
            "-build",
            "-pak",
            "-iostore",
            "-compressed",
            "-manifests",
            "-clientconfig=Shipping",
            "-nodebuginfo",
            "-nocompile",
            "-nocompileuat",
        ];
    }

    internal static CookPackageResult CollectSelectedChunkPackage(
        string projectFile,
        string modDisplayName,
        int chunkId,
        string timestamp,
        DateTime packagingStartedUtc,
        string packageLogPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var projectName = Path.GetFileNameWithoutExtension(projectFile);
        var stagedPaksDirectory = Path.Combine(
            projectDirectory,
            "Saved",
            "StagedBuilds",
            "Windows",
            projectName,
            "Content",
            "Paks");
        if (!Directory.Exists(stagedPaksDirectory))
        {
            throw new InvalidOperationException(
                "Unreal reported success, but its staged Paks folder was not created.");
        }

        var chunkPattern = new Regex(
            $"^pakchunk{chunkId}(?:optional)?(?:_s[0-9]+)?-Windows\\.(?:pak|utoc|ucas)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var freshnessFloor = packagingStartedUtc.AddMinutes(-2);
        var selectedFiles = Directory
            .EnumerateFiles(stagedPaksDirectory, $"pakchunk{chunkId}*-Windows.*", SearchOption.TopDirectoryOnly)
            .Where(file => chunkPattern.IsMatch(Path.GetFileName(file)))
            .Where(file => File.GetLastWriteTimeUtc(file) >= freshnessFloor)
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var requiredNames = new[]
        {
            $"pakchunk{chunkId}-Windows.pak",
            $"pakchunk{chunkId}-Windows.utoc",
            $"pakchunk{chunkId}-Windows.ucas",
        };
        var foundNames = selectedFiles
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNames = requiredNames.Where(name => !foundNames.Contains(name)).ToList();
        if (missingNames.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unreal did not produce the selected chunk {chunkId}: " +
                string.Join(", ", missingNames) + ". Check the package log for cook or chunk-label warnings.");
        }

        var packagesRoot = Path.Combine(projectDirectory, "LimelightModelMigrator_Packages");
        Directory.CreateDirectory(packagesRoot);
        var safeModName = Regex.Replace(modDisplayName, "[^A-Za-z0-9_-]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeModName))
        {
            safeModName = "ModelMod";
        }

        var baseName = $"{timestamp}_{safeModName}_chunk{chunkId}";
        var outputDirectory = UniqueOutputPath(Path.Combine(packagesRoot, baseName));
        Directory.CreateDirectory(outputDirectory);

        var copiedFiles = new List<string>();
        foreach (var sourceFile in selectedFiles)
        {
            var destination = Path.Combine(outputDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destination, true);
            copiedFiles.Add(destination);
        }

        var packageInfoPath = Path.Combine(outputDirectory, "PACKAGE_INFO.txt");
        File.WriteAllText(
            packageInfoPath,
            "Limelight Model Migrator package\r\n" +
            "Mod: " + modDisplayName + "\r\n" +
            "Chunk: " + chunkId + "\r\n" +
            "Created: " + DateTime.Now.ToString("O") + "\r\n\r\n" +
            "Keep the .pak, .utoc, and .ucas files together when installing or sharing this mod.\r\n",
            new UTF8Encoding(false));
        copiedFiles.Add(packageInfoPath);

        var zipPath = outputDirectory + ".zip";
        ZipFile.CreateFromDirectory(outputDirectory, zipPath, CompressionLevel.Optimal, false);
        return new CookPackageResult
        {
            Success = true,
            LogPath = packageLogPath,
            OutputDirectory = outputDirectory,
            ZipPath = zipPath,
            PackageFiles = copiedFiles,
        };
    }

    private static string UniqueOutputPath(string desiredPath)
    {
        if (!Directory.Exists(desiredPath) && !File.Exists(desiredPath) && !File.Exists(desiredPath + ".zip"))
        {
            return desiredPath;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = desiredPath + "-" + suffix;
            if (!Directory.Exists(candidate) && !File.Exists(candidate) && !File.Exists(candidate + ".zip"))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not create a unique package output folder.");
    }

    internal async Task<int> RunAutomationToolAsync(
        string automationToolPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string fullLogPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = automationToolPath,
            WorkingDirectory = File.Exists(automationToolPath)
                ? Path.GetDirectoryName(Path.GetFullPath(automationToolPath))!
                : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var automationOutputRoot = Path.GetDirectoryName(Path.GetFullPath(fullLogPath))!;
        var automationLogsDirectory = Path.Combine(automationOutputRoot, "AutomationLogs");
        var automationSavedDirectory = Path.Combine(automationOutputRoot, "AutomationSaved");
        Directory.CreateDirectory(automationLogsDirectory);
        Directory.CreateDirectory(automationSavedDirectory);
        startInfo.Environment["uebp_LogFolder"] = automationLogsDirectory;
        startInfo.Environment["uebp_FinalLogFolder"] = automationLogsDirectory;
        startInfo.Environment["uebp_EngineSavedFolder"] = automationSavedDirectory;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unreal Automation Tool could not be started.");
        }

        var output = new StringBuilder();
        var outputLock = new object();
        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (outputLock)
                {
                    output.AppendLine(line);
                }

                if (IsUsefulPackageProgress(line))
                {
                    _log(CleanPackageProgress(line));
                }
            }
        }

        var stdoutTask = PumpAsync(process.StandardOutput);
        var stderrTask = PumpAsync(process.StandardError);
        await Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask);
        await File.WriteAllTextAsync(fullLogPath, output.ToString(), new UTF8Encoding(false));
        return process.ExitCode;
    }

    private static bool IsUsefulPackageProgress(string line) =>
        line.Contains("COOK COMMAND", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("STAGE COMMAND", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("PACKAGE COMMAND", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Creating pak", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Writing tocs", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("BUILD SUCCESSFUL", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("BUILD FAILED", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("AutomationTool exiting", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error:", StringComparison.OrdinalIgnoreCase);

    private static string CleanPackageProgress(string line)
    {
        var trimmed = line.Trim();
        var logPrefix = trimmed.IndexOf(": Display:", StringComparison.OrdinalIgnoreCase);
        return logPrefix >= 0 ? trimmed[(logPrefix + 10)..].Trim() : trimmed;
    }

    private static async Task<string?> FindUsefulPackageErrorAsync(string fullLogPath)
    {
        if (!File.Exists(fullLogPath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(fullLogPath);
        var useful = lines
            .Where(line =>
                line.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("BUILD FAILED", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("AutomationTool exiting with ExitCode=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(4)
            .ToList();
        return useful.Count == 0 ? null : string.Join(" ", useful);
    }

    private async Task<int> RunEditorAsync(PatchOptions options, string scriptPath, string fullLogPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.EditorExecutable,
            WorkingDirectory = Path.GetDirectoryName(options.ProjectFile)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(options.ProjectFile);
        // Unreal interprets backslash-number sequences as escaped characters in this
        // option. Forward slashes also keep paths with spaces and numeric folders safe.
        startInfo.ArgumentList.Add("-ExecutePythonScript=" + scriptPath.Replace('\\', '/'));
        foreach (var argument in new[]
                 {
                     "-unattended", "-nop4", "-nosplash", "-nullrhi", "-nosound",
                     "-NoZen", "-DDC-ForceMemoryCache", "-stdout", "-FullStdOutLogOutput", "-UTF8Output",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unreal Editor could not be started.");
        }

        var output = new StringBuilder();
        var outputLock = new object();

        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (outputLock)
                {
                    output.AppendLine(line);
                }

                const string marker = "[LIMELIGHT_MODEL_MIGRATOR]";
                var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    _log(line[(markerIndex + marker.Length)..].Trim());
                }
            }
        }

        var stdoutTask = PumpAsync(process.StandardOutput);
        var stderrTask = PumpAsync(process.StandardError);
        await Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask);

        await File.WriteAllTextAsync(fullLogPath, output.ToString(), new UTF8Encoding(false));
        return process.ExitCode;
    }

    private static async Task<string?> FindUsefulUnrealErrorAsync(string fullLogPath)
    {
        if (!File.Exists(fullLogPath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(fullLogPath);
        var markers = new[]
        {
            "LogPython: Error:",
            "LogEditorPythonExecuter: Error:",
            "LogPluginManager: Error:",
            "LogInit: Error:",
            "Fatal error:",
        };

        var errors = new List<string>();
        foreach (var marker in markers)
        {
            foreach (var line in lines.Where(line =>
                         line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                var message = line[(markerIndex + marker.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(message) &&
                    !errors.Contains(message, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(message);
                }
            }
        }

        return errors.Count == 0
            ? null
            : "Unreal reported: " + string.Join(" ", errors.Take(3));
    }

    private static void TryDeleteWorkingDirectory(string workDirectory)
    {
        try
        {
            var safeRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "LimelightModelMigrator"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedDirectory = Path.GetFullPath(workDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (resolvedDirectory.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolvedDirectory))
            {
                Directory.Delete(resolvedDirectory, true);
            }
        }
        catch
        {
            // A stale temporary script is harmless and can be removed by Windows later.
        }
    }
}
