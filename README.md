# Limelight Model Migrator

Limelight Model Migrator updates older Dead as Disco model replacement mods for
the current split Charlie mesh layout in Unreal Engine 5.7.

It is part of the Limelight family and uses the standard Limelight identity.

The tool is intended for community use. It does not contain user-profile or
project-location assumptions: projects may live on any drive, package and
backup folders are derived from the selected `.uproject`, and Unreal Engine 5.7
is discovered from Epic Launcher installation data or registered source builds.
Users with a portable engine build can set `UE_5_7_ROOT` or choose
`UnrealEditor-Cmd.exe` manually.

The only fixed Unreal asset paths are the Dead as Disco compatibility contract:
the older `SK_Charlie` location and the current body, preview, and cosmetics
targets that replace it. Mod folders, project names, material locations,
dependency graphs, PABP choices, chunk IDs, engine installs, backups, and package
outputs are discovered or derived at runtime.

## Multi-mod projects

Choose a `.uproject` and the app scans its `Content` folder for every legacy
replacement ending in `SK_Charlie.uasset`. This includes named copies such as
`hanniSK_Charlie` and `BeatItSK_Charlie`. Each candidate appears separately
with its Unreal asset path and detected cook chunk. Select one mod, migrate it,
then return to the list for the next stored mod.

If an older copy only appears in Unreal's catch-all chunk 0, the app now picks a
free dedicated chunk automatically. The automatic ID is shown beside the mod,
can still be changed manually, and is remembered after a successful migration.
Automatically assigned mods receive their own Limelight packaging label so a
shared project label cannot move the other stored mods with them.

## Glasses PABP

The optional **Glasses PABP** selector finds `PABP` and `No_Glasses` Blueprint
assets in the chosen project. Select the Blueprint used by that particular mod,
or leave it set to **None**. The selected Blueprint and its dependencies are
packaged in the same mod chunk; the original Blueprint asset is not renamed or
overwritten.

## No hair

No hair requires the complete source set used by the original Unreal project:
`PBAP_NoHairForCSM`, `SK_Charlie_ATTACH_Hair_Default`, and
`SKEL_Humanoid_Head`. If those three editable assets are already in the selected
project, enable **No hair** and continue. Otherwise, enable it and choose the
original No hair project or `Content` folder. The app verifies all three before
it changes anything, installs their package sidecars, and assigns the complete
set to the selected mod chunk for both **Patch only** and **Patch + package**.

A `.uasset` extracted from a cooked `.pak`/IoStore container is not an editable
Unreal project asset and cannot be recooked. Model Migrator rejects those files
immediately with a clear message instead of copying them into the project and
letting Unreal fail later. Existing files at the three target paths are included
in the safety backup before a selected source set replaces them.

## Patch and packaging modes

**Patch only** updates the selected older model in the project and stops there.
Use this when you want to reopen Unreal and handle cooking yourself.

**Patch + package** performs the same migration, runs Unreal's Windows Shipping
cook/package process, and then collects only the selected mod chunk's `.pak`,
`.utoc`, and `.ucas` files. The ready-to-share folder and ZIP are written beside
the project under `LimelightModelMigrator_Packages`. Keep the three container
file types together when installing the mod.

Before cooking, the selected mesh's project materials are added explicitly to
the mod label along with their material, texture, and shader dependencies. This
keeps the visible model assets in the same chunk instead of leaving textures in
chunk 0 or in another stored mod's chunk.

The Shipping cook can take several minutes and may cook the wider project, but
the final Limelight ZIP contains only the selected mod chunk (including its
optional IoStore containers when Unreal creates them). Stored source copies
remain in place, so you can return to the app and migrate or package the next
older mod afterward.

The canonical legacy path remains supported for single-mod projects.

## Safety

Every migration creates a timestamped `LimelightModelMigrator_Backups` folder
beside the project before changing assets or packaging settings. For a mod kept
in its own subfolder, that selected mod folder is included in the backup. The
chosen PABP and saved automatic chunk assignments are backed up as well.

If the project descriptor enables an editor plugin that is missing from disk,
Model Migrator temporarily disables that entry for its unattended Unreal run
and restores the original `.uproject` afterward.
