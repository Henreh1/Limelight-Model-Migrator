using System.Text.Json.Serialization;

namespace LimelightModelMigrator;

internal sealed record PatchOptions(
    string ProjectFile,
    string EditorExecutable,
    string SourceAsset,
    string ModDisplayName,
    string? SupportBlueprintAsset,
    bool IncludeNoHair,
    string? NoHairSourceDirectory,
    bool UseDedicatedPackagingLabel,
    bool PatchBody,
    bool PatchPreview,
    bool PatchCosmetics,
    bool ReplaceExisting,
    bool UpdateCharacterLabel,
    int? ModChunkId,
    PatchRunMode RunMode);

internal enum PatchRunMode
{
    PatchOnly,
    PatchCookAndPackage,
}

internal sealed record LegacyModCandidate(
    string DisplayName,
    string AssetPath,
    string FilePath,
    int? ChunkId,
    ChunkAssignmentKind ChunkKind)
{
    public string ChunkLabel => ChunkId is null
        ? "CHUNK UNAVAILABLE"
        : ChunkKind == ChunkAssignmentKind.Suggested
            ? $"CHUNK {ChunkId}  •  AUTO"
            : $"CHUNK {ChunkId}";

    public string ChunkStatusLabel => ChunkKind switch
    {
        ChunkAssignmentKind.Detected => "Detected from the last cook",
        ChunkAssignmentKind.Remembered => "Remembered for this mod",
        ChunkAssignmentKind.Suggested => "Safe unused chunk selected automatically",
        _ => "Chunk unavailable",
    };

    public string LocationLabel => AssetPath == PatcherService.CanonicalLegacyAsset
        ? "CANONICAL LEGACY PATH"
        : "STORED MOD COPY";
}

internal enum ChunkAssignmentKind
{
    Unavailable,
    Detected,
    Remembered,
    Suggested,
}

internal sealed record SupportBlueprintCandidate(
    string DisplayName,
    string? AssetPath,
    string? FilePath,
    int? ChunkId)
{
    public bool IsNone => string.IsNullOrWhiteSpace(AssetPath);

    public string DetailLabel => IsNone
        ? "Do not add a glasses blueprint"
        : ChunkId is null
            ? AssetPath!
            : $"{AssetPath}  •  chunk {ChunkId}";

    public static SupportBlueprintCandidate None { get; } =
        new("None", null, null, null);
}

internal sealed class PatchRunResult
{
    public required bool Success { get; init; }
    public required string BackupDirectory { get; init; }
    public required string FullLogPath { get; init; }
    public EnginePatchResult? EngineResult { get; init; }
    public CookPackageResult? PackageResult { get; init; }
    public string? Error { get; init; }
    public string? DescriptorRestoreWarning { get; init; }
}

internal sealed class CookPackageResult
{
    public required bool Success { get; init; }
    public required string LogPath { get; init; }
    public string? OutputDirectory { get; init; }
    public string? ZipPath { get; init; }
    public IReadOnlyList<string> PackageFiles { get; init; } = [];
    public string? Error { get; init; }
}

internal sealed class EnginePatchResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public List<string> Created { get; set; } = [];

    [JsonPropertyName("replaced")]
    public List<string> Replaced { get; set; } = [];

    [JsonPropertyName("skipped")]
    public List<string> Skipped { get; set; } = [];

    [JsonPropertyName("labels_updated")]
    public List<string> LabelsUpdated { get; set; } = [];

    [JsonPropertyName("support_blueprints_added")]
    public List<string> SupportBlueprintsAdded { get; set; } = [];

    [JsonPropertyName("material_assets_added")]
    public List<string> MaterialAssetsAdded { get; set; } = [];

    [JsonPropertyName("no_hair_assets_added")]
    public List<string> NoHairAssetsAdded { get; set; } = [];

    [JsonPropertyName("materials_protected")]
    public bool MaterialsProtected { get; set; }

    [JsonPropertyName("dummy_material_slots_added")]
    public int DummyMaterialSlotsAdded { get; set; }

    [JsonPropertyName("dummy_material_sections_added")]
    public int DummyMaterialSectionsAdded { get; set; }

    [JsonPropertyName("material_parent_isolated")]
    public bool MaterialParentIsolated { get; set; }

    [JsonPropertyName("material_instances_reparented")]
    public int MaterialInstancesReparented { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("traceback")]
    public string? Traceback { get; set; }
}
