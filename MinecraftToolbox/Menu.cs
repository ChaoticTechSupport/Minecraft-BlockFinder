using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using fNbt;

namespace MinecraftToolbox;

public class ClosestMatch
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public double Distance { get; set; }
    public string BlockState { get; set; } = "";
    public string RegionFile { get; set; } = "";
}

public partial class Menu : Control
{
    // App windows and shared launcher controls
    private Window _finderWindow = null!;
    private Window _browserWindow = null!;
    private Window _editorWindow = null!;
    private Label _globalDirStatus = null!;

    // Block Finder tab fields
    private LineEdit _pathEdit = null!;
    private LineEdit _targetEdit = null!;
    private LineEdit _xEdit = null!, _yEdit = null!, _zEdit = null!;
    private Button _scanButton = null!;
    private Button _stopButton = null!;
    private ProgressBar _progressBar = null!;
    private RichTextLabel _output = null!;

    // Global directory field (above tabs)
    private LineEdit _globalDirEdit = null!;
    private Button _globalDirBrowseButton = null!;

    // File Browser tab fields
    private Tree _fileListTree = null!;
    private Label _fileBrowserStatus = null!;

    // File Editor tab fields
    private Tree _nbtTree = null!;
    private LineEdit _elementSearchEdit = null!;
    private Button _findElementButton = null!;
    private Label _elementSearchStatus = null!;
    private RichTextLabel _editorOutput = null!;
    private Label _editorTitle = null!;
    private Button _saveFileButton = null!;
    private Button _editValueButton = null!;
    private PopupPanel _editPopup = null!;
    private LineEdit _editValueEdit = null!;

    // File dialogs
    private FileDialog _fileDialog = null!;
    private FileDialog _globalDirDialog = null!;

    private string _settingsPath = "user://settings.cfg";
    private ConfigFile _config = new();

    private ClosestMatch? _closestMatch;
    private string _currentTarget = "";
    private double _refX, _refY, _refZ;
    private System.Threading.CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private double _minDistSq = double.MaxValue;

    // File editor state
    private string? _saveDirectory;
    private string? _currentFilePath;
    private NbtCompound? _currentNbtRoot;
    private RegionFile? _currentRegionFile;
    private int? _currentRegionChunkIndex;
    private Dictionary<TreeItem, NbtTag> _nbtNodeMap = new();
    private Dictionary<TreeItem, int> _regionNodeMap = new();
    private readonly Dictionary<int, NbtCompound> _regionChunkCache = new();
    private readonly HashSet<int> _dirtyRegionChunks = new();
    private NbtTag? _selectedNbtTag;
    private bool _updatingNbtTree;
    private int _regionSelectionRequestId;
    private int _elementSearchRequestId;
    private bool _elementSearchInProgress;
    private bool _selectingSearchResult;
    private List<string> _supportedExtensions = new() { ".nbt", ".dat", ".dat_old", ".mca" };

    public override void _Ready()
    {
        _finderWindow = GetNode<Window>("FinderWindow");
        _browserWindow = GetNode<Window>("BrowserWindow");
        _editorWindow = GetNode<Window>("EditorWindow");

        _globalDirEdit = GetNode<LineEdit>("Launcher/Content/DirectoryRow/GlobalDirectoryEdit");
        _globalDirBrowseButton = GetNode<Button>("Launcher/Content/DirectoryRow/BrowseGlobalDirectoryButton");
        _globalDirStatus = GetNode<Label>("Launcher/Content/GlobalDirectoryStatus");
        _globalDirEdit.TextSubmitted += OnGlobalDirTextSubmitted;
        _globalDirBrowseButton.Pressed += OnBrowseGlobalDirectory;

        GetNode<Button>("Launcher/Content/FinderButton").Pressed += () => ShowWindow(_finderWindow);
        GetNode<Button>("Launcher/Content/BrowserButton").Pressed += () => ShowWindow(_browserWindow);
        GetNode<Button>("Launcher/Content/EditorButton").Pressed += () => ShowWindow(_editorWindow);

        _finderWindow.CloseRequested += () => _finderWindow.Hide();
        _browserWindow.CloseRequested += () => _browserWindow.Hide();
        _editorWindow.CloseRequested += () => _editorWindow.Hide();

        _pathEdit = GetNode<LineEdit>("FinderWindow/Content/Layout/PathRow/PathEdit");
        _targetEdit = GetNode<LineEdit>("FinderWindow/Content/Layout/TargetEdit");
        _xEdit = GetNode<LineEdit>("FinderWindow/Content/Layout/CoordinatesRow/XEdit");
        _yEdit = GetNode<LineEdit>("FinderWindow/Content/Layout/CoordinatesRow/YEdit");
        _zEdit = GetNode<LineEdit>("FinderWindow/Content/Layout/CoordinatesRow/ZEdit");
        _scanButton = GetNode<Button>("FinderWindow/Content/Layout/ButtonsRow/ScanButton");
        _stopButton = GetNode<Button>("FinderWindow/Content/Layout/ButtonsRow/StopButton");
        _progressBar = GetNode<ProgressBar>("FinderWindow/Content/Layout/ProgressBar");
        _output = GetNode<RichTextLabel>("FinderWindow/Content/Layout/Output");
        GetNode<Button>("FinderWindow/Content/Layout/PathRow/BrowsePathButton").Pressed +=
            () => _fileDialog.PopupCentered();
        _pathEdit.TextSubmitted += _ => SaveSettings();
        _scanButton.Pressed += OnScanPressed;
        _stopButton.Pressed += OnStopPressed;

        _fileListTree = GetNode<Tree>("BrowserWindow/Content/Layout/FileListTree");
        _fileBrowserStatus = GetNode<Label>("BrowserWindow/Content/Layout/FileBrowserStatus");
        _fileListTree.ItemSelected += OnFileListItemSelected;
        GetNode<Button>("BrowserWindow/Content/Layout/Toolbar/RefreshButton").Pressed += RefreshFileList;
        GetNode<Button>("BrowserWindow/Content/Layout/Toolbar/OpenFolderButton").Pressed +=
            OpenSaveDirectoryInFileManager;

        _nbtTree = GetNode<Tree>("EditorWindow/Content/Layout/WorkArea/TreePanel/NbtTree");
        _elementSearchEdit = GetNode<LineEdit>("EditorWindow/Content/Layout/WorkArea/TreePanel/ElementSearchRow/ElementSearchEdit");
        _findElementButton = GetNode<Button>("EditorWindow/Content/Layout/WorkArea/TreePanel/ElementSearchRow/FindElementButton");
        _elementSearchStatus = GetNode<Label>("EditorWindow/Content/Layout/WorkArea/TreePanel/ElementSearchStatus");
        _editorTitle = GetNode<Label>("EditorWindow/Content/Layout/Title");
        _editorOutput = GetNode<RichTextLabel>("EditorWindow/Content/Layout/WorkArea/DetailsPanel/EditorOutput");
        _saveFileButton =
            GetNode<Button>("EditorWindow/Content/Layout/WorkArea/DetailsPanel/EditButtons/SaveFileButton");
        _editValueButton =
            GetNode<Button>("EditorWindow/Content/Layout/WorkArea/DetailsPanel/EditButtons/EditValueButton");
        _nbtTree.ItemSelected += OnNbtTreeItemSelected;
        _elementSearchEdit.TextChanged += _ => CancelElementSearch("Search canceled because the query changed.");
        _elementSearchEdit.TextSubmitted += _ => OnFindElementPressed();
        _findElementButton.Pressed += OnFindElementPressed;
        _saveFileButton.Pressed += OnSaveFilePressed;
        _editValueButton.Pressed += EditSelectedTag;
        GetNode<Button>("EditorWindow/Content/Layout/WorkArea/DetailsPanel/EditButtons/CopyPathButton").Pressed +=
            CopySelectedTagPath;

        _editPopup = GetNode<PopupPanel>("EditorWindow/EditPopup");
        _editValueEdit = GetNode<LineEdit>("EditorWindow/EditPopup/Content/Layout/EditValueEdit");
        GetNode<Button>("EditorWindow/EditPopup/Content/Layout/Buttons/ApplyEditButton").Pressed += ApplySelectedTagEdit;
        GetNode<Button>("EditorWindow/EditPopup/Content/Layout/Buttons/CancelEditButton").Pressed += () => _editPopup.Hide();

        _fileDialog = GetNode<FileDialog>("FinderWindow/RegionDirectoryDialog");
        _fileDialog.DirSelected += path =>
        {
            _pathEdit.Text = path;
            SaveSettings();
        };
        _globalDirDialog = GetNode<FileDialog>("Launcher/GlobalDirectoryDialog");
        _globalDirDialog.DirSelected += OnGlobalDirectorySelected;

        LoadSettings();
    }

    private void ShowWindow(Window window)
    {
        window.Show();
    }

    private void CopySelectedTagPath()
    {
        if (_selectedNbtTag != null)
        {
            DisplayServer.ClipboardSet(GetTagPath(_selectedNbtTag));
        }
    }

    private void OnBrowseGlobalDirectory()
    {
        _globalDirDialog.PopupCentered();
    }

    private void OnGlobalDirTextSubmitted(string path)
    {
        ApplyGlobalDirectory(path);
    }

    private void OnGlobalDirectorySelected(string path)
    {
        ApplyGlobalDirectory(path);
    }

    private bool ApplyGlobalDirectory(string path, bool persistSettings = true)
    {
        path = path.Trim();
        if (string.IsNullOrEmpty(path))
        {
            SetGlobalDirectoryStatus("Enter a directory path.", true);
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                SetGlobalDirectoryStatus($"Directory does not exist: {fullPath}", true);
                return false;
            }

            _saveDirectory = fullPath;
            _globalDirEdit.Text = fullPath;
            SetGlobalDirectoryStatus($"Shared directory: {fullPath}", false);

            string regionPath = Path.Combine(fullPath, "region");
            _pathEdit.Text = Directory.Exists(regionPath) ? regionPath : fullPath;
            RefreshFileList();
            if (persistSettings) SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            SetGlobalDirectoryStatus($"Invalid directory path: {ex.Message}", true);
            return false;
        }
    }

    private void SetGlobalDirectoryStatus(string message, bool isError)
    {
        _globalDirStatus.Text = message;
        if (isError)
        {
            _globalDirStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
            GD.PrintErr(message);
        }
        else
        {
            _globalDirStatus.RemoveThemeColorOverride("font_color");
        }
    }

    private void OpenSaveDirectoryInFileManager()
    {
        string? dir = _saveDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _fileBrowserStatus.Text = "No valid save directory selected.";
            _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
            return;
        }

        if (OS.GetName() == "Linux")
        {
            Process.Start("xdg-open", dir);
        }
        else if (OS.GetName() == "Windows")
        {
            Process.Start(new ProcessStartInfo("explorer", dir) { UseShellExecute = true });
        }
        else if (OS.GetName() == "macOS")
        {
            Process.Start("open", dir);
        }
    }

    private void RefreshFileList()
    {
        _fileListTree.Clear();
        // After Clear(), we need to create a new root item
        var root = _fileListTree.CreateItem();

        if (string.IsNullOrEmpty(_saveDirectory))
        {
            var item = _fileListTree.CreateItem(root);
            item.SetText(0, "No directory selected");
            item.SetCustomColor(0, new Color(0.5f, 0.5f, 0.5f));
            _fileBrowserStatus.Text = "No directory selected";
            _fileBrowserStatus.RemoveThemeColorOverride("font_color");
            return;
        }

        if (!Directory.Exists(_saveDirectory))
        {
            var item = _fileListTree.CreateItem(root);
            item.SetText(0, $"Directory not found: {_saveDirectory}");
            item.SetCustomColor(0, new Color(1f, 0.3f, 0.3f));
            _fileBrowserStatus.Text = $"Error: Directory not found";
            _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
            GD.PrintErr($"[FileBrowser] Directory does not exist: {_saveDirectory}");
            return;
        }

        // Reset status color
        _fileBrowserStatus.RemoveThemeColorOverride("font_color");

        // Search for supported files recursively
        var files = new List<string>();
        var errors = new List<string>();
        foreach (var ext in _supportedExtensions)
        {
            try
            {
                var found = Directory.GetFiles(_saveDirectory, $"*{ext}", SearchOption.AllDirectories);
                files.AddRange(found);
                GD.Print($"[FileBrowser] Found {found.Length} files with extension {ext}");
            }
            catch (Exception ex)
            {
                errors.Add($"{ext}: {ex.Message}");
                GD.PrintErr($"[FileBrowser] Error searching for {ext}: {ex.Message}");
            }
        }

        GD.Print($"[FileBrowser] Total files found: {files.Count} in {_saveDirectory}");

        if (files.Count == 0)
        {
            var item = _fileListTree.CreateItem(root);
            string msg = "No files found";
            if (errors.Count > 0)
            {
                msg += $"\nErrors: {string.Join(", ", errors)}";
            }

            item.SetText(0, msg);
            item.SetCustomColor(0, new Color(0.5f, 0.5f, 0.5f));
            _fileBrowserStatus.Text = $"0 files found in: {_saveDirectory}";
            if (errors.Count > 0)
            {
                _fileBrowserStatus.Text += $" | Errors: {string.Join(", ", errors)}";
                _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
            }

            return;
        }

        _fileBrowserStatus.Text = $"{files.Count} files found in: {_saveDirectory}";

        // Group by relative directory
        var grouped = files.GroupBy(f =>
        {
            var relative = Path.GetRelativePath(_saveDirectory, Path.GetDirectoryName(f) ?? ".");
            if (relative == ".") return "(root)";
            return relative;
        }).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            TreeItem? dirItem = null;
            if (group.Key != "(root)")
            {
                dirItem = _fileListTree.CreateItem(root);
                dirItem.SetText(0, $"📁 {group.Key}");
            }

            foreach (var file in group.OrderBy(f => Path.GetFileName(f)))
            {
                var item = _fileListTree.CreateItem(dirItem ?? root);
                string relativePath = Path.GetRelativePath(_saveDirectory, file);
                item.SetText(0, relativePath);
                item.SetMetadata(0, Variant.From(file));
            }
        }

        GD.Print($"[FileBrowser] Tree populated with {files.Count} items across {grouped.Count()} groups");
    }

    private void OnFileListItemSelected()
    {
        var item = _fileListTree.GetSelected();
        if (item == null) return;

        string? filePath = item.GetMetadata(0).As<string>();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _fileBrowserStatus.Text = "Select a supported file to open it.";
            _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
            return;
        }

        LoadNbtFile(filePath);
    }

    private void LoadNbtFile(string filePath)
    {
        CancelElementSearch("Search canceled because the opened file changed.");
        _regionSelectionRequestId++;
        if (string.Equals(Path.GetExtension(filePath), ".mca", StringComparison.OrdinalIgnoreCase))
        {
            LoadRegionFile(filePath);
            return;
        }

        try
        {
            var nbtFile = new NbtFile();
            nbtFile.LoadFromFile(filePath);
            _currentRegionFile = null;
            _currentRegionChunkIndex = null;
            _regionChunkCache.Clear();
            _dirtyRegionChunks.Clear();
            _selectedNbtTag = null;
            _currentNbtRoot = nbtFile.RootTag;
            _currentFilePath = filePath;
            _nbtNodeMap.Clear();
            _regionNodeMap.Clear();
            _nbtTree.Clear();
            _nbtTree.CreateItem(); // root

            // Build tree
            var rootItem = _nbtTree.GetRoot();
            BuildNbtTreeItem(rootItem, _currentNbtRoot);

            _saveFileButton.Disabled = false;
            _editorTitle.Text = $"NBT File Editor — {Path.GetFileName(filePath)}";
            _editorOutput.Clear();
            _editorOutput.AppendText($"[color=green]Loaded: {Path.GetFileName(filePath)}[/color]\n");
            _editorOutput.AppendText($"Root: {_currentNbtRoot.Name}\n");
            _editorOutput.AppendText($"Type: Compound ({_currentNbtRoot.Count} keys)\n");
            _fileBrowserStatus.Text = $"Loaded: {Path.GetFileName(filePath)}";
            _fileBrowserStatus.RemoveThemeColorOverride("font_color");
            _editValueButton.Disabled = true;
            ShowWindow(_editorWindow);
        }
        catch (Exception ex)
        {
            _fileBrowserStatus.Text = $"Could not load file: {ex.Message}";
            _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
        }
    }

    private void LoadRegionFile(string filePath)
    {
        CancelElementSearch("Search canceled because the opened file changed.");
        try
        {
            var regionFile = new RegionFile(filePath);
            var chunkIndices = regionFile.GetChunkIndices();

            _currentRegionFile = regionFile;
            _currentRegionChunkIndex = null;
            _currentNbtRoot = null;
            _currentFilePath = filePath;
            _regionChunkCache.Clear();
            _dirtyRegionChunks.Clear();
            _selectedNbtTag = null;
            _saveFileButton.Disabled = true;
            _editValueButton.Disabled = true;
            _editorTitle.Text = $"Region File Editor — {Path.GetFileName(filePath)}";
            PopulateRegionChunks(null);
            _editorOutput.Clear();
            _editorOutput.AppendText($"Loaded region: {Path.GetFileName(filePath)}\n");
            _editorOutput.AppendText($"Chunks: {chunkIndices.Count}\nSelect a chunk to inspect its NBT data.\n");
            _fileBrowserStatus.Text = $"Loaded region: {Path.GetFileName(filePath)} ({chunkIndices.Count} chunks)";
            _fileBrowserStatus.RemoveThemeColorOverride("font_color");
            ShowWindow(_editorWindow);
        }
        catch (Exception ex)
        {
            _fileBrowserStatus.Text = $"Could not load region: {ex.Message}";
            _fileBrowserStatus.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f));
        }
    }

    private void PopulateRegionChunks(int? expandedChunkIndex)
    {
        if (_currentRegionFile == null) return;

        _updatingNbtTree = true;
        try
        {
            _nbtTree.Clear();
            _nbtNodeMap.Clear();
            _regionNodeMap.Clear();
            var rootItem = _nbtTree.CreateItem();

            foreach (int index in _currentRegionFile.GetChunkIndices())
            {
                var chunkItem = _nbtTree.CreateItem(rootItem);
                chunkItem.SetText(0, $"Chunk {index} ({index % 32}, {index / 32})" +
                                     (_dirtyRegionChunks.Contains(index) ? " *" : ""));
                _regionNodeMap[chunkItem] = index;

                if (expandedChunkIndex == index && _regionChunkCache.TryGetValue(index, out var chunkRoot))
                {
                    _nbtNodeMap[chunkItem] = chunkRoot;
                    BuildNbtTreeItem(chunkItem, chunkRoot);
                }
            }

            var selectedItem = _nbtNodeMap.FirstOrDefault(entry => ReferenceEquals(entry.Value, _selectedNbtTag)).Key;
            selectedItem?.Select(0);
        }
        finally
        {
            _updatingNbtTree = false;
        }
    }

    private void LoadRegionChunk(int index)
    {
        if (_currentRegionFile == null) return;

        try
        {
            if (!_regionChunkCache.TryGetValue(index, out var chunkRoot))
            {
                chunkRoot = _currentRegionFile.ReadChunk(index);
                _regionChunkCache[index] = chunkRoot;
            }

            _currentRegionChunkIndex = index;
            _currentNbtRoot = chunkRoot;
            _selectedNbtTag = chunkRoot;
            _saveFileButton.Disabled = false;
            _editValueButton.Disabled = true;
            PopulateRegionChunks(index);
            ShowTagDetails(chunkRoot);
            _editorOutput.AppendText($"\nChunk index: {index} ({index % 32}, {index / 32})\n");
        }
        catch (Exception ex)
        {
            _currentRegionChunkIndex = null;
            _currentNbtRoot = null;
            _selectedNbtTag = null;
            _editorOutput.Clear();
            _editorOutput.AppendText($"[color=red]Could not load chunk {index}: {ex.Message}[/color]\n");
            _saveFileButton.Disabled = true;
            _editValueButton.Disabled = true;
            _editPopup.Hide();
        }
    }

    private void OnNbtTreeItemSelected()
    {
        if (_updatingNbtTree || _selectingSearchResult) return;
        var item = _nbtTree.GetSelected();
        if (item == null) return;

        CancelElementSearch("Search canceled because the selection changed.");
        int requestId = ++_regionSelectionRequestId;
        if (_regionNodeMap.TryGetValue(item, out int chunkIndex))
        {
            CallDeferred(nameof(LoadRegionChunkDeferred), chunkIndex, requestId);
            return;
        }

        if (_nbtNodeMap.TryGetValue(item, out var tag))
        {
            _selectedNbtTag = tag;
            ShowTagDetails(tag);
            // Enable edit button only for primitive types
            bool isEditable = tag is NbtByte or NbtShort or NbtInt or NbtLong or NbtFloat or NbtDouble or NbtString;
            _editValueButton.Disabled = !isEditable;
        }
    }

    private void CancelElementSearch(string reason)
    {
        _elementSearchRequestId++;
        if (!_elementSearchInProgress) return;

        _elementSearchInProgress = false;
        _elementSearchStatus.Text = reason;
    }

    private async void OnFindElementPressed()
    {
        string query = _elementSearchEdit.Text.Trim();
        int requestId = ++_elementSearchRequestId;
        if (string.IsNullOrEmpty(query))
        {
            _elementSearchStatus.Text = "Enter a key or value to search for.";
            return;
        }

        if (_currentNbtRoot == null && _currentRegionFile == null)
        {
            _elementSearchStatus.Text = "Open an NBT file or region file before searching.";
            return;
        }

        string? filePath = _currentFilePath;
        NbtTag? selectedTag = _selectedNbtTag;
        RegionFile? regionFile = _currentRegionFile;
        _elementSearchInProgress = true;
        _elementSearchStatus.Text = "Searching…";

        try
        {
            if (regionFile == null)
            {
                NbtTag? match = NbtSearch.FindNextMatch(_currentNbtRoot!, query, selectedTag, includeRoot: false);
                if (!IsElementSearchCurrent(requestId, filePath, regionFile)) return;
                if (match == null)
                {
                    _elementSearchStatus.Text = "No matching element found.";
                    return;
                }

                SelectSearchResult(match, null, null);
                return;
            }

            await SearchRegionForElement(query, selectedTag, requestId, filePath, regionFile);
        }
        catch (Exception ex)
        {
            if (IsElementSearchCurrent(requestId, filePath, regionFile))
                _elementSearchStatus.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (requestId == _elementSearchRequestId) _elementSearchInProgress = false;
        }
    }

    private async Task SearchRegionForElement(string query, NbtTag? selectedTag, int requestId, string? filePath,
        RegionFile regionFile)
    {
        var chunkIndices = regionFile.GetChunkIndices();
        if (chunkIndices.Count == 0)
        {
            _elementSearchStatus.Text = "The region file contains no chunks.";
            return;
        }

        int? currentChunkIndex = _currentRegionChunkIndex is int activeIndex && chunkIndices.Contains(activeIndex)
            ? activeIndex
            : null;
        NbtCompound? currentChunkRoot = null;
        var skippedChunks = new List<string>();

        if (currentChunkIndex is int currentIndex)
        {
            try
            {
                currentChunkRoot = GetChunkForSearch(regionFile, currentIndex);
                NbtTag? match = NbtSearch.FindNextMatch(currentChunkRoot, query, selectedTag, wrap: false);
                if (match != null && IsElementSearchCurrent(requestId, filePath, regionFile))
                {
                    SelectSearchResult(match, currentIndex, currentChunkRoot);
                    return;
                }
            }
            catch (Exception ex)
            {
                skippedChunks.Add($"{currentIndex}: {ex.Message}");
            }
        }

        int currentPosition = currentChunkIndex is int chunkIndex ? chunkIndices.ToList().IndexOf(chunkIndex) : -1;
        for (int offset = 1; offset <= chunkIndices.Count; offset++)
        {
            if (!IsElementSearchCurrent(requestId, filePath, regionFile)) return;

            int position = currentPosition < 0 ? offset - 1 : (currentPosition + offset) % chunkIndices.Count;
            int index = chunkIndices[position];
            if (currentChunkIndex == index) continue;

            _elementSearchStatus.Text = $"Searching chunk {index} ({offset}/{chunkIndices.Count})…";
            try
            {
                bool alreadyCached = _regionChunkCache.TryGetValue(index, out var chunkRoot);
                chunkRoot ??= regionFile.ReadChunk(index);
                NbtTag? match = NbtSearch.FindNextMatch(chunkRoot, query, null, wrap: false);
                if (match != null)
                {
                    if (!alreadyCached) _regionChunkCache[index] = chunkRoot;
                    SelectSearchResult(match, index, chunkRoot);
                    return;
                }
            }
            catch (Exception ex)
            {
                skippedChunks.Add($"{index}: {ex.Message}");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (!IsElementSearchCurrent(requestId, filePath, regionFile)) return;
        if (currentChunkRoot != null && currentChunkIndex is int wrapIndex)
        {
            NbtTag? wrappedMatch = NbtSearch.FindNextMatch(currentChunkRoot, query, selectedTag);
            if (wrappedMatch != null)
            {
                SelectSearchResult(wrappedMatch, wrapIndex, currentChunkRoot);
                return;
            }
        }

        string skippedMessage = skippedChunks.Count == 0
            ? ""
            : $" Skipped {skippedChunks.Count} unreadable chunk(s): {string.Join("; ", skippedChunks.Take(3))}";
        _elementSearchStatus.Text = $"No matching element found.{skippedMessage}";
    }

    private NbtCompound GetChunkForSearch(RegionFile regionFile, int index)
    {
        if (_regionChunkCache.TryGetValue(index, out var cachedRoot)) return cachedRoot;
        return regionFile.ReadChunk(index);
    }

    private bool IsElementSearchCurrent(int requestId, string? filePath, RegionFile? regionFile)
    {
        return requestId == _elementSearchRequestId &&
               string.Equals(filePath, _currentFilePath, StringComparison.Ordinal) &&
               ReferenceEquals(regionFile, _currentRegionFile);
    }

    private void SelectSearchResult(NbtTag tag, int? chunkIndex, NbtCompound? chunkRoot)
    {
        if (chunkIndex is int index && chunkRoot != null)
        {
            _currentRegionChunkIndex = index;
            _currentNbtRoot = chunkRoot;
            _regionChunkCache[index] = chunkRoot;
            _saveFileButton.Disabled = false;
            PopulateRegionChunks(index);
        }

        var item = _nbtNodeMap.FirstOrDefault(entry => ReferenceEquals(entry.Value, tag)).Key;
        if (item == null)
        {
            _elementSearchStatus.Text = "The matching element could not be displayed.";
            return;
        }

        _selectingSearchResult = true;
        try
        {
            for (TreeItem? ancestor = item.GetParent(); ancestor != null; ancestor = ancestor.GetParent())
                ancestor.Collapsed = false;
            item.Select(0);
            _selectedNbtTag = tag;
            ShowTagDetails(tag);
            bool isEditable = tag is NbtByte or NbtShort or NbtInt or NbtLong or NbtFloat or NbtDouble or NbtString;
            _editValueButton.Disabled = !isEditable;
            _elementSearchStatus.Text = $"Found: {GetTagPath(tag)}";
        }
        finally
        {
            _selectingSearchResult = false;
        }
    }

    private void LoadRegionChunkDeferred(int index, int requestId)
    {
        if (requestId != _regionSelectionRequestId || _currentRegionFile == null) return;

        var selectedItem = _nbtTree.GetSelected();
        if (selectedItem == null || !_regionNodeMap.TryGetValue(selectedItem, out int selectedIndex) ||
            selectedIndex != index)
        {
            return;
        }

        LoadRegionChunk(index);
    }

    private void ShowTagDetails(NbtTag tag)
    {
        _editorOutput.Clear();
        string typeStr = GetTagTypeName(tag);
        string valueStr = FormatTagValue(tag);
        string path = GetTagPath(tag);

        _editorOutput.AppendText($"[color=cyan][b]Type:[/b][/color] {typeStr}\n");
        _editorOutput.AppendText($"[color=cyan][b]Path:[/b][/color] {path}\n");
        _editorOutput.AppendText($"[color=cyan][b]Value:[/b][/color]\n{valueStr}\n");

        if (tag is NbtCompound compound && compound.Count > 0)
        {
            _editorOutput.AppendText($"\n[color=cyan][b]Children:[/b][/color] {compound.Count}\n");
        }
        else if (tag is NbtList list && list.Count > 0)
        {
            _editorOutput.AppendText($"\n[color=cyan][b]Elements:[/b][/color] {list.Count}\n");
        }
    }

    private string GetTagTypeName(NbtTag tag)
    {
        return tag switch
        {
            NbtByte => "Byte (TAG_Byte)",
            NbtShort => "Short (TAG_Short)",
            NbtInt => "Int (TAG_Int)",
            NbtLong => "Long (TAG_Long)",
            NbtFloat => "Float (TAG_Float)",
            NbtDouble => "Double (TAG_Double)",
            NbtByteArray => "Byte Array (TAG_ByteArray)",
            NbtString => "String (TAG_String)",
            NbtList => "List (TAG_List)",
            NbtCompound => "Compound (TAG_Compound)",
            NbtIntArray => "Int Array (TAG_IntArray)",
            NbtLongArray => "Long Array (TAG_LongArray)",
            _ => "Unknown"
        };
    }

    private string FormatTagValue(NbtTag tag)
    {
        return tag switch
        {
            NbtByte b => b.Value.ToString(),
            NbtShort s => s.Value.ToString(),
            NbtInt i => i.Value.ToString(),
            NbtLong l => l.Value.ToString(),
            NbtFloat f => f.Value.ToString(),
            NbtDouble d => d.Value.ToString(),
            NbtString s => $"\"{s.Value}\"",
            NbtByteArray ba => $"[{ba.Value?.Length ?? 0} bytes]",
            NbtIntArray ia => $"[{ia.Value?.Length ?? 0} ints]",
            NbtLongArray la => $"[{la.Value?.Length ?? 0} longs]",
            NbtList list => list.Count > 0 ? $"List with {list.Count} elements" : "Empty list",
            NbtCompound compound => compound.Count > 0 ? $"Compound with {compound.Count} keys" : "Empty compound",
            _ => "(null)"
        };
    }

    private string GetTagPath(NbtTag tag)
    {
        // Find the path by searching the tree
        if (_currentNbtRoot == null || tag == _currentNbtRoot) return "/";

        var queue = new Queue<(NbtTag tag, string path)>();
        queue.Enqueue((_currentNbtRoot, "/"));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();

            if (current == tag) return path;

            if (current is NbtCompound compound)
            {
                foreach (var child in compound.Tags)
                {
                    queue.Enqueue((child, $"{path}{child.Name}/"));
                }
            }
            else if (current is NbtList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    queue.Enqueue((list.Get<NbtTag>(i), $"{path}[{i}]/"));
                }
            }
        }

        return "?";
    }

    private void BuildNbtTreeItem(TreeItem parentItem, NbtTag tag)
    {
        if (tag is NbtCompound compound)
        {
            foreach (var child in compound.Tags)
            {
                var item = _nbtTree.CreateItem(parentItem);
                item.SetText(0, child.Name);
                _nbtNodeMap[item] = child;

                if (child is NbtCompound childCompound && childCompound.Count > 0)
                {
                    BuildNbtTreeItem(item, childCompound);
                }
                else if (child is NbtList childList && childList.Count > 0)
                {
                    BuildListTreeItem(item, childList);
                }
            }
        }
        else if (tag is NbtList list)
        {
            BuildListTreeItem(parentItem, list);
        }
    }

    private void BuildListTreeItem(TreeItem parentItem, NbtList list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var element = list.Get<NbtTag>(i);
            var item = _nbtTree.CreateItem(parentItem);
            string typeName = element is NbtCompound ? "Compound" : GetTagTypeName(element);
            item.SetText(0, $"[{i}] {typeName}");
            _nbtNodeMap[item] = element;

            if (element is NbtCompound childCompound && childCompound.Count > 0)
            {
                BuildNbtTreeItem(item, childCompound);
            }
            else if (element is NbtList childList && childList.Count > 0)
            {
                BuildListTreeItem(item, childList);
            }
        }
    }

    private void OnSaveFilePressed()
    {
        if (_currentNbtRoot == null)
        {
            _editorOutput.AppendText("[color=red]No file loaded.[/color]\n");
            return;
        }

        if (_currentRegionFile != null)
        {
            if (_currentRegionChunkIndex is not int chunkIndex) return;
            try
            {
                _currentRegionFile.SaveChunk(chunkIndex, _currentNbtRoot);
                _dirtyRegionChunks.Remove(chunkIndex);
                PopulateRegionChunks(chunkIndex);
                _editorOutput.AppendText($"[color=green]Saved chunk {chunkIndex} to {Path.GetFileName(_currentFilePath)}.[/color]\n");
            }
            catch (Exception ex)
            {
                _editorOutput.AppendText($"[color=red]Chunk save error: {ex.Message}[/color]\n");
            }

            return;
        }

        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            SaveNbtToFile(_currentFilePath);
        }
    }

    private void SaveNbtToFile(string path)
    {
        if (_currentNbtRoot == null) return;

        try
        {
            var nbtFile = new NbtFile(_currentNbtRoot);
            nbtFile.SaveToFile(path, NbtCompression.GZip);
            _currentFilePath = path;
            _editorOutput.AppendText($"[color=green]Saved to: {path}[/color]\n");
        }
        catch (Exception ex)
        {
            _editorOutput.AppendText($"[color=red]Save error: {ex.Message}[/color]\n");
        }
    }

    private void EditSelectedTag()
    {
        if (_selectedNbtTag == null) return;

        _editValueEdit.Text = GetEditableValue(_selectedNbtTag);
        _editPopup.PopupCentered(new Vector2I(400, 180));
        _editValueEdit.GrabFocus();
        _editValueEdit.SelectAll();
    }

    private void ApplySelectedTagEdit()
    {
        if (ApplyEdit(_selectedNbtTag, _editValueEdit.Text))
        {
            _editPopup.Hide();
        }
    }

    private string GetEditableValue(NbtTag tag)
    {
        return tag switch
        {
            NbtByte b => b.Value.ToString(),
            NbtShort s => s.Value.ToString(),
            NbtInt i => i.Value.ToString(),
            NbtLong l => l.Value.ToString(),
            NbtFloat f => f.Value.ToString(),
            NbtDouble d => d.Value.ToString(),
            NbtString s => s.Value,
            _ => ""
        };
    }

    private bool ApplyEdit(NbtTag? tag, string newValue)
    {
        if (tag == null) return false;

        try
        {
            if (tag is NbtString strTag)
            {
                strTag.Value = newValue;
            }
            else if (tag is NbtByte byteTag)
            {
                byteTag.Value = (byte)byte.Parse(newValue);
            }
            else if (tag is NbtShort shortTag)
            {
                shortTag.Value = short.Parse(newValue);
            }
            else if (tag is NbtInt intTag)
            {
                intTag.Value = int.Parse(newValue);
            }
            else if (tag is NbtLong longTag)
            {
                longTag.Value = long.Parse(newValue);
            }
            else if (tag is NbtFloat floatTag)
            {
                floatTag.Value = float.Parse(newValue);
            }
            else if (tag is NbtDouble doubleTag)
            {
                doubleTag.Value = double.Parse(newValue);
            }

            // Refresh the tree display
            CancelElementSearch("Search canceled because the NBT data changed.");
            if (_currentRegionFile != null && _currentRegionChunkIndex is int chunkIndex)
                _dirtyRegionChunks.Add(chunkIndex);
            RefreshTreeDisplay();
            if (_selectedNbtTag != null) ShowTagDetails(_selectedNbtTag);
            _editorOutput.AppendText($"[color=green]Value updated.[/color]\n");
            return true;
        }
        catch (Exception ex)
        {
            _editorOutput.AppendText($"[color=red]Invalid value: {ex.Message}[/color]\n");
            return false;
        }
    }

    private void RefreshTreeDisplay()
    {
        if (_currentNbtRoot == null) return;
        if (_currentRegionFile != null && _currentRegionChunkIndex is int chunkIndex)
        {
            PopulateRegionChunks(chunkIndex);
            return;
        }

        _nbtNodeMap.Clear();
        _updatingNbtTree = true;
        try
        {
            _nbtTree.Clear();
            var rootItem = _nbtTree.CreateItem();
            BuildNbtTreeItem(rootItem, _currentNbtRoot);
            var selectedItem = _nbtNodeMap.FirstOrDefault(entry => ReferenceEquals(entry.Value, _selectedNbtTag)).Key;
            selectedItem?.Select(0);
        }
        finally
        {
            _updatingNbtTree = false;
        }
    }

    private void OnStopPressed()
    {
        _cts?.Cancel();
        _output.AppendText("\n[color=yellow]Stopping scan...[/color]\n");
        _stopButton.Disabled = true;
    }

    private void LoadSettings()
    {
        if (_config.Load(_settingsPath) == Error.Ok)
        {
            _pathEdit.Text = _config.GetValue("settings", "path", "").As<string>();
            _targetEdit.Text = _config.GetValue("settings", "target", "cobblemon:roseli_berry").As<string>();
            _xEdit.Text = _config.GetValue("settings", "ref_x", "-10").As<string>();
            _yEdit.Text = _config.GetValue("settings", "ref_y", "64").As<string>();
            _zEdit.Text = _config.GetValue("settings", "ref_z", "-105").As<string>();
            string saveDirectory = _config.GetValue("settings", "save_directory", "").As<string>();
            if (!string.IsNullOrEmpty(saveDirectory))
            {
                _globalDirEdit.Text = saveDirectory;
                ApplyGlobalDirectory(saveDirectory, false);
            }
            else
            {
                RefreshFileList();
            }
        }
        else
        {
            RefreshFileList();
        }
    }

    private void SaveSettings()
    {
        _config.SetValue("settings", "path", _pathEdit.Text);
        _config.SetValue("settings", "target", _targetEdit.Text);
        _config.SetValue("settings", "ref_x", _xEdit.Text);
        _config.SetValue("settings", "ref_y", _yEdit.Text);
        _config.SetValue("settings", "ref_z", _zEdit.Text);
        _config.SetValue("settings", "save_directory", _saveDirectory ?? "");
        _config.Save(_settingsPath);
    }

    private async void OnScanPressed()
    {
        _scanButton.Disabled = true;
        _stopButton.Disabled = false;
        _output.Clear();
        _progressBar.Value = 0;
        _closestMatch = null;
        _minDistSq = double.MaxValue;

        string path = _pathEdit.Text.Trim();
        _currentTarget = _targetEdit.Text.Trim();
        if (!_currentTarget.Contains(":")) _currentTarget = "minecraft:" + _currentTarget;

        if (!double.TryParse(_xEdit.Text, out _refX)) _refX = 0;
        if (!double.TryParse(_yEdit.Text, out _refY)) _refY = 0;
        if (!double.TryParse(_zEdit.Text, out _refZ)) _refZ = 0;

        SaveSettings();

        if (!Directory.Exists(path))
        {
            _output.AppendText($"[color=red]Error: Directory '{path}' not found.[/color]\n");
            ResetScanState();
            return;
        }

        string[] mcaFiles = Directory.GetFiles(path, "*.mca");
        if (mcaFiles.Length == 0)
        {
            _output.AppendText("[color=yellow]No .mca files found in the region directory.[/color]\n");
            ResetScanState();
            return;
        }

        _output.AppendText($"Searching for: [color=cyan]{_currentTarget}[/color]\n");

        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;

        int totalMatches = 0;
        int filesScanned = 0;
        try
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(mcaFiles, new ParallelOptions { CancellationToken = token }, (filePath) =>
                {
                    if (token.IsCancellationRequested) return;

                    string fileName = Path.GetFileName(filePath);

                    int current = Interlocked.Increment(ref filesScanned);
                    if (current % 10 == 0 || current == mcaFiles.Length)
                    {
                        CallDeferred(nameof(UpdateProgress), fileName, current, mcaFiles.Length);
                    }

                    try
                    {
                        int matches = ScanRegionFile(filePath, token);
                        Interlocked.Add(ref totalMatches, matches);
                    }
                    catch (Exception ex)
                    {
                        CallDeferred(nameof(LogMessage),
                            $"[color=red]Error reading {fileName}: {ex.Message}[/color]\n");
                    }
                });
            }, token);
        }
        catch (OperationCanceledException)
        {
            _output.AppendText("\n[color=yellow]Scan canceled.[/color]\n");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        _output.AppendText($"\nFinished. Matches (chunks found): {totalMatches}\n");
        if (_closestMatch != null)
        {
            _output.AppendText("\n[b][color=green]CLOSEST MATCH FOUND:[/color][/b]\n");
            _output.AppendText($"Coordinates: X={_closestMatch.X}, Y={_closestMatch.Y}, Z={_closestMatch.Z}\n");
            _output.AppendText($"Distance: {_closestMatch.Distance:F2} blocks\n");
            _output.AppendText($"Region: {_closestMatch.RegionFile}\n");
            _output.AppendText($"Block state: {_closestMatch.BlockState}\n");
        }
        else if (!token.IsCancellationRequested)
        {
            _output.AppendText("\nNo target blocks found.\n");
        }

        ResetScanState();
    }

    private void ResetScanState()
    {
        _scanButton.Disabled = false;
        _stopButton.Disabled = true;
    }

    private void UpdateProgress(string fileName, int current, int total)
    {
        float percent = (float)current / total * 100;
        _progressBar.Value = percent;
        if (current % 10 == 0 || current == total)
        {
            _output.AppendText($"Scanning {fileName} ({current}/{total})...\n");
        }
    }

    private void LogMessage(string msg) => _output.AppendText(msg);

    private int ScanRegionFile(string path, System.Threading.CancellationToken token)
    {
        string fileName = Path.GetFileName(path);

        // Region-level skip
        string[] parts = fileName.Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[1], out int rx) && int.TryParse(parts[2], out int rz))
        {
            if (_closestMatch != null)
            {
                int rbx = rx * 512;
                int rbz = rz * 512;
                double dx = Math.Max(0, Math.Max(rbx - _refX, _refX - (rbx + 512)));
                double dz = Math.Max(0, Math.Max(rbz - _refZ, _refZ - (rbz + 512)));
                if (dx * dx + dz * dz >= _minDistSq) return 0;
            }
        }

        int matches = 0;
        using var fs = File.OpenRead(path);

        byte[] header = new byte[4096];
        if (FullRead(fs, header, 4096) < 4096) return 0;

        var chunksToScan = new List<(int index, int sector)>();
        for (int i = 0; i < 1024; i++)
        {
            int offset = (header[i * 4] << 16) | (header[i * 4 + 1] << 8) | header[i * 4 + 2];
            byte sectorCount = header[i * 4 + 3];

            if (offset != 0 && sectorCount != 0)
            {
                chunksToScan.Add((i, offset));
            }
        }

        foreach (var (index, sector) in chunksToScan)
        {
            if (token.IsCancellationRequested) break;

            try
            {
                fs.Seek(sector * 4096L, SeekOrigin.Begin);
                byte[] lengthBytes = new byte[4];
                if (FullRead(fs, lengthBytes, 4) < 4) continue;

                if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
                int length = BitConverter.ToInt32(lengthBytes, 0);

                byte compressionType = (byte)fs.ReadByte();
                byte[] compressedData = new byte[length - 1];
                if (FullRead(fs, compressedData, length - 1) < length - 1) continue;

                using var ms = new MemoryStream(compressedData);
                Stream decompressor = compressionType switch
                {
                    1 => new GZipStream(ms, CompressionMode.Decompress),
                    2 => new ZLibStream(ms, CompressionMode.Decompress),
                    _ => ms
                };

                var nbtFile = new NbtFile();
                nbtFile.LoadFromStream(decompressor, NbtCompression.None);
                if (ScanChunk(nbtFile.RootTag, fileName, index, token))
                {
                    matches++;
                }
            }
            catch
            {
            }
        }

        return matches;
    }

    private int FullRead(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    private bool ScanChunk(NbtCompound chunk, string regionFile, int index, System.Threading.CancellationToken token)
    {
        if (!chunk.Contains("sections")) return false;
        var sections = chunk.Get<NbtList>("sections");
        if (sections == null) return false;

        string[] parts = regionFile.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[1], out int rx) || !int.TryParse(parts[2], out int rz))
            return false;

        int cxInReg = index % 32;
        int czInReg = index / 32;
        int cx = rx * 32 + cxInReg;
        int cz = rz * 32 + czInReg;
        int chunkBaseX = cx * 16;
        int chunkBaseZ = cz * 16;

        bool foundAny = false;

        foreach (NbtCompound section in sections)
        {
            if (token.IsCancellationRequested) break;

            if (!section.Contains("block_states")) continue;
            var blockStates = section.Get<NbtCompound>("block_states");
            if (blockStates == null || !blockStates.Contains("palette")) continue;

            var palette = blockStates.Get<NbtList>("palette");
            if (palette == null) continue;

            var targetIndices = new HashSet<int>();
            for (int i = 0; i < palette.Count; i++)
            {
                var block = palette.Get<NbtCompound>(i);
                if (block.Get<NbtString>("Name")?.Value == _currentTarget)
                {
                    targetIndices.Add(i);
                }
            }

            if (targetIndices.Count == 0) continue;

            sbyte sectionY = (sbyte)(section.Get<NbtByte>("Y")?.Value ?? 0);
            int baseY = sectionY * 16;

            // Bounding box optimization
            if (_closestMatch != null)
            {
                if (GetMinDistanceSqToBox(_refX, _refY, _refZ, chunkBaseX, baseY, chunkBaseZ, 16) >= _minDistSq)
                    continue;
            }

            if (blockStates.Contains("data"))
            {
                long[]? data = blockStates.Get<NbtLongArray>("data")?.Value;
                if (data != null)
                {
                    int paletteSize = palette.Count;
                    int bitsPerBlock = Math.Max(4, (int)Math.Ceiling(Math.Log2(paletteSize)));
                    int blocksPerLong = 64 / bitsPerBlock;
                    long mask = (1L << bitsPerBlock) - 1;

                    int blockCount = 0;
                    foreach (long longVal in data)
                    {
                        ulong unsignedLong = (ulong)longVal;
                        for (int b = 0; b < blocksPerLong; b++)
                        {
                            if (blockCount >= 4096) break;

                            int paletteIndex = (int)(unsignedLong & (ulong)mask);
                            if (targetIndices.Contains(paletteIndex))
                            {
                                int lx = blockCount % 16;
                                int lz = (blockCount / 16) % 16;
                                int ly = blockCount / 256;

                                int gx = chunkBaseX + lx;
                                int gy = baseY + ly;
                                int gz = chunkBaseZ + lz;

                                UpdateClosestInternal(gx, gy, gz, palette.Get<NbtCompound>(paletteIndex), regionFile);
                                foundAny = true;
                            }

                            unsignedLong >>= bitsPerBlock;
                            blockCount++;
                        }
                    }
                }
            }
            else if (palette.Count == 1)
            {
                int gx = chunkBaseX + 8;
                int gy = baseY + 8;
                int gz = chunkBaseZ + 8;
                UpdateClosestInternal(gx, gy, gz, palette.Get<NbtCompound>(0), regionFile);
                foundAny = true;
            }
        }

        return foundAny;
    }

    private void UpdateClosestInternal(int x, int y, int z, NbtCompound blockTag, string regionFile)
    {
        double dx = x - _refX;
        double dy = y - _refY;
        double dz = z - _refZ;
        double distSq = dx * dx + dy * dy + dz * dz;

        lock (_lock)
        {
            if (_closestMatch == null || distSq < _minDistSq)
            {
                _minDistSq = distSq;
                double dist = Math.Sqrt(distSq);
                _closestMatch = new ClosestMatch
                {
                    X = x,
                    Y = y,
                    Z = z,
                    Distance = dist,
                    BlockState = blockTag.ToString(),
                    RegionFile = regionFile
                };

                CallDeferred(nameof(LogMessage),
                    $"[color=green]FOUND: {_currentTarget} at X={x}, Y={y}, Z={z} (Dist: {dist:F2})[/color]\n");
            }
        }
    }

    private static double GetMinDistanceSqToBox(double px, double py, double pz, int xMin, int yMin, int zMin, int size)
    {
        int xMax = xMin + size;
        int yMax = yMin + size;
        int zMax = zMin + size;

        double dx = Math.Max(0, Math.Max(xMin - px, px - xMax));
        double dy = Math.Max(0, Math.Max(yMin - py, py - yMax));
        double dz = Math.Max(0, Math.Max(zMin - pz, pz - zMax));

        return dx * dx + dy * dy + dz * dz;
    }
}