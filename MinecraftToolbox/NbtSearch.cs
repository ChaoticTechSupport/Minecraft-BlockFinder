using System.Globalization;
using System.Collections;
using fNbt;

namespace MinecraftToolbox;

public static class NbtSearch
{
    public static IReadOnlyList<NbtTag> FindMatches(NbtCompound root, string query, bool includeRoot = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<NbtTag>();

        var matches = new List<NbtTag>();
        foreach (var tag in EnumerateTags(root, includeRoot))
        {
            if (Matches(tag, query)) matches.Add(tag);
        }

        return matches;
    }

    public static NbtTag? FindNextMatch(NbtCompound root, string query, NbtTag? selectedTag,
        bool includeRoot = true, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(query)) return null;

        var tags = EnumerateTags(root, includeRoot).ToList();
        if (tags.Count == 0) return null;

        int selectedIndex = selectedTag == null
            ? -1
            : tags.FindIndex(tag => ReferenceEquals(tag, selectedTag));
        int startIndex = selectedIndex < 0 ? 0 : selectedIndex + 1;

        for (int offset = 0; offset < tags.Count; offset++)
        {
            int index = startIndex + offset;
            if (index >= tags.Count)
            {
                if (!wrap) break;
                index %= tags.Count;
            }

            if (Matches(tags[index], query)) return tags[index];
        }

        return null;
    }

    private static IEnumerable<NbtTag> EnumerateTags(NbtTag tag, bool includeTag = true)
    {
        if (includeTag) yield return tag;

        if (tag is NbtCompound compound)
        {
            foreach (var child in compound.Tags)
            {
                foreach (var descendant in EnumerateTags(child)) yield return descendant;
            }
        }
        else if (tag is NbtList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                foreach (var descendant in EnumerateTags(list.Get<NbtTag>(i))) yield return descendant;
            }
        }
    }

    private static bool Matches(NbtTag tag, string query)
    {
        if (tag.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return true;
        return GetValues(tag).Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetValues(NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtShort value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtInt value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtLong value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtFloat value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtDouble value:
                yield return value.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case NbtString value:
                yield return value.Value;
                break;
            case NbtByteArray value:
                foreach (var item in ArrayValues(value.Value)) yield return item;
                break;
            case NbtIntArray value:
                foreach (var item in ArrayValues(value.Value)) yield return item;
                break;
            case NbtLongArray value:
                foreach (var item in ArrayValues(value.Value)) yield return item;
                break;
        }
    }

    private static IEnumerable<string> ArrayValues(IEnumerable? values)
    {
        if (values == null) yield break;
        foreach (var value in values)
        {
            if (value != null) yield return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}