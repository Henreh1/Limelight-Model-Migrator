# Dead as Disco Legacy Mesh Patcher

This Windows tool updates asset-replacement projects made with the older
`SK_Charlie` workflow for the August 2026 split-mesh update.

It duplicates the project's legacy replacement mesh at:

`/Game/Pagoda/Characters/Player/Meshes/SK_Charlie`

to the selected current targets:

- `SK_Charlie_Body` — in-game character body
- `SK_Charlie_Preview` — preview/shadow presentation mesh
- `SK_Charlie_Cosmetics_Default` — default cosmetic layer

The tool detects the legacy mesh's chunk from the project's last cook, then
creates or updates the `Character` Primary Asset Label so every patched mesh is
included in that same packaged mod chunk. If detection is unavailable, the
chunk can be entered manually in the app.

## Use

1. Close Unreal Editor.
2. Run `DeadAsDiscoMeshPatcher.exe`.
3. Choose the mod project's `Pagoda.uproject` file.
4. Confirm the detected Unreal Engine 5.7 editor path.
5. Leave the three recommended targets selected and click **Patch project**.
6. Reopen the project, check the new assets, and cook/package the mod again.
7. Deploy the newly cooked files for the detected mod chunk, using the usual
   `_P` filename suffix.

A timestamped `DeadAsDiscoMeshPatcher_Backups` folder is created beside the
project before the tool changes anything.

The original `SK_Charlie` asset is retained for legacy/menu references.

# Initial Release Notes from demo I built

- Rebuilt the interface in the Limelight visual style with a bounded,
  DPI-safe WPF layout and themed in-app dialogs.
- Fixed Unreal's command-line path escaping when a backup folder begins with
  a numeric timestamp (for example `20260818_...`).
- Improved failures so the useful Unreal/Python error is shown in the app.
- Replaced the remaining Windows scrollbar with Limelight's rounded dark rail.
- Added Patchlight's Limelight-derived circular P icon to the window and EXE.
- Fixed patched meshes being silently cooked into chunk 0. Patchlight now
  detects the legacy mod chunk, creates a missing `Character` packaging label,
  and assigns all selected replacement meshes to it.
