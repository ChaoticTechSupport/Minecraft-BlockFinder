using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using fNbt;

namespace MinecraftToolbox;

public sealed class RegionFile
{
    private const int SectorSize = 4096;
    private const int HeaderSize = SectorSize * 2;
    private const int ChunkCount = 1024;
    private const int LocationTableOffset = 0;
    private const int TimestampTableOffset = SectorSize;

    private readonly object _writeLock = new();
    private readonly string _path;
    private byte[] _header;
    private byte[] _fingerprint;

    public RegionFile(string path)
    {
        _path = Path.GetFullPath(path);
        _header = ReadHeader(_path);
        _fingerprint = ComputeFingerprint(_path);
    }

    public IReadOnlyList<int> GetChunkIndices()
    {
        var indices = new List<int>();
        var ranges = new List<(int Start, int End)>();
        long fileLength = new FileInfo(_path).Length;
        for (int index = 0; index < ChunkCount; index++)
        {
            var location = GetLocation(index);
            if (location.Offset == 0 && location.SectorCount == 0) continue;
            ValidateLocation(location, fileLength);
            ranges.Add((location.Offset, location.Offset + location.SectorCount));
            indices.Add(index);
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (int i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End)
                throw new InvalidDataException("Region file contains overlapping chunk sectors.");
        }

        return indices;
    }

    public NbtCompound ReadChunk(int index)
    {
        ValidateChunkIndex(index);
        var location = GetLocation(index);
        if (location.Offset == 0 && location.SectorCount == 0)
            throw new InvalidDataException($"Chunk {index} is not present in the region file.");

        ValidateLocation(location, new FileInfo(_path).Length);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = (long)location.Offset * SectorSize;

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        stream.ReadExactly(lengthBytes);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        int maximumLength = location.SectorCount * SectorSize - sizeof(int);
        if (length < 1 || length > maximumLength)
            throw new InvalidDataException($"Chunk {index} has an invalid record length.");

        int compressionMarker = stream.ReadByte();
        if (compressionMarker < 0)
            throw new InvalidDataException($"Chunk {index} is missing its compression marker.");
        if ((compressionMarker & 0x80) != 0)
            throw new NotSupportedException($"Chunk {index} uses an external .mcc chunk stream, which is not supported.");

        byte[] compressedData = new byte[length - 1];
        stream.ReadExactly(compressedData);
        using var input = new MemoryStream(compressedData, writable: false);
        using Stream decompressor = (byte)compressionMarker switch
        {
            1 => new GZipStream(input, CompressionMode.Decompress),
            2 => new ZLibStream(input, CompressionMode.Decompress),
            3 => input,
            _ => throw new NotSupportedException(
                $"Chunk {index} uses unsupported compression type {compressionMarker}.")
        };

        var nbtFile = new NbtFile();
        nbtFile.LoadFromStream(decompressor, NbtCompression.None);
        return nbtFile.RootTag;
    }

    public void SaveChunk(int index, NbtCompound root)
    {
        ValidateChunkIndex(index);
        ArgumentNullException.ThrowIfNull(root);

        lock (_writeLock)
        {
            EnsureUnchanged();
            GetChunkIndices();

            using var nbtStream = new MemoryStream();
            new NbtFile(root).SaveToStream(nbtStream, NbtCompression.None);
            using var compressedStream = new MemoryStream();
            using (var compressor = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
                compressor.Write(nbtStream.ToArray());
            byte[] compressedBytes = compressedStream.ToArray();
            byte[] record = new byte[sizeof(int) + 1 + compressedBytes.Length];
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(0, sizeof(int)), compressedBytes.Length + 1);
            record[sizeof(int)] = 2;
            compressedBytes.CopyTo(record, sizeof(int) + 1);

            int sectorCount = (record.Length + SectorSize - 1) / SectorSize;
            if (sectorCount is < 1 or > byte.MaxValue)
                throw new InvalidDataException("The edited chunk is too large to store in a region file.");

            string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(_path, tempPath);
                EnsureUnchanged();

                var location = GetLocation(index);
                int startSector;
                using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (stream.Length < HeaderSize || stream.Length % SectorSize != 0)
                        throw new InvalidDataException("The region file has an invalid sector layout.");

                    if (location.Offset >= 2 && location.SectorCount >= sectorCount)
                    {
                        startSector = location.Offset;
                    }
                    else
                    {
                        long nextSector = stream.Length / SectorSize;
                        if (nextSector > 0xFFFFFF)
                            throw new InvalidDataException("The region file has no remaining addressable sectors.");
                        startSector = (int)nextSector;
                        stream.Position = stream.Length;
                    }

                    stream.Position = (long)startSector * SectorSize;
                    stream.Write(record);
                    int padding = sectorCount * SectorSize - record.Length;
                    if (padding > 0) stream.Write(new byte[padding]);

                    Span<byte> locationBytes = stackalloc byte[4];
                    locationBytes[0] = (byte)(startSector >> 16);
                    locationBytes[1] = (byte)(startSector >> 8);
                    locationBytes[2] = (byte)startSector;
                    locationBytes[3] = (byte)sectorCount;
                    stream.Position = LocationTableOffset + index * 4L;
                    stream.Write(locationBytes);

                    Span<byte> timestamp = stackalloc byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32BigEndian(timestamp, unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    stream.Position = TimestampTableOffset + index * 4L;
                    stream.Write(timestamp);
                    stream.Flush(flushToDisk: true);
                }

                EnsureUnchanged();
                File.Move(tempPath, _path, overwrite: true);
                _header = ReadHeader(_path);
                _fingerprint = ComputeFingerprint(_path);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    private static byte[] ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < HeaderSize || stream.Length % SectorSize != 0)
            throw new InvalidDataException("The file is not a valid Minecraft region file: invalid header or sector alignment.");

        var header = new byte[HeaderSize];
        stream.ReadExactly(header);
        return header;
    }

    private static byte[] ComputeFingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private (int Offset, int SectorCount) GetLocation(int index)
    {
        int position = LocationTableOffset + index * 4;
        int offset = (_header[position] << 16) | (_header[position + 1] << 8) | _header[position + 2];
        return (offset, _header[position + 3]);
    }

    private static void ValidateLocation((int Offset, int SectorCount) location, long fileLength)
    {
        if (location.Offset == 0 || location.SectorCount == 0 || location.Offset < 2)
            throw new InvalidDataException("Region file contains an incomplete or invalid chunk location.");

        long end = ((long)location.Offset + location.SectorCount) * SectorSize;
        if (end > fileLength)
            throw new InvalidDataException("Region file contains a chunk location outside the file.");
    }

    private void EnsureUnchanged()
    {
        byte[] currentFingerprint = ComputeFingerprint(_path);
        if (!CryptographicOperations.FixedTimeEquals(_fingerprint, currentFingerprint))
            throw new IOException("The region file changed on disk since it was opened. Reload it before saving.");
    }

    private static void ValidateChunkIndex(int index)
    {
        if ((uint)index >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));
    }
}