using System.Buffers.Binary;
using System.IO.Compression;
using fNbt;
using MinecraftToolbox;

var testDirectory = Path.Combine(AppContext.BaseDirectory, "region-test-data");
Directory.CreateDirectory(testDirectory);
try
{
    TestNbtSearch();
    TestCompressionAndChunkEditing();
    TestMalformedHeaderAndRecord();
    TestExternalChangeRejection();
    Console.WriteLine("All region file checks passed.");
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}

void TestCompressionAndChunkEditing()
{
    string path = Path.Combine(testDirectory, "r.0.0.mca");
    WriteRegion(path, new[]
    {
        (0, (byte)1, "gzip-original"),
        (1, (byte)2, "zlib-original"),
        (2, (byte)3, "raw-original")
    });
    byte[] before = File.ReadAllBytes(path);

    var region = new RegionFile(path);
    Assert(region.GetChunkIndices().SequenceEqual(new[] { 0, 1, 2 }), "Chunk indices were not read correctly.");
    Assert(ReadName(region.ReadChunk(0)) == "gzip-original", "Gzip chunk did not load.");
    Assert(ReadName(region.ReadChunk(1)) == "zlib-original", "Zlib chunk did not load.");
    Assert(ReadName(region.ReadChunk(2)) == "raw-original", "Uncompressed chunk did not load.");

    var edited = region.ReadChunk(0);
    edited.Get<NbtString>("name")!.Value = "gzip-updated";
    region.SaveChunk(0, edited);

    var reopened = new RegionFile(path);
    Assert(ReadName(reopened.ReadChunk(0)) == "gzip-updated", "Edited chunk was not saved and reopened.");
    Assert(ReadName(reopened.ReadChunk(1)) == "zlib-original", "Saving changed an untouched chunk.");
    Assert(ReadName(reopened.ReadChunk(2)) == "raw-original", "Saving changed an untouched chunk.");
    byte[] after = File.ReadAllBytes(path);
    Assert(before.AsSpan(3 * 4096, 2 * 4096).SequenceEqual(after.AsSpan(3 * 4096, 2 * 4096)),
        "Untouched chunk sectors were not byte-for-byte preserved.");
}

void TestNbtSearch()
{
    var root = new NbtCompound("root");
    root.Add(new NbtString("firstHit", "first value"));
    var nested = new NbtCompound("details");
    nested.Add(new NbtString("DisplayName", "Roseli Berry"));
    nested.Add(new NbtInt("count", 42));
    nested.Add(new NbtIntArray("codes", new[] { 9081726, 456 }));
    nested.Add(new NbtString("insideHit", "needle value"));
    var entries = new NbtList("entries", NbtTagType.Compound);
    var entry = new NbtCompound((string?)null);
    entry.Add(new NbtString("listValue", "Inside list"));
    entries.Add(entry);
    nested.Add(entries);
    root.Add(nested);
    root.Add(new NbtString("lastHit", "omega"));

    var keyMatches = NbtSearch.FindMatches(root, "displayname");
    Assert(keyMatches.Count == 1 && keyMatches[0].Name == "DisplayName",
        "Nested keys were not searched case-insensitively.");
    Assert(NbtSearch.FindMatches(root, "berry").Single().Name == "DisplayName",
        "String values were not searched case-insensitively.");
    Assert(NbtSearch.FindMatches(root, "817").Single().Name == "codes",
        "Array values were not searched.");
    Assert(NbtSearch.FindMatches(root, "inside list").Single().Name == "listValue",
        "Nested list values were not searched.");

    var orderedMatches = NbtSearch.FindMatches(root, "hit");
    Assert(orderedMatches.Select(tag => tag.Name).SequenceEqual(new[] { "firstHit", "insideHit", "lastHit" }),
        "Search matches were not returned in tree traversal order.");
    Assert(ReferenceEquals(NbtSearch.FindNextMatch(root, "hit", nested), orderedMatches[1]),
        "Search did not continue after a selected nonmatching node.");
    Assert(ReferenceEquals(NbtSearch.FindNextMatch(root, "hit", orderedMatches[^1]), orderedMatches[0]),
        "Search did not wrap to the first match.");
    Assert(NbtSearch.FindNextMatch(root, "hit", orderedMatches[^1], wrap: false) == null,
        "Non-wrapping search unexpectedly returned an earlier match.");
}

void TestMalformedHeaderAndRecord()
{
    string shortPath = Path.Combine(testDirectory, "short.mca");
    File.WriteAllBytes(shortPath, new byte[4096]);
    AssertThrows<InvalidDataException>(() => new RegionFile(shortPath), "Malformed header was accepted.");

    string badRecordPath = Path.Combine(testDirectory, "bad-record.mca");
    var bytes = new byte[3 * 4096];
    bytes[2] = 2;
    bytes[3] = 1;
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2 * 4096, 4), int.MaxValue);
    File.WriteAllBytes(badRecordPath, bytes);
    AssertThrows<InvalidDataException>(() => new RegionFile(badRecordPath).ReadChunk(0),
        "Malformed chunk record was accepted.");
}

void TestExternalChangeRejection()
{
    string path = Path.Combine(testDirectory, "external-change.mca");
    WriteRegion(path, new[] { (0, (byte)2, "initial") });
    var region = new RegionFile(path);
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
    {
        stream.Position = 4096;
        stream.WriteByte(1);
    }

    var edited = region.ReadChunk(0);
    edited.Get<NbtString>("name")!.Value = "should-not-save";
    AssertThrows<IOException>(() => region.SaveChunk(0, edited),
        "Save did not reject a region file changed after it was opened.");
}

void WriteRegion(string path, IEnumerable<(int Index, byte Compression, string Name)> chunks)
{
    var entries = chunks.Select(chunk => (chunk.Index, chunk.Compression, Data: EncodeChunk(chunk.Name, chunk.Compression))).ToArray();
    int totalSectors = 2 + entries.Sum(entry => (entry.Data.Length + 4 + 1 + 4095) / 4096);
    var bytes = new byte[totalSectors * 4096];
    int nextSector = 2;
    foreach (var entry in entries)
    {
        int sectorCount = (entry.Data.Length + 4 + 1 + 4095) / 4096;
        int locationOffset = entry.Index * 4;
        bytes[locationOffset] = (byte)(nextSector >> 16);
        bytes[locationOffset + 1] = (byte)(nextSector >> 8);
        bytes[locationOffset + 2] = (byte)nextSector;
        bytes[locationOffset + 3] = (byte)sectorCount;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4096 + entry.Index * 4, 4), 1);

        int recordOffset = nextSector * 4096;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(recordOffset, 4), entry.Data.Length + 1);
        bytes[recordOffset + 4] = entry.Compression;
        entry.Data.CopyTo(bytes, recordOffset + 5);
        nextSector += sectorCount;
    }

    File.WriteAllBytes(path, bytes);
}

byte[] EncodeChunk(string name, byte compression)
{
    var root = new NbtCompound("chunk");
    root.Add(new NbtString("name", name));
    using var nbtStream = new MemoryStream();
    new NbtFile(root).SaveToStream(nbtStream, NbtCompression.None);
    byte[] raw = nbtStream.ToArray();
    if (compression == 3) return raw;

    using var compressed = new MemoryStream();
    using (Stream compressor = compression == 1
               ? new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)
               : new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        compressor.Write(raw);
    return compressed.ToArray();
}

string ReadName(NbtCompound root) => root.Get<NbtString>("name")!.Value;

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}