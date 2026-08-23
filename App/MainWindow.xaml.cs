using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace LimelightModelMigrator;

public partial class MainWindow : Window
{
    private bool _isRunning;
    private string? _dialogOpenPath;
    private string? _noHairSourceDirectory;
    private IReadOnlyList<LegacyModCandidate> _discoveredMods = [];
    private IReadOnlyList<SupportBlueprintCandidate> _discoveredBlueprints = [];

    private LegacyModCandidate? SelectedMod => ModListBox.SelectedItem as LegacyModCandidate;
    private SupportBlueprintCandidate? SelectedBlueprint =>
        PabpComboBox.SelectedItem as SupportBlueprintCandidate;

    public MainWindow(string? initialProject)
    {
        InitializeComponent();
        UpdateRunModeUi();

        EditorTextBox.Text = PatcherService.FindEditorExecutable() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(initialProject) &&
            string.Equals(Path.GetExtension(initialProject), ".uproject", StringComparison.OrdinalIgnoreCase))
        {
            ProjectTextBox.Text = Path.GetFullPath(initialProject);
            RefreshModDiscovery();
        }
        else
        {
            UpdateSelectedModUi();
        }
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose your Dead as Disco Unreal project",
            Filter = "Unreal project (*.uproject)|*.uproject",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == true)
        {
            ProjectTextBox.Text = picker.FileName;
            RefreshModDiscovery();
        }
    }

    private void BrowseEditor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose UnrealEditor-Cmd.exe from Unreal Engine 5.7",
            Filter = "Unreal command-line editor (UnrealEditor-Cmd.exe)|UnrealEditor-Cmd.exe",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == true)
        {
            EditorTextBox.Text = picker.FileName;
        }
    }

    private void BrowseNoHairSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose the original No hair Unreal project or Content folder",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var files = PatcherService.ResolveNoHairSourceFiles(picker.FolderName);
            _noHairSourceDirectory = picker.FolderName;
            NoHairSourceText.Text = $"Complete set selected ({files.Count} assets): {new DirectoryInfo(picker.FolderName).Name}";
            NoHairSourceText.Foreground = FindResource("CyanBrush") as System.Windows.Media.Brush;
        }
        catch (Exception error)
        {
            ShowDialogCard("NO HAIR SOURCE NOT READY", error.Message, null, null, false);
        }
    }

    private async void PatchProject_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        if (PatcherService.IsAnyUnrealEditorOpen())
        {
            ShowDialogCard(
                "CLOSE UNREAL EDITOR",
                "Unreal Editor is still open. Close it before patching the project, then try again.",
                null,
                null,
                false);
            return;
        }

        var selectedMod = SelectedMod;
        if (selectedMod is null)
        {
            ShowDialogCard(
                "SELECT AN OLDER MOD",
                "Choose which SK_Charlie replacement to migrate. Scan the project again if the list is empty.",
                null,
                null,
                false);
            return;
        }

        int? chunkId;
        try
        {
            chunkId = ResolveChunkId();
        }
        catch (Exception error)
        {
            ShowDialogCard("CHECK MOD CHUNK", error.Message, null, null, false);
            return;
        }

        var runMode = PatchAndPackageRadioButton.IsChecked == true
            ? PatchRunMode.PatchCookAndPackage
            : PatchRunMode.PatchOnly;

        var options = new PatchOptions(
            ProjectTextBox.Text.Trim().Trim('"'),
            EditorTextBox.Text.Trim().Trim('"'),
            selectedMod.AssetPath,
            selectedMod.DisplayName,
            SelectedBlueprint?.AssetPath,
            NoHairCheckBox.IsChecked == true,
            _noHairSourceDirectory,
            selectedMod.ChunkKind != ChunkAssignmentKind.Detected || selectedMod.ChunkId != chunkId,
            BodyCheckBox.IsChecked == true,
            PreviewCheckBox.IsChecked == true,
            CosmeticsCheckBox.IsChecked == true,
            ReplaceCheckBox.IsChecked == true,
            LabelCheckBox.IsChecked == true,
            chunkId,
            runMode);

        SetRunning(true);
        LogTextBox.Clear();
        AppendLog(runMode == PatchRunMode.PatchCookAndPackage
            ? "Migrating, cooking, and packaging selected mod: " + selectedMod.DisplayName + "..."
            : "Migrating selected mod: " + selectedMod.DisplayName + "...");

        PatchRunResult result;
        try
        {
            result = await new PatcherService(AppendLog).RunAsync(options);
        }
        catch (Exception error)
        {
            result = new PatchRunResult
            {
                Success = false,
                BackupDirectory = string.Empty,
                FullLogPath = string.Empty,
                Error = error.Message,
            };
        }
        finally
        {
            SetRunning(false);
        }

        if (result.Success)
        {
            if (result.PackageResult is { Success: true } package)
            {
                AppendLog("Done. The selected chunk package is ready to install or share.");
                ShowDialogCard(
                    "MOD PACKAGE READY",
                    BuildSuccessSummary(result),
                    package.ZipPath,
                    package.OutputDirectory,
                    true,
                    "OPEN PACKAGE");
            }
            else
            {
                AppendLog("Done. Reopen Unreal and cook the selected mod chunk when you are ready.");
                ShowDialogCard(
                    "MODEL MIGRATION COMPLETE",
                    BuildSuccessSummary(result),
                    result.BackupDirectory,
                    result.BackupDirectory,
                    true);
            }
        }
        else
        {
            var error = result.Error ?? "The patch did not complete.";
            var packageFailed = result.EngineResult?.Success == true &&
                                result.PackageResult is { Success: false };
            AppendLog(packageFailed ? "Packaging stopped: " + error : "Patch stopped: " + error);
            var openPath = File.Exists(result.FullLogPath)
                ? result.FullLogPath
                : result.BackupDirectory;
            ShowDialogCard(
                packageFailed ? "PATCHED — PACKAGE NOT COMPLETED" : "MIGRATION NOT COMPLETED",
                error,
                string.IsNullOrWhiteSpace(result.FullLogPath) ? null : result.FullLogPath,
                openPath,
                false,
                packageFailed ? "SHOW PACKAGE LOG" : null);
        }
    }

    private static string BuildSuccessSummary(PatchRunResult result)
    {
        var engine = result.EngineResult!;
        var changed = engine.Created.Count + engine.Replaced.Count;
        var summary = $"Migrated {changed} current model target(s).";

        if (engine.Skipped.Count > 0)
        {
            summary += $" Kept {engine.Skipped.Count} existing target(s).";
        }
        if (engine.LabelsUpdated.Count > 0)
        {
            summary += " The selected mod's packaging label was updated without moving the other mods.";
        }
        if (engine.SupportBlueprintsAdded.Count > 0)
        {
            summary += " The selected glasses PABP was included in the same mod chunk.";
        }
        if (engine.MaterialAssetsAdded.Count > 0)
        {
            summary += $" Assigned {engine.MaterialAssetsAdded.Count} material, texture, and shader asset(s) to the same mod chunk.";
        }
        if (engine.NoHairAssetsAdded.Count > 0)
        {
            summary += $" Installed and assigned all {engine.NoHairAssetsAdded.Count} No hair support assets to the same mod chunk.";
        }
        if (engine.MaterialsProtected)
        {
            summary += engine.DummyMaterialSectionsAdded > 0
                ? " Added 20 face-backed dummy sections so the game cannot replace the custom textures."
                : " The material sections were already protected.";
        }
        if (engine.MaterialParentIsolated)
        {
            summary += engine.MaterialInstancesReparented > 0
                ? $" Moved {engine.MaterialInstancesReparented} material instance(s) to a unique shader path so they load in the game."
                : " The custom material shader path was already isolated.";
        }
        if (result.PackageResult is { Success: true } package)
        {
            summary += $" Cooked and packaged {package.PackageFiles.Count - 1} selected chunk file(s) into a ready-to-share ZIP.";
        }
        if (engine.Warnings.Count > 0)
        {
            summary += "\n\nWarning: " + string.Join(" ", engine.Warnings);
        }

        return summary;
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        ConfigurationCard.IsEnabled = !running;
        ModSelectionCard.IsEnabled = !running;
        TargetsCard.IsEnabled = !running;
        PatchButton.IsEnabled = !running && SelectedMod is not null;
        RunProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunModePanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = running
            ? PatchAndPackageRadioButton.IsChecked == true
                ? "PATCHING, COOKING + PACKAGING..."
                : "UNREAL IS MIGRATING..."
            : SelectedMod is null
                ? "SELECT AN OLDER MOD"
                : PatchAndPackageRadioButton.IsChecked == true
                    ? "READY TO BUILD PACKAGE"
                    : "READY TO MIGRATE";
        StatusText.Foreground = FindResource(running ? "PinkBrush" : "CyanBrush") as System.Windows.Media.Brush;
        Cursor = running ? Cursors.Wait : Cursors.Arrow;
        if (!running)
        {
            UpdateRunModeUi();
        }
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => AppendLog(message));
            return;
        }

        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogTextBox.ScrollToEnd();
    }

    private void ShowDialogCard(
        string title,
        string message,
        string? displayPath,
        string? openPath,
        bool success,
        string? openButtonText = null)
    {
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogPath.Text = displayPath ?? string.Empty;
        DialogPathBorder.Visibility = string.IsNullOrWhiteSpace(displayPath)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _dialogOpenPath = openPath;
        DialogOpenButton.Visibility = string.IsNullOrWhiteSpace(openPath)
            ? Visibility.Collapsed
            : Visibility.Visible;
        DialogOpenButton.Content = openButtonText ??
                                   (File.Exists(openPath ?? string.Empty) ? "SHOW FULL LOG" : "OPEN BACKUP");
        DialogIcon.Foreground = FindResource(success ? "CyanBrush" : "PinkBrush") as System.Windows.Media.Brush;
        DialogOverlay.Visibility = Visibility.Visible;
    }

    private void DialogClose_Click(object sender, RoutedEventArgs e)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        _dialogOpenPath = null;
    }

    private void DialogOpen_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dialogOpenPath))
        {
            return;
        }

        var arguments = File.Exists(_dialogOpenPath)
            ? $"/select,\"{_dialogOpenPath}\""
            : $"\"{_dialogOpenPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    private void Window_PreviewDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var project = files.FirstOrDefault(file =>
            string.Equals(Path.GetExtension(file), ".uproject", StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            ProjectTextBox.Text = project;
            RefreshModDiscovery();
        }
    }

    private void RefreshMods_Click(object sender, RoutedEventArgs e) => RefreshModDiscovery();

    private void RunMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateRunModeUi();
    }

    private void NoHairOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateRunModeUi();
        }
    }

    private void UpdateRunModeUi()
    {
        var packageMode = PatchAndPackageRadioButton.IsChecked == true;
        NoHairSourcePanel.Visibility = NoHairCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        PatchButton.Content = packageMode ? "MIGRATE + BUILD PACKAGE" : "MIGRATE SELECTED MOD";
        RunModeHelpText.Text = packageMode
            ? "Runs Unreal's Windows Shipping cook, then collects only this mod chunk into a ZIP."
            : "Updates the project only. You can reopen Unreal and cook it yourself later.";

        if (packageMode || NoHairCheckBox.IsChecked == true)
        {
            LabelCheckBox.IsChecked = true;
            LabelCheckBox.IsEnabled = false;
        }
        else
        {
            LabelCheckBox.IsEnabled = !_isRunning;
        }

        if (!_isRunning && SelectedMod is not null)
        {
            StatusText.Text = packageMode ? "READY TO BUILD PACKAGE" : "READY TO MIGRATE";
        }
    }

    private void RefreshModDiscovery()
    {
        var project = ProjectTextBox.Text.Trim().Trim('"');
        RefreshBlueprintDiscovery(project);
        _discoveredMods = PatcherService.DiscoverLegacyMods(project);
        ModListBox.ItemsSource = _discoveredMods;

        if (_discoveredMods.Count == 0)
        {
            ModScanSummaryText.Text = File.Exists(project)
                ? "No older SK_Charlie model replacements were found under Content."
                : "Choose a valid .uproject to scan for older model mods.";
            ModListBox.SelectedItem = null;
            UpdateSelectedModUi();
            return;
        }

        ModScanSummaryText.Text = _discoveredMods.Count == 1
            ? "Found 1 older model replacement. Its mod chunk is ready."
            : $"Found {_discoveredMods.Count} older model replacements. Missing chunk IDs were assigned safely.";
        ModListBox.SelectedIndex = 0;
    }

    private void RefreshBlueprintDiscovery(string project)
    {
        var previousPath = SelectedBlueprint?.AssetPath;
        _discoveredBlueprints = PatcherService.DiscoverSupportBlueprints(project);
        var choices = new List<SupportBlueprintCandidate> { SupportBlueprintCandidate.None };
        choices.AddRange(_discoveredBlueprints);
        PabpComboBox.ItemsSource = choices;

        var previous = choices.FirstOrDefault(candidate =>
            string.Equals(candidate.AssetPath, previousPath, StringComparison.OrdinalIgnoreCase));
        PabpComboBox.SelectedItem = previous ?? SupportBlueprintCandidate.None;
        PabpScanSummaryText.Text = _discoveredBlueprints.Count switch
        {
            0 => "No PABP or No_Glasses blueprint found.",
            1 => "Found 1 glasses blueprint in this project.",
            _ => $"Found {_discoveredBlueprints.Count} glasses blueprints. Choose the one for this mod.",
        };
    }

    private void ModListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateSelectedModUi();

    private void UpdateSelectedModUi()
    {
        var selected = SelectedMod;
        if (selected is null)
        {
            SelectedModText.Text = "No older model mod selected";
            ChunkTextBox.Clear();
            ChunkStatusText.Text = "Select a mod";
            PatchButton.IsEnabled = false;
            if (!_isRunning)
            {
                StatusText.Text = File.Exists(ProjectTextBox.Text.Trim().Trim('"'))
                    ? "SELECT AN OLDER MOD"
                    : "CHOOSE A PROJECT";
            }
            return;
        }

        SelectedModText.Text = selected.DisplayName + "  •  " + selected.AssetPath;
        if (selected.ChunkId is not null)
        {
            ChunkTextBox.Text = selected.ChunkId.Value.ToString();
            ChunkStatusText.Text = selected.ChunkStatusLabel;
        }
        else
        {
            ChunkTextBox.Clear();
            ChunkStatusText.Text = "Enter a chunk manually";
        }

        PatchButton.IsEnabled = !_isRunning;
        if (!_isRunning)
        {
            StatusText.Text = PatchAndPackageRadioButton.IsChecked == true
                ? "READY TO BUILD PACKAGE"
                : "READY TO MIGRATE";
        }
    }

    private int? ResolveChunkId()
    {
        if (LabelCheckBox.IsChecked != true)
        {
            return null;
        }

        var value = ChunkTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            var selected = SelectedMod;
            if (selected?.ChunkId is not null)
            {
                ChunkTextBox.Text = selected.ChunkId.Value.ToString();
                ChunkStatusText.Text = selected.ChunkStatusLabel;
                return selected.ChunkId;
            }

            var suggested = PatcherService.SuggestUnusedChunkId(
                ProjectTextBox.Text.Trim().Trim('"'));
            if (suggested is not null)
            {
                ChunkTextBox.Text = suggested.Value.ToString();
                ChunkStatusText.Text = "Safe unused chunk selected automatically";
                return suggested;
            }

            throw new InvalidOperationException(
                "No free mod chunk was available automatically. Enter a positive chunk ID manually.");
        }

        if (!int.TryParse(value, out var chunkId) || chunkId <= 0)
        {
            throw new InvalidOperationException("Enter a positive mod chunk ID, such as 19.");
        }

        return chunkId;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        e.Cancel = true;
        ShowDialogCard(
            "MIGRATION IN PROGRESS",
            "Unreal Engine is still working. Wait for the migration to finish before closing this window.",
            null,
            null,
            false);
    }

    private void MinimiseWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximiseWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
