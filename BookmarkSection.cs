namespace MichMapper;

internal sealed record BookmarkSection(
    string Title,
    int StartPage,
    int EndPage,
    int Level,
    string Path,
    string NavigationMethod)
{
    public bool ContainsPage(int pageNumber) =>
        pageNumber >= StartPage && pageNumber <= EndPage;
}
