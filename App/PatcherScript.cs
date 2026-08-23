using System.Text.Json;

namespace LimelightModelMigrator;

internal static class PatcherScript
{
    public static string Build(PatchOptions options, string resultFile)
    {
        var targets = new List<string>();
        if (options.PatchBody)
        {
            targets.Add("/Game/Pagoda/Characters/Player/Meshes/SK_Charlie_Body");
        }

        if (options.PatchPreview)
        {
            targets.Add("/Game/Pagoda/Characters/Player/Meshes/SK_Charlie_Preview");
        }

        if (options.PatchCosmetics)
        {
            targets.Add("/Game/Pagoda/Characters/Player/Meshes/SK_Charlie_Cosmetics_Default");
        }

        var template = """
import json
import os
import re
import traceback
import unreal

SOURCE_ASSET = @@SOURCE@@
SUPPORT_BLUEPRINT_ASSET = @@SUPPORT_BLUEPRINT@@
NO_HAIR_PACKAGES = [
    "/Game/Pagoda/Characters/Player/Meshes/PBAP_NoHairForCSM",
    "/Game/Pagoda/Characters/Player/Meshes/SK_Charlie_ATTACH_Hair_Default",
    "/Game/Pagoda/Characters/Common/Skeletons/SKEL_Humanoid_Head",
]
NO_HAIR_ASSETS = @@NO_HAIR_ASSETS@@
TARGET_ASSETS = @@TARGETS@@
REPLACE_EXISTING = @@REPLACE@@
UPDATE_CHARACTER_LABEL = @@UPDATE_LABEL@@
USE_DEDICATED_LABEL = @@USE_DEDICATED_LABEL@@
MOD_CHUNK_ID = @@MOD_CHUNK_ID@@
MOD_KEY = @@MOD_KEY@@
RESULT_FILE = @@RESULT@@
DUMMY_MATERIAL_COUNT = 20
DUMMY_MATERIAL_PREFIX = "DAD_Dummy_"
LEGACY_MASTER_MATERIAL = "/Game/Pagoda/Characters/Materials/Master"
LEGACY_SHARED_MIGRATED_MASTER = "/Game/LimelightModelMigrator/Materials/M_ModelMigrationParent"
MIGRATED_MATERIAL_ROOT = "/Game/LimelightModelMigrator/Materials"

result = {
    "success": False,
    "source": SOURCE_ASSET,
    "created": [],
    "replaced": [],
    "skipped": [],
    "labels_updated": [],
    "support_blueprints_added": [],
    "material_assets_added": [],
    "no_hair_assets_added": [],
    "materials_protected": False,
    "dummy_material_slots_added": 0,
    "dummy_material_sections_added": 0,
    "material_parent_isolated": False,
    "material_instances_reparented": 0,
    "warnings": [],
}


def say(message):
    unreal.log("[LIMELIGHT_MODEL_MIGRATOR] " + message)


def warn(message):
    result["warnings"].append(message)
    unreal.log_warning("[LIMELIGHT_MODEL_MIGRATOR] " + message)


def asset_object_path(asset_path):
    asset_name = asset_path.rsplit("/", 1)[-1]
    return asset_path + "." + asset_name


def explicit_asset_path(value):
    try:
        return str(value.get_asset_path_name())
    except Exception:
        return str(value)


def material_parent_path(material_interface):
    if not isinstance(material_interface, unreal.MaterialInstanceConstant):
        return None
    parent = material_interface.get_editor_property("parent")
    return parent.get_path_name() if parent is not None else None


def safe_mod_asset_key():
    safe_key = re.sub(r"[^A-Za-z0-9_]+", "_", MOD_KEY).strip("_")
    return (safe_key or "SelectedMod")[:48]


def selected_material_parent_path():
    return MIGRATED_MATERIAL_ROOT + "/M_" + safe_mod_asset_key() + "_ModelMigrationParent"


def isolate_colliding_master_material(asset_subsystem, mesh):
    assigned_materials = [
        entry.get_editor_property("material_interface")
        for entry in list(mesh.get_editor_property("materials"))
        if entry.get_editor_property("material_interface") is not None
    ]
    assigned_parent_paths = {
        material_parent_path(material)
        for material in assigned_materials
    }
    isolated_master_path = selected_material_parent_path()
    isolated_master_object_path = asset_object_path(isolated_master_path)
    if isolated_master_object_path in assigned_parent_paths:
        result["material_parent_isolated"] = True
        say("The selected mod already uses its own migrated material parent.")
        return

    source_master_path = None
    for candidate in [LEGACY_MASTER_MATERIAL, LEGACY_SHARED_MIGRATED_MASTER]:
        if asset_object_path(candidate) in assigned_parent_paths:
            source_master_path = candidate
            break
    if source_master_path is None:
        return
    if not asset_subsystem.does_asset_exist(source_master_path):
        raise RuntimeError(
            "A selected custom material references a missing parent: " + source_master_path
        )

    if asset_subsystem.does_asset_exist(isolated_master_path):
        isolated_master = asset_subsystem.load_asset(isolated_master_path)
    else:
        say("Creating a material parent owned only by " + MOD_KEY + "...")
        isolated_master = asset_subsystem.duplicate_asset(
            source_master_path,
            isolated_master_path,
        )
    if isolated_master is None:
        raise RuntimeError("Could not create the selected mod's material parent.")
    if not asset_subsystem.save_loaded_asset(isolated_master, False):
        raise RuntimeError("Could not save the selected mod's material parent.")

    source_master_object_path = asset_object_path(source_master_path)
    reparented = 0
    for material in assigned_materials:
        if not isinstance(material, unreal.MaterialInstanceConstant):
            continue
        if material_parent_path(material) != source_master_object_path:
            continue

        unreal.MaterialEditingLibrary.set_material_instance_parent(
            material,
            isolated_master,
        )
        unreal.MaterialEditingLibrary.update_material_instance(material)
        if material_parent_path(material) != isolated_master.get_path_name():
            raise RuntimeError(
                "Could not move selected material instance to its unique parent: "
                + material.get_path_name()
            )
        if not asset_subsystem.save_loaded_asset(material, False):
            raise RuntimeError(
                "Could not save reparented material instance: " + material.get_path_name()
            )
        reparented += 1

    if reparented == 0:
        raise RuntimeError(
            "The selected mesh's material instances could not be moved to their mod-owned parent."
        )

    if source_master_path == LEGACY_MASTER_MATERIAL:
        remaining_referencers = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
            LEGACY_MASTER_MATERIAL,
            load_assets_to_confirm=True,
        )
        if not remaining_referencers:
            if not asset_subsystem.delete_asset(LEGACY_MASTER_MATERIAL):
                warn("The now-unused legacy Master package could not be removed.")
        else:
            say("Kept the legacy Master because other stored mods still reference it.")

    result["material_parent_isolated"] = True
    result["material_instances_reparented"] = reparented
    say(
        "Moved " + str(reparented)
        + " material instance(s) to a unique migrated shader path."
    )


def add_face_backed_dummy_sections(dynamic_mesh, material_ids):
    if not material_ids:
        return

    uv_count = dynamic_mesh.get_num_uv_sets()
    root_weights = [unreal.GeometryScriptBoneWeight(bone_index=0, weight=1.0)]
    dummy_normal = unreal.GeometryScriptTriangle(
        vector0=unreal.Vector(0.0, 0.0, 1.0),
        vector1=unreal.Vector(0.0, 0.0, 1.0),
        vector2=unreal.Vector(0.0, 0.0, 1.0),
    )
    dummy_uvs = unreal.GeometryScriptUVTriangle(
        uv0=unreal.Vector2D(0.0, 0.0),
        uv1=unreal.Vector2D(0.0, 0.0),
        uv2=unreal.Vector2D(0.0, 0.0),
    )

    # Each protected material must own a real section or the skeletal-mesh
    # cooker drops it. These 1 mm triangles are skinned to the root, stacked
    # inside the torso, and have zero-area UVs so they cannot affect the mod's
    # visible geometry or texture mapping.
    for material_id in material_ids:
        offset = material_id * 0.002
        positions = [
            unreal.Vector(offset, 0.0, 100.0),
            unreal.Vector(offset + 0.1, 0.0, 100.0),
            unreal.Vector(offset, 0.1, 100.0),
        ]
        vertex_ids = []
        for position in positions:
            _, vertex_id = dynamic_mesh.add_vertex_to_mesh(position, True)
            _, valid_vertex = dynamic_mesh.set_vertex_bone_weights(
                vertex_id,
                root_weights,
            )
            if not valid_vertex:
                raise RuntimeError("Could not skin a protected dummy face.")
            vertex_ids.append(vertex_id)

        _, triangle_id = dynamic_mesh.add_triangle_to_mesh(
            unreal.IntVector(vertex_ids[0], vertex_ids[1], vertex_ids[2]),
            material_id,
            True,
        )
        if triangle_id < 0:
            raise RuntimeError(
                "Could not create protected dummy section " + str(material_id + 1) + "."
            )

        _, valid_material = dynamic_mesh.set_triangle_material_id(
            triangle_id,
            material_id,
            True,
        )
        if not valid_material:
            raise RuntimeError("Could not assign a protected dummy material.")

        _, valid_normals = dynamic_mesh.set_mesh_triangle_normals(
            triangle_id,
            dummy_normal,
            True,
        )
        if not valid_normals:
            raise RuntimeError("Could not set protected dummy face normals.")

        for uv_index in range(uv_count):
            _, valid_uvs = dynamic_mesh.set_mesh_triangle_u_vs(
                uv_index,
                triangle_id,
                dummy_uvs,
                True,
            )
            if not valid_uvs:
                raise RuntimeError("Could not set protected dummy face UVs.")


def protect_material_slots(asset_subsystem, mesh):
    original_materials = list(mesh.get_editor_property("materials"))
    if not original_materials:
        warn("The legacy mesh has no materials to protect.")
        return

    original_names = [
        str(material.get_editor_property("material_slot_name"))
        for material in original_materials
    ]
    expected_dummy_names = [
        DUMMY_MATERIAL_PREFIX + str(index + 1).zfill(2)
        for index in range(DUMMY_MATERIAL_COUNT)
    ]
    has_dummy_slots = original_names[:DUMMY_MATERIAL_COUNT] == expected_dummy_names
    if not has_dummy_slots and any(
        name.startswith(DUMMY_MATERIAL_PREFIX) for name in original_names
    ):
        raise RuntimeError(
            "The legacy mesh contains a partial migration dummy-material range. "
            "Restore its most recent safety backup, then patch it again."
        )

    skeletal_subsystem = unreal.get_editor_subsystem(unreal.SkeletalMeshEditorSubsystem)
    lod_count = skeletal_subsystem.get_lod_count(mesh)
    if lod_count <= 0:
        raise RuntimeError("The legacy mesh does not contain a readable LOD.")

    default_material = unreal.load_asset("/Engine/EngineMaterials/DefaultMaterial")
    if default_material is None:
        raise RuntimeError("Unreal's default material could not be loaded.")

    if has_dummy_slots:
        new_materials = [
            material.get_editor_property("material_interface") or default_material
            for material in original_materials
        ]
        new_material_names = list(original_names)
    else:
        # Move every rendered material above the game's 0-19 runtime override
        # range, then create one tiny face-backed section for each protected ID.
        new_materials = [default_material] * DUMMY_MATERIAL_COUNT
        new_material_names = list(expected_dummy_names)
        for skeletal_material in original_materials:
            material_interface = skeletal_material.get_editor_property("material_interface")
            new_materials.append(material_interface or default_material)
            new_material_names.append(
                str(skeletal_material.get_editor_property("material_slot_name"))
            )

    def read_lod_dynamic_mesh(lod_index):
        dynamic_mesh = unreal.DynamicMesh()
        read_options = unreal.GeometryScriptCopyMeshFromAssetOptions(
            apply_build_settings=False,
            request_tangents=True,
            ignore_remove_degenerates=True,
            use_build_scale=False,
        )
        read_lod = unreal.GeometryScriptMeshReadLOD(
            lod_type=unreal.GeometryScriptLODType.SOURCE_MODEL,
            lod_index=lod_index,
        )
        _, read_outcome = unreal.GeometryScript_AssetUtils.copy_mesh_from_skeletal_mesh(
            mesh,
            dynamic_mesh,
            read_options,
            read_lod,
        )
        if read_outcome != unreal.GeometryScriptOutcomePins.SUCCESS:
            raise RuntimeError(
                "Could not read LOD " + str(lod_index) + " while protecting materials."
            )

        _, has_bone_weights = dynamic_mesh.mesh_has_bone_weights()
        if not has_bone_weights:
            raise RuntimeError(
                "LOD " + str(lod_index) + " has no readable skin weights; no material changes were saved."
            )
        return dynamic_mesh

    def write_lod_dynamic_mesh(dynamic_mesh, lod_index, replace_materials):
        write_options = unreal.GeometryScriptCopyMeshToAssetOptions(
            enable_recompute_normals=False,
            enable_recompute_tangents=not replace_materials,
            enable_remove_degenerates=False,
            replace_materials=replace_materials,
            new_materials=new_materials,
            new_material_slot_names=new_material_names,
            use_original_vertex_order=True,
            emit_transaction=False,
        )
        write_lod = unreal.GeometryScriptMeshWriteLOD(
            lod_index=lod_index,
            write_hi_res_source=False,
        )
        _, write_outcome = unreal.GeometryScript_AssetUtils.copy_mesh_to_skeletal_mesh(
            dynamic_mesh,
            mesh,
            write_options,
            write_lod,
        )
        if write_outcome != unreal.GeometryScriptOutcomePins.SUCCESS:
            raise RuntimeError(
                "Could not update LOD " + str(lod_index) + " while protecting materials."
            )

    changed = False
    say("Protecting custom materials with 20 face-backed dummy sections...")

    if not has_dummy_slots:
        # Unreal can discard low-ID sections when the material list and new
        # faces are both supplied in one skeletal-mesh write. Establish the
        # shifted 26-slot layout first, then add face-backed sections below.
        for lod_index in range(lod_count):
            dynamic_mesh = read_lod_dynamic_mesh(lod_index)
            # Descending order prevents an old material ID from colliding with
            # one that still needs to be shifted.
            for old_material_id in reversed(range(len(original_materials))):
                unreal.GeometryScript_Materials.remap_material_i_ds(
                    dynamic_mesh,
                    old_material_id,
                    old_material_id + DUMMY_MATERIAL_COUNT,
                )
            write_lod_dynamic_mesh(
                dynamic_mesh,
                lod_index,
                lod_index == 0,
            )
        changed = True

    for lod_index in range(lod_count):
        section_slots = [
            skeletal_subsystem.get_lod_material_slot(mesh, lod_index, section_index)
            for section_index in range(
                skeletal_subsystem.get_num_sections(mesh, lod_index)
            )
        ]
        missing_dummy_ids = [
            material_id
            for material_id in range(DUMMY_MATERIAL_COUNT)
            if material_id not in section_slots
        ]
        if not missing_dummy_ids:
            continue

        dynamic_mesh = read_lod_dynamic_mesh(lod_index)
        add_face_backed_dummy_sections(dynamic_mesh, missing_dummy_ids)
        write_lod_dynamic_mesh(dynamic_mesh, lod_index, False)

        changed = True
        result["dummy_material_sections_added"] = max(
            result["dummy_material_sections_added"],
            len(missing_dummy_ids),
        )

    for lod_index in range(lod_count):
        section_slots = [
            skeletal_subsystem.get_lod_material_slot(mesh, lod_index, section_index)
            for section_index in range(
                skeletal_subsystem.get_num_sections(mesh, lod_index)
            )
        ]
        missing_dummy_ids = [
            material_id
            for material_id in range(DUMMY_MATERIAL_COUNT)
            if material_id not in section_slots
        ]
        if missing_dummy_ids:
            raise RuntimeError(
                "Material protection verification failed for LOD " + str(lod_index)
                + "; missing face-backed sections: " + str(missing_dummy_ids) + "."
            )

    if changed and not asset_subsystem.save_loaded_asset(mesh, False):
        raise RuntimeError("Could not save material protection to the legacy mesh.")

    result["materials_protected"] = True
    if not has_dummy_slots:
        result["dummy_material_slots_added"] = DUMMY_MATERIAL_COUNT

    if result["dummy_material_sections_added"] > 0:
        say("Created 20 face-backed dummy sections and protected the custom materials.")
    else:
        say("The material sections are already fully protected.")


def label_chunk_id(label):
    try:
        return label.get_editor_property("rules").get_editor_property("chunk_id")
    except Exception:
        return None


def selected_label_name():
    return "LMM_" + safe_mod_asset_key() + "_Character"


def create_character_label():
    if MOD_CHUNK_ID is None:
        raise RuntimeError(
            "No matching Character Primary Asset Label exists and no usable mod chunk was selected."
        )

    asset_name = selected_label_name()
    package_path = "/Game/LimelightModelMigrator/Labels"
    existing_path = package_path + "/" + asset_name
    if unreal.EditorAssetLibrary.does_asset_exist(existing_path):
        existing = unreal.EditorAssetLibrary.load_asset(existing_path)
        if existing and isinstance(existing, unreal.PrimaryAssetLabel):
            return existing

    say("Creating a dedicated packaging label for " + MOD_KEY + "...")
    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
    factory = unreal.DataAssetFactory()
    factory.set_editor_property("data_asset_class", unreal.PrimaryAssetLabel)

    label = asset_tools.create_asset(
        asset_name,
        package_path,
        unreal.PrimaryAssetLabel,
        factory,
    )
    if label is None:
        raise RuntimeError("Could not create the Character Primary Asset Label.")
    return label


def apply_chunk_rules(label):
    if MOD_CHUNK_ID is None:
        return

    rules = label.get_editor_property("rules")
    rules.set_editor_property("priority", 1)
    rules.set_editor_property("chunk_id", MOD_CHUNK_ID)
    rules.set_editor_property("apply_recursively", True)
    rules.set_editor_property("cook_rule", unreal.PrimaryAssetCookRule.ALWAYS_COOK)
    label.set_editor_property("rules", rules)


def collect_material_chunk_assets(source_mesh):
    material_roots = []
    for skeletal_material in list(source_mesh.get_editor_property("materials")):
        material = skeletal_material.get_editor_property("material_interface")
        if material is None:
            continue
        material_path = material.get_path_name()
        if material_path.startswith("/Game/"):
            material_roots.append(material)

    if not material_roots:
        warn("The selected mesh has no project material assets to assign to its mod chunk.")
        return []

    registry = unreal.AssetRegistryHelpers.get_asset_registry()
    registry.wait_for_completion()
    dependency_options = unreal.AssetRegistryDependencyOptions(
        include_soft_package_references=True,
        include_hard_package_references=True,
        include_game_package_references=True,
        include_editor_only_package_references=False,
        include_searchable_names=False,
        include_soft_management_references=False,
        include_hard_management_references=False,
    )

    pending_packages = [
        material.get_path_name().split(".", 1)[0]
        for material in material_roots
    ]
    visited_packages = set()
    collected_assets = {}
    while pending_packages:
        package_path = pending_packages.pop()
        if package_path in visited_packages or not package_path.startswith("/Game/"):
            continue
        visited_packages.add(package_path)

        package_assets = registry.get_assets_by_package_name(
            unreal.Name(package_path),
            False,
            True,
        ) or []
        for asset_data in package_assets:
            if asset_data.is_redirector():
                continue
            asset = asset_data.get_asset()
            if asset is None:
                continue
            asset_path = asset.get_path_name()
            if asset_path.startswith("/Game/"):
                collected_assets[asset_path] = asset

        dependencies = registry.get_dependencies(
            unreal.Name(package_path),
            dependency_options,
        ) or []
        for dependency in dependencies:
            dependency_path = str(dependency)
            if (
                dependency_path.startswith("/Game/")
                and dependency_path not in visited_packages
            ):
                pending_packages.append(dependency_path)

    for material in material_roots:
        collected_assets[material.get_path_name()] = material

    ordered_assets = [
        collected_assets[path]
        for path in sorted(collected_assets.keys(), key=str.lower)
    ]
    result["material_assets_added"] = [
        asset.get_path_name()
        for asset in ordered_assets
    ]
    say(
        "Assigned " + str(len(ordered_assets))
        + " material, texture, and shader dependency asset(s) to mod chunk "
        + str(MOD_CHUNK_ID) + "."
    )
    return ordered_assets


def update_character_labels(
    asset_subsystem,
    source_object,
    target_objects,
    support_blueprint,
    no_hair_assets,
):
    if not UPDATE_CHARACTER_LABEL:
        return

    all_labels = []
    for path in asset_subsystem.list_assets("/Game", True, False):
        path_string = str(path)
        candidate = asset_subsystem.load_asset(path_string)
        if candidate and isinstance(candidate, unreal.PrimaryAssetLabel):
            all_labels.append(candidate)

    source_path = source_object.get_path_name()
    target_paths = {
        target_object.get_path_name()
        for target_object in target_objects
        if target_object is not None
    }

    selected_label = None
    if USE_DEDICATED_LABEL:
        selected_label = create_character_label()
        if selected_label not in all_labels:
            all_labels.append(selected_label)
    else:
        for label in all_labels:
            explicit_paths = {
                explicit_asset_path(asset)
                for asset in list(label.get_editor_property("explicit_assets"))
                if asset is not None
            }
            if source_path in explicit_paths:
                selected_label = label
                break

        if selected_label is None and MOD_CHUNK_ID is not None:
            for label in all_labels:
                label_name = label.get_name().lower()
                if "character" in label_name and label_chunk_id(label) == MOD_CHUNK_ID:
                    selected_label = label
                    break

    if selected_label is None:
        selected_label = create_character_label()
        all_labels.append(selected_label)

    for label in all_labels:
        if label == selected_label:
            continue
        explicit_assets = list(label.get_editor_property("explicit_assets"))
        filtered_assets = [
            asset
            for asset in explicit_assets
            if asset is None or explicit_asset_path(asset) not in target_paths
        ]
        if len(filtered_assets) != len(explicit_assets):
            label.modify()
            label.set_editor_property("explicit_assets", filtered_assets)
            if not asset_subsystem.save_loaded_asset(label, False):
                raise RuntimeError(
                    "Could not remove migrated targets from another packaging label: "
                    + label.get_path_name()
                )
            say("Removed active targets from another mod label: " + label.get_name())

    selected_label.modify()
    apply_chunk_rules(selected_label)
    explicit_assets = list(selected_label.get_editor_property("explicit_assets"))
    selected_source_package = SOURCE_ASSET.lower()
    filtered_assets = []
    for asset in explicit_assets:
        if asset is None:
            continue
        object_path = explicit_asset_path(asset)
        package_path = object_path.split(".", 1)[0]
        if package_path.lower().endswith("/sk_charlie") and package_path.lower() != selected_source_package:
            continue
        if (
            package_path.lower() in {path.lower() for path in NO_HAIR_PACKAGES}
            and not no_hair_assets
        ):
            continue
        filtered_assets.append(asset)

    existing_paths = {
        explicit_asset_path(asset)
        for asset in filtered_assets
        if asset is not None
    }
    material_chunk_assets = collect_material_chunk_assets(source_object)
    assets_to_add = [source_object] + list(target_objects) + material_chunk_assets
    if support_blueprint is not None:
        assets_to_add.append(support_blueprint)
    assets_to_add.extend(no_hair_assets)

    for asset in assets_to_add:
        if asset is None:
            continue
        asset_path = asset.get_path_name()
        if asset_path not in existing_paths:
            filtered_assets.append(asset)
            existing_paths.add(asset_path)

    if support_blueprint is not None:
        support_path = support_blueprint.get_path_name()
        result["support_blueprints_added"].append(support_path)
        say("Included selected glasses blueprint in the mod chunk: " + support_path)

    for no_hair_asset in no_hair_assets:
        no_hair_path = no_hair_asset.get_path_name()
        result["no_hair_assets_added"].append(no_hair_path)
        say("Included No hair support in the mod chunk: " + no_hair_path)

    selected_label.set_editor_property("explicit_assets", filtered_assets)
    if not asset_subsystem.save_loaded_asset(selected_label, False):
        raise RuntimeError("Could not save Primary Asset Label: " + selected_label.get_path_name())

    label_path = selected_label.get_path_name()
    result["labels_updated"].append(label_path)
    say("Updated selected mod packaging label: " + label_path)


try:
    say("Loading legacy replacement mesh...")
    asset_subsystem = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)

    if not asset_subsystem.does_asset_exist(SOURCE_ASSET):
        raise RuntimeError(
            "Legacy source mesh was not found at " + SOURCE_ASSET + ". "
            "Import or move your replacement mesh to that exact path first."
        )

    source_object = asset_subsystem.load_asset(SOURCE_ASSET)
    if source_object is None:
        raise RuntimeError("Unreal could not load the legacy source mesh: " + SOURCE_ASSET)

    source_class = str(source_object.get_class().get_name())
    if source_class != "SkeletalMesh":
        raise RuntimeError(
            "The source asset is a " + source_class + ", not a SkeletalMesh: " + SOURCE_ASSET
        )

    support_blueprint = None
    if SUPPORT_BLUEPRINT_ASSET:
        if not asset_subsystem.does_asset_exist(SUPPORT_BLUEPRINT_ASSET):
            raise RuntimeError(
                "The selected glasses blueprint was not found: " + SUPPORT_BLUEPRINT_ASSET
            )
        support_blueprint = asset_subsystem.load_asset(SUPPORT_BLUEPRINT_ASSET)
        if support_blueprint is None:
            raise RuntimeError(
                "Unreal could not load the selected glasses blueprint: " + SUPPORT_BLUEPRINT_ASSET
            )
        support_class = str(support_blueprint.get_class().get_name())
        if "Blueprint" not in support_class:
            raise RuntimeError(
                "The selected glasses asset is a " + support_class + ", not a Blueprint: "
                + SUPPORT_BLUEPRINT_ASSET
            )

    no_hair_assets = []
    if NO_HAIR_ASSETS:
        expected_no_hair_classes = ["AnimBlueprint", "SkeletalMesh", "Skeleton"]
        for no_hair_path, expected_class in zip(NO_HAIR_ASSETS, expected_no_hair_classes):
            if not asset_subsystem.does_asset_exist(no_hair_path):
                raise RuntimeError("No hair support was not installed at " + no_hair_path)
            no_hair_asset = asset_subsystem.load_asset(no_hair_path)
            if no_hair_asset is None:
                raise RuntimeError("Unreal could not load No hair support: " + no_hair_path)
            no_hair_class = str(no_hair_asset.get_class().get_name())
            if expected_class not in no_hair_class:
                raise RuntimeError(
                    "The No hair asset " + no_hair_path + " is a " + no_hair_class
                    + ", not a " + expected_class + "."
                )
            no_hair_assets.append(no_hair_asset)

    isolate_colliding_master_material(asset_subsystem, source_object)
    protect_material_slots(asset_subsystem, source_object)

    patched_objects = []
    for target_path in TARGET_ASSETS:
        target_name = target_path.rsplit("/", 1)[-1]
        target_exists = asset_subsystem.does_asset_exist(target_path)

        if target_exists and not REPLACE_EXISTING:
            result["skipped"].append(target_path)
            patched_objects.append(asset_subsystem.load_asset(target_path))
            say("Kept existing asset: " + target_name)
            continue

        action = "created"
        if target_exists:
            say("Replacing existing asset: " + target_name)
            if not asset_subsystem.delete_asset(target_path):
                raise RuntimeError("Could not replace existing asset: " + target_path)
            action = "replaced"
        else:
            say("Creating asset: " + target_name)

        duplicated = asset_subsystem.duplicate_asset(SOURCE_ASSET, target_path)
        if duplicated is None:
            raise RuntimeError("Could not duplicate the legacy mesh to " + target_path)

        if not asset_subsystem.save_loaded_asset(duplicated, False):
            raise RuntimeError("Could not save patched asset: " + target_path)

        result[action].append(target_path)
        patched_objects.append(duplicated)

    update_character_labels(
        asset_subsystem,
        source_object,
        patched_objects,
        support_blueprint,
        no_hair_assets,
    )
    result["success"] = True
    say("Patch completed successfully.")
except Exception as error:
    result["error"] = str(error)
    result["traceback"] = traceback.format_exc()
    unreal.log_error("[LIMELIGHT_MODEL_MIGRATOR] " + result["error"])
finally:
    os.makedirs(os.path.dirname(RESULT_FILE), exist_ok=True)
    with open(RESULT_FILE, "w", encoding="utf-8") as result_file:
        json.dump(result, result_file, indent=2)
""";

        return template
            .Replace("@@SOURCE@@", JsonSerializer.Serialize(options.SourceAsset), StringComparison.Ordinal)
            .Replace(
                "@@SUPPORT_BLUEPRINT@@",
                options.SupportBlueprintAsset is null
                    ? "None"
                    : JsonSerializer.Serialize(options.SupportBlueprintAsset),
                StringComparison.Ordinal)
            .Replace(
                "@@NO_HAIR_ASSETS@@",
                options.IncludeNoHair
                    ? JsonSerializer.Serialize(PatcherService.NoHairAssets)
                    : "[]",
                StringComparison.Ordinal)
            .Replace("@@TARGETS@@", JsonSerializer.Serialize(targets), StringComparison.Ordinal)
            .Replace("@@REPLACE@@", options.ReplaceExisting ? "True" : "False", StringComparison.Ordinal)
            .Replace("@@UPDATE_LABEL@@", options.UpdateCharacterLabel ? "True" : "False", StringComparison.Ordinal)
            .Replace("@@USE_DEDICATED_LABEL@@", options.UseDedicatedPackagingLabel ? "True" : "False", StringComparison.Ordinal)
            .Replace("@@MOD_CHUNK_ID@@", options.ModChunkId?.ToString() ?? "None", StringComparison.Ordinal)
            .Replace("@@MOD_KEY@@", JsonSerializer.Serialize(options.ModDisplayName), StringComparison.Ordinal)
            .Replace("@@RESULT@@", JsonSerializer.Serialize(resultFile), StringComparison.Ordinal);
    }
}
