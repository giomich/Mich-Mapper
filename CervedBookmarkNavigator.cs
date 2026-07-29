using System.Collections;
using System.Reflection;
using UglyToad.PdfPig;

namespace MichMapper;

internal sealed class CervedBookmarkNavigator
{
    public IReadOnlyList<BookmarkSection> ReadSections(
        PdfDocument document,
        int pageCount)
    {
        if (!document.TryGetBookmarks(out var bookmarks, allowContainerNode: true) ||
            bookmarks is null)
            return [];

        var raw = new List<RawBookmark>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Traverse(
            bookmarks,
            parentPath: "",
            level: 0,
            pageCount,
            raw,
            visited);

        var ordered = raw
            .Where(x => !string.IsNullOrWhiteSpace(x.Title) && x.StartPage > 0)
            .OrderBy(x => x.StartPage)
            .ThenBy(x => x.Level)
            .ToArray();

        var result = new List<BookmarkSection>();

        for (int i = 0; i < ordered.Length; i++)
        {
            RawBookmark current = ordered[i];

            int nextPage = ordered
                .Skip(i + 1)
                .Where(x => x.StartPage > current.StartPage)
                .Select(x => x.StartPage)
                .FirstOrDefault();

            int endPage = nextPage > 0
                ? Math.Max(current.StartPage, nextPage - 1)
                : pageCount;

            result.Add(new BookmarkSection(
                current.Title,
                current.StartPage,
                Math.Min(endPage, pageCount),
                current.Level,
                current.Path,
                current.Method));
        }

        return result
            .GroupBy(x => $"{x.Path}|{x.StartPage}|{x.EndPage}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public IReadOnlyList<PageText> PagesForSection(
        CervedRecord record,
        params string[] aliases)
    {
        BookmarkSection? section = FindBestSection(record.BookmarkSections, aliases);

        if (section is null)
            return [];

        return record.Pages
            .Where(page => section.ContainsPage(page.Number))
            .ToArray();
    }

    public BookmarkSection? FindBestSection(
        IReadOnlyList<BookmarkSection> sections,
        params string[] aliases)
    {
        if (sections.Count == 0)
            return null;

        var exact = sections.FirstOrDefault(section =>
            aliases.Any(alias =>
                Normalize(section.Title) == Normalize(alias)));

        if (exact is not null)
            return exact;

        var contains = sections.FirstOrDefault(section =>
            aliases.Any(alias =>
                Normalize(section.Title).Contains(Normalize(alias),
                    StringComparison.Ordinal) ||
                Normalize(alias).Contains(Normalize(section.Title),
                    StringComparison.Ordinal)));

        if (contains is not null)
            return contains;

        return sections.FirstOrDefault(section =>
            aliases.Any(alias =>
                Normalize(section.Path).Contains(Normalize(alias),
                    StringComparison.Ordinal)));
    }

    private static void Traverse(
        object? node,
        string parentPath,
        int level,
        int pageCount,
        List<RawBookmark> output,
        HashSet<object> visited)
    {
        if (node is null || level > 20 || !visited.Add(node))
            return;

        Type type = node.GetType();

        string title =
            ReadString(type, node, "Title") ??
            ReadString(type, node, "Text") ??
            ReadString(type, node, "Name") ??
            "";

        int page = ReadPageNumber(node, pageCount);
        string path = string.IsNullOrWhiteSpace(title)
            ? parentPath
            : string.IsNullOrWhiteSpace(parentPath)
                ? title
                : $"{parentPath} > {title}";

        if (!string.IsNullOrWhiteSpace(title) && page > 0)
        {
            output.Add(new RawBookmark(
                title.Trim(),
                page,
                level,
                path,
                "Segnalibro PDF"));
        }

        foreach (object child in ReadChildren(node))
        {
            Traverse(
                child,
                path,
                string.IsNullOrWhiteSpace(title) ? level : level + 1,
                pageCount,
                output,
                visited);
        }
    }

    private static int ReadPageNumber(object node, int pageCount)
    {
        object? destination =
            ReadObject(node, "Destination") ??
            ReadObject(node, "BookmarkDestination") ??
            ReadObject(node, "Action");

        int page =
            ReadInt(destination, "PageNumber") ??
            ReadInt(destination, "Page") ??
            ReadInt(destination, "PageIndex") ??
            ReadInt(node, "PageNumber") ??
            ReadInt(node, "Page") ??
            ReadInt(node, "PageIndex") ??
            0;

        if (page == 0)
            return 0;

        if (page >= 1 && page <= pageCount)
            return page;

        if (page >= 0 && page < pageCount)
            return page + 1;

        return 0;
    }

    private static IEnumerable<object> ReadChildren(object node)
    {
        string[] propertyNames =
        [
            "Children",
            "Nodes",
            "Bookmarks",
            "Roots",
            "Items"
        ];

        foreach (string propertyName in propertyNames)
        {
            object? value = ReadObject(node, propertyName);

            if (value is null || value is string)
                continue;

            if (value is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    if (item is not null)
                        yield return item;
                }
            }
        }
    }

    private static string? ReadString(Type type, object instance, string name)
    {
        PropertyInfo? property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public);

        return property?.GetValue(instance)?.ToString();
    }

    private static object? ReadObject(object? instance, string name)
    {
        if (instance is null)
            return null;

        PropertyInfo? property = instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public);

        try
        {
            return property?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(object? instance, string name)
    {
        object? value = ReadObject(instance, name);

        if (value is null)
            return null;

        if (value is int intValue)
            return intValue;

        return int.TryParse(value.ToString(), out int parsed)
            ? parsed
            : null;
    }

    private static string Normalize(string value) =>
        new(value
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private sealed record RawBookmark(
        string Title,
        int StartPage,
        int Level,
        string Path,
        string Method);

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
