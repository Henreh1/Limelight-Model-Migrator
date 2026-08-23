using LimelightModelMigrator;
using System.IO.Compression;
using System.Text.Json.Nodes;

if (args.Length > 0 && args[0] == "--echo-args")
{
    Console.WriteLine("ARG1=" + (args.Length > 1 ? args[1] : string.Empty));
    Console.WriteLine("ARG2=" + (args.Length > 2 ? args[2] : string.Empty));
    return;
}

var fixtureRoot = Path.Combine(
    Path.GetTempPath(),
    "LimelightModelMigrator.Tests",
    Guid.NewGuid().ToString("N"));

try
{
    var projectFile = Path.Combine(fixtureRoot, "Pagoda.uproject");
    Directory.CreateDirectory(fixtureRoot);
    File.WriteAllText(projectFile, """
        {
          "Plugins": [
            { "Name": "JsonAsAsset", "Enabled": true },
            { "Name": "BubblepopLightingTool", "Enabled": true }
          ]
        }
        """);

    CreateAsset("Pagoda/Characters/Player/Meshes/SK_Charlie.uasset");
    CreateAsset("OlderMods/Haru/Pagoda/Characters/Player/Meshes/SK_Charlie.uasset");
    CreateAsset("OlderMods/Fuji/Pagoda/Characters/Player/Meshes/SK_Charlie.uasset");
    CreateAsset("Pagoda/Characters/Player/Meshes/billieSK_Charlie.uasset");
    CreateAsset("Pagoda/Characters/Player/Meshes/BeatItSK_Charlie.uasset");
    CreateAsset("Pagoda/Characters/Player/Meshes/HanniSK_Charlie.uasset");
    CreateAsset("Pagoda/Characters/Player/No_Glasses_AnimBP.uasset");
    CreateAsset("Pagoda/Characters/Player/Meshes/PABP_Charlie.uasset");

    var metadataDirectory = Path.Combine(
        fixtureRoot,
        "Saved",
        "Cooked",
        "Windows",
        "Pagoda",
        "Metadata");
    Directory.CreateDirectory(metadataDirectory);
    File.WriteAllLines(Path.Combine(metadataDirectory, "AllChunksInfo.csv"),
    [
        "19,/Game/Pagoda/Characters/Player/Meshes/SK_Charlie,/Script/Engine.SkeletalMesh,Hard,1,None",
        "21,/Game/OlderMods/Haru/Pagoda/Characters/Player/Meshes/SK_Charlie,/Script/Engine.SkeletalMesh,Hard,1,None",
        "22,/Game/OlderMods/Fuji/Pagoda/Characters/Player/Meshes/SK_Charlie,/Script/Engine.SkeletalMesh,Hard,1,None",
        "23,/Game/Pagoda/Characters/Player/Meshes/billieSK_Charlie,/Script/Engine.SkeletalMesh,Hard,1,None",
        "24,/Game/Pagoda/Characters/Player/Meshes/BeatItSK_Charlie,/Script/Engine.SkeletalMesh,Hard,1,None",
        "20,/Game/Pagoda/Characters/Player/Meshes/PABP_Charlie,/Script/Engine.AnimBlueprint,Hard,1,None",
    ]);

    var candidates = PatcherService.DiscoverLegacyMods(projectFile);
    Expect(candidates.Count == 6, $"Expected 6 older mods, found {candidates.Count}.");
    Expect(candidates[0].AssetPath == PatcherService.CanonicalLegacyAsset,
        "The canonical legacy asset should be listed first.");
    Expect(candidates.Single(candidate => candidate.DisplayName == "Haru").ChunkId == 21,
        "Haru should retain its own chunk 21.");
    Expect(candidates.Single(candidate => candidate.DisplayName == "Fuji").ChunkId == 22,
        "Fuji should retain its own chunk 22.");
    Expect(candidates.Single(candidate => candidate.DisplayName == "Billie").ChunkId == 23,
        "A prefixed Billie mesh should be selectable and retain chunk 23.");
    Expect(candidates.Single(candidate => candidate.DisplayName == "Beat It").ChunkId == 24,
        "A camel-case BeatIt mesh should be shown as Beat It and retain chunk 24.");
    var hanni = candidates.Single(candidate => candidate.DisplayName == "Hanni");
    Expect(hanni.ChunkId == 100 && hanni.ChunkKind == ChunkAssignmentKind.Suggested,
        "A mod without cook metadata should receive a safe automatic chunk.");

    var blueprints = PatcherService.DiscoverSupportBlueprints(projectFile);
    Expect(blueprints.Count == 2, $"Expected 2 glasses blueprints, found {blueprints.Count}.");
    Expect(blueprints[0].DisplayName == "No Glasses AnimBP",
        "The no-glasses blueprint should be easy to find at the top of the selector.");
    Expect(blueprints.Single(candidate => candidate.DisplayName == "PABP Charlie").ChunkId == 20,
        "The canonical PABP should retain detected chunk information.");

    var noHairSourceDirectory = Path.Combine(fixtureRoot, "OriginalNoHairProject");
    foreach (var assetPath in PatcherService.NoHairAssets)
    {
        var relativePath = assetPath[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        var sourcePath = Path.Combine(noHairSourceDirectory, "Content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, [0xC1, 0x83, 0x2A, 0x9E, 0x57]);
    }

    var selectedSource = "/Game/Pagoda/Characters/Player/Meshes/BeatItSK_Charlie";
    var options = new PatchOptions(
        projectFile,
        "UnrealEditor-Cmd.exe",
        selectedSource,
        "Beat It",
        "/Game/Pagoda/Characters/Player/No_Glasses_AnimBP",
        true,
        noHairSourceDirectory,
        true,
        true,
        true,
        true,
        true,
        true,
        24,
        PatchRunMode.PatchCookAndPackage);
    var script = PatcherScript.Build(options, Path.Combine(fixtureRoot, "result.json"));
    Expect(script.Contains($"SOURCE_ASSET = \"{selectedSource}\"", StringComparison.Ordinal),
        "Generated Unreal script did not use the selected mod source.");
    Expect(script.Contains("MOD_KEY = \"Beat It\"", StringComparison.Ordinal),
        "Generated Unreal script did not carry the selected mod name.");
    Expect(script.Contains("MOD_CHUNK_ID = 24", StringComparison.Ordinal),
        "Generated Unreal script did not carry the selected mod chunk.");
    Expect(script.Contains("USE_DEDICATED_LABEL = True", StringComparison.Ordinal),
        "Generated Unreal script did not isolate an automatically assigned mod label.");
    Expect(script.Contains(
            "SUPPORT_BLUEPRINT_ASSET = \"/Game/Pagoda/Characters/Player/No_Glasses_AnimBP\"",
            StringComparison.Ordinal),
        "Generated Unreal script did not carry the selected glasses blueprint.");
    Expect(script.Contains("support_blueprints_added", StringComparison.Ordinal),
        "Generated Unreal script did not package the selected glasses blueprint.");
    Expect(script.Contains(
               "\"/Game/Pagoda/Characters/Player/Meshes/PBAP_NoHairForCSM\"",
               StringComparison.Ordinal) &&
           script.Contains("SK_Charlie_ATTACH_Hair_Default", StringComparison.Ordinal) &&
           script.Contains("SKEL_Humanoid_Head", StringComparison.Ordinal) &&
           script.Contains("no_hair_assets_added", StringComparison.Ordinal) &&
           script.Contains("assets_to_add.extend(no_hair_assets)", StringComparison.Ordinal) &&
           script.Contains("expected_no_hair_classes", StringComparison.Ordinal),
        "Generated Unreal script did not install and chunk the complete optional No hair asset set.");
    Expect(script.Contains("material_assets_added", StringComparison.Ordinal) &&
           script.Contains("collect_material_chunk_assets", StringComparison.Ordinal) &&
           script.Contains("include_soft_package_references=True", StringComparison.Ordinal) &&
           script.Contains("assets_to_add = [source_object] + list(target_objects) + material_chunk_assets", StringComparison.Ordinal),
        "Generated Unreal script did not explicitly assign material and texture dependencies to the selected chunk.");
    Expect(script.Contains("selected_material_parent_path", StringComparison.Ordinal) &&
           script.Contains("MIGRATED_MATERIAL_ROOT", StringComparison.Ordinal) &&
           script.Contains("safe_mod_asset_key", StringComparison.Ordinal) &&
           !script.Contains("for referencer_path in referencers", StringComparison.Ordinal),
        "Generated Unreal script did not isolate materials per selected community mod.");
    Expect(!script.Contains("Patchlight", StringComparison.OrdinalIgnoreCase),
        "Generated Unreal script still contains retired Patchlight branding.");

    var installedNoHairPaths = PatcherService.InstallNoHairAssetSet(projectFile, noHairSourceDirectory);
    Expect(installedNoHairPaths.Count == 3 &&
           installedNoHairPaths.All(path => PatcherService.IsEditorAssetFile(path)),
        "The complete editor-native No hair set was not installed into the chosen project.");

    var cookedNoHairDirectory = Path.Combine(fixtureRoot, "CookedNoHairExport");
    foreach (var assetPath in PatcherService.NoHairAssets)
    {
        var cookedPath = Path.Combine(
            cookedNoHairDirectory,
            "Content",
            assetPath[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(cookedPath)!);
        File.WriteAllBytes(cookedPath, [0x00, 0x00, 0x00, 0x00, 0x57]);
    }
    try
    {
        _ = PatcherService.ResolveNoHairSourceFiles(cookedNoHairDirectory);
        throw new InvalidOperationException("A cooked No hair export was incorrectly accepted as a project asset.");
    }
    catch (InvalidOperationException error) when (
        error.Message.Contains("cooked game export", StringComparison.OrdinalIgnoreCase))
    {
        // Expected: cooked packages cannot be opened or cooked by the editor.
    }

    var packageArguments = PatcherService.BuildCookPackageArguments(options);
    Expect(packageArguments.Contains("BuildCookRun") &&
           packageArguments.Contains("-cook") &&
           packageArguments.Contains("-stage") &&
           packageArguments.Contains("-package") &&
           packageArguments.Contains("-pak") &&
           packageArguments.Contains("-iostore") &&
           packageArguments.Contains("-manifests"),
        "The automated run should use Unreal's complete cook/package pipeline.");
    Expect(packageArguments.Contains("-project=" + Path.GetFullPath(projectFile)),
        "The automated package command did not target the selected project.");

    var fakeUatLogPath = Path.Combine(fixtureRoot, "fake_uat.log");
    var fakeUatExitCode = await new PatcherService(_ => { }).RunAutomationToolAsync(
        Environment.ProcessPath!,
        ["--echo-args", "value with spaces", "BuildCookRun"],
        fixtureRoot,
        fakeUatLogPath);
    var fakeUatLog = File.ReadAllText(fakeUatLogPath);
    Expect(fakeUatExitCode == 0 &&
           fakeUatLog.Contains("ARG1=value with spaces", StringComparison.Ordinal) &&
           fakeUatLog.Contains("ARG2=BuildCookRun", StringComparison.Ordinal),
        "The Automation Tool launcher should preserve arguments containing spaces.");

    var stagedPaksDirectory = Path.Combine(
        fixtureRoot,
        "Saved",
        "StagedBuilds",
        "Windows",
        "Pagoda",
        "Content",
        "Paks");
    Directory.CreateDirectory(stagedPaksDirectory);
    foreach (var fileName in new[]
             {
                 "pakchunk24-Windows.pak",
                 "pakchunk24-Windows.utoc",
                 "pakchunk24-Windows.ucas",
                 "pakchunk24optional-Windows.pak",
                 "pakchunk24optional-Windows.utoc",
                 "pakchunk24optional-Windows.ucas",
                 "pakchunk25-Windows.pak",
             })
    {
        File.WriteAllText(Path.Combine(stagedPaksDirectory, fileName), fileName);
    }

    var collectedPackage = PatcherService.CollectSelectedChunkPackage(
        projectFile,
        "Beat It",
        24,
        "20260823_030000",
        DateTime.UtcNow.AddMinutes(-1),
        Path.Combine(fixtureRoot, "package_output.log"));
    Expect(collectedPackage.Success && File.Exists(collectedPackage.ZipPath),
        "The selected chunk should be collected into a package ZIP.");
    Expect(collectedPackage.PackageFiles.Count == 7,
        "The package should contain the selected primary and optional chunk files plus its info file.");
    using (var packageZip = ZipFile.OpenRead(collectedPackage.ZipPath!))
    {
        Expect(packageZip.Entries.Any(entry => entry.FullName == "pakchunk24-Windows.pak") &&
               packageZip.Entries.Any(entry => entry.FullName == "pakchunk24-Windows.utoc") &&
               packageZip.Entries.Any(entry => entry.FullName == "pakchunk24-Windows.ucas") &&
               packageZip.Entries.All(entry => !entry.FullName.StartsWith("pakchunk25", StringComparison.OrdinalIgnoreCase)),
            "The ZIP should contain only the selected chunk rather than another mod's files.");
    }

    var editorExecutable = Path.Combine(
        fixtureRoot,
        "UE_5.7",
        "Engine",
        "Binaries",
        "Win64",
        "UnrealEditor-Cmd.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(editorExecutable)!);
    File.WriteAllBytes(editorExecutable, [0]);
    var buildVersionDirectory = Path.Combine(
        fixtureRoot,
        "UE_5.7",
        "Engine",
        "Build");
    Directory.CreateDirectory(buildVersionDirectory);
    File.WriteAllText(Path.Combine(buildVersionDirectory, "Build.version"),
        "{\"MajorVersion\":5,\"MinorVersion\":7,\"PatchVersion\":4}");
    Expect(PatcherService.IsUnreal57Editor(editorExecutable),
        "A portable Unreal 5.7 installation should be accepted by its Build.version metadata.");

    var communityEngineRoot = Path.Combine(fixtureRoot, "Community Tools", "UE_5.7");
    var launcherFile = Path.Combine(fixtureRoot, "LauncherInstalled.dat");
    File.WriteAllText(launcherFile, new JsonObject
    {
        ["InstallationList"] = new JsonArray
        {
            new JsonObject
            {
                ["InstallLocation"] = communityEngineRoot,
                ["AppName"] = "UE_5.7",
                ["ArtifactId"] = "UE_5.7",
            },
        },
    }.ToJsonString());
    var launcherCandidates = PatcherService.DiscoverEditorCandidatesFromLauncherFile(launcherFile);
    Expect(launcherCandidates.Single() == Path.Combine(
               communityEngineRoot,
               "Engine",
               "Binaries",
               "Win64",
               "UnrealEditor-Cmd.exe"),
        "Epic Launcher discovery should preserve a community user's custom drive and folder.");
    var availableProjectPlugin = Path.Combine(
        fixtureRoot,
        "Plugins",
        "BubblepopLightingTool",
        "BubblepopLightingTool.uplugin");
    Directory.CreateDirectory(Path.GetDirectoryName(availableProjectPlugin)!);
    File.WriteAllText(availableProjectPlugin, "{}");

    var migrationLogs = new List<string>();
    new PatcherService(migrationLogs.Add)
        .PrepareProjectDescriptorForMigration(projectFile, editorExecutable);
    var descriptor = JsonNode.Parse(File.ReadAllText(projectFile))!.AsObject();
    var plugins = descriptor["Plugins"]!.AsArray().OfType<JsonObject>().ToList();
    bool PluginEnabled(string name) => plugins.Single(plugin =>
        plugin["Name"]!.GetValue<string>() == name)["Enabled"]!.GetValue<bool>();
    Expect(!PluginEnabled("JsonAsAsset"),
        "A missing enabled plugin should be disabled for the temporary migration run.");
    Expect(PluginEnabled("BubblepopLightingTool"),
        "An available project plugin should remain enabled.");
    Expect(PluginEnabled("PythonScriptPlugin") &&
           PluginEnabled("EditorScriptingUtilities") &&
           PluginEnabled("GeometryScripting"),
        "Required Unreal migration plugins should be enabled.");
    Expect(migrationLogs.Any(line => line.Contains("JsonAsAsset", StringComparison.Ordinal)),
        "The user should be told when a missing plugin is temporarily disabled.");

    Console.WriteLine("PASS: portable engine discovery, named-mod/PABP/No-hair options, per-mod materials, automatic chunks, patch/package modes, selected-chunk ZIPs, and missing-plugin handling.");

    if (args.Length == 1)
    {
        Console.WriteLine("DISCOVERY: " + Path.GetFullPath(args[0]));
        foreach (var candidate in PatcherService.DiscoverLegacyMods(args[0]))
        {
            Console.WriteLine($"  {candidate.DisplayName} | {candidate.AssetPath} | {candidate.ChunkLabel}");
        }
    }

    void CreateAsset(string relativePath)
    {
        var fullPath = Path.Combine(
            fixtureRoot,
            "Content",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [0]);
    }
}
finally
{
    if (Directory.Exists(fixtureRoot))
    {
        Directory.Delete(fixtureRoot, true);
    }
}

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
