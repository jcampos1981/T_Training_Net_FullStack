namespace HelpDesk.Api.Paging;

// NextAfter: el valor que el cliente manda como ?after= para pedir la siguiente página (null = no hay más).
public sealed record CursorPage<T>(IReadOnlyList<T> Items, int? NextAfter);

public static class CursorPage
{
    // "fetched" trae hasta PageSize + 1 elementos (ver PageRequest.Take): el sobrante solo indica que hay más.
    public static CursorPage<TResult> Create<TSource, TResult>(
        IReadOnlyList<TSource> fetched,
        PageRequest page,
        Func<TSource, TResult> map,
        Func<TSource, int> cursorOf)
    {
        bool hasMore = fetched.Count > page.PageSize;
        List<TSource> pageItems = [.. fetched.Take(page.PageSize)];

        return new CursorPage<TResult>(
            [.. pageItems.Select(map)],
            hasMore ? cursorOf(pageItems[^1]) : null);
    }
}
