using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using fNbt;

namespace MineFinder;

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
    private LineEdit _pathEdit = null!;
    private LineEdit _targetEdit = null!;
    private LineEdit _xEdit = null!, _yEdit = null!, _zEdit = null!;
    private Button _scanButton = null!;
    private Button _stopButton = null!;
    private ProgressBar _progressBar = null!;
    private RichTextLabel _output = null!;
    private FileDialog _fileDialog = null!;
    
    private string _settingsPath = "user://settings.cfg";
    private ConfigFile _config = new();

    private ClosestMatch? _closestMatch;
    private string _currentTarget = "";
    private double _refX, _refY, _refZ;
    private System.Threading.CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private double _minDistSq = double.MaxValue;

    public override void _Ready()
    {
        // Setup UI
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        vbox.AddChild(new Label { Text = "Region Directory Path:" });
        var pathHbox = new HBoxContainer();
        _pathEdit = new LineEdit { PlaceholderText = "C:/Users/.../AppData/Roaming/.minecraft/saves/WorldName/region", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var browseButton = new Button { Text = "Browse..." };
        browseButton.Pressed += () => _fileDialog.PopupCentered();
        pathHbox.AddChild(_pathEdit);
        pathHbox.AddChild(browseButton);
        vbox.AddChild(pathHbox);

        vbox.AddChild(new Label { Text = "Target Block:" });
        _targetEdit = new LineEdit { Text = "cobblemon:roseli_berry" };
        vbox.AddChild(_targetEdit);

        vbox.AddChild(new Label { Text = "Reference Coordinates (X, Y, Z):" });
        var coordHbox = new HBoxContainer();
        _xEdit = new LineEdit { Text = "-10", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _yEdit = new LineEdit { Text = "64", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _zEdit = new LineEdit { Text = "-105", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        coordHbox.AddChild(_xEdit);
        coordHbox.AddChild(_yEdit);
        coordHbox.AddChild(_zEdit);
        vbox.AddChild(coordHbox);

        var buttonsHbox = new HBoxContainer();
        _scanButton = new Button { Text = "Start Scan", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scanButton.Pressed += OnScanPressed;
        _stopButton = new Button { Text = "Stop", SizeFlagsHorizontal = SizeFlags.ExpandFill, Disabled = true };
        _stopButton.Pressed += OnStopPressed;
        buttonsHbox.AddChild(_scanButton);
        buttonsHbox.AddChild(_stopButton);
        vbox.AddChild(buttonsHbox);

        _progressBar = new ProgressBar { 
            MinValue = 0, 
            MaxValue = 100, 
            Value = 0, 
            CustomMinimumSize = new Vector2(0, 20) 
        };
        vbox.AddChild(_progressBar);

        _output = new RichTextLabel { 
            SizeFlagsVertical = SizeFlags.ExpandFill, 
            ScrollFollowing = true,
            BbcodeEnabled = true
        };
        vbox.AddChild(_output);

        _fileDialog = new FileDialog {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Select Region Directory",
            Size = new Vector2I(600, 400)
        };
        _fileDialog.DirSelected += (path) => _pathEdit.Text = path;
        AddChild(_fileDialog);

        LoadSettings();
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
        }
    }

    private void SaveSettings()
    {
        _config.SetValue("settings", "path", _pathEdit.Text);
        _config.SetValue("settings", "target", _targetEdit.Text);
        _config.SetValue("settings", "ref_x", _xEdit.Text);
        _config.SetValue("settings", "ref_y", _yEdit.Text);
        _config.SetValue("settings", "ref_z", _zEdit.Text);
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
                        CallDeferred(nameof(LogMessage), $"[color=red]Error reading {fileName}: {ex.Message}[/color]\n");
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
            catch { }
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
                
                CallDeferred(nameof(LogMessage), $"[color=green]FOUND: {_currentTarget} at X={x}, Y={y}, Z={z} (Dist: {dist:F2})[/color]\n");
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
