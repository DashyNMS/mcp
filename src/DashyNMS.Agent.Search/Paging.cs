namespace DashyNMS.Agent;

/// <summary>Where a page sits in a longer list, returned with every paged result as <c>page</c>.</summary>
/// <param name="NextOffset">The offset for the next page, or null when this is the last one.</param>
public sealed record PageInfo(int Offset, int Limit, int Returned, int Total, int? NextOffset);

/// <summary>
/// Offset-and-limit paging for list tools (#12), the same shape for every
/// one, so a model that has paged one list can page them all.
/// </summary>
/// <remarks>
/// Without it a broad search returned its best few and a count of the rest,
/// and the only way on was many narrower searches - slow, and in testing
/// enough calls for the client to give up. The byte cap still applies on
/// top: a page that's too big is cut and says so, like any result.
/// </remarks>
public static class Paging
{
    /// <summary>Checks the arguments, so a bad offset or limit is a plain error rather than an empty page.</summary>
    public static void Check(int offset, int limit, int maxLimit)
    {
        if (offset < 0)
        {
            throw new ToolException("Offset can't be negative. Start at 0, then use the next_offset each page gives.");
        }

        if (limit < 1 || limit > maxLimit)
        {
            throw new ToolException($"Limit must be between 1 and {maxLimit}.");
        }
    }

    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> all, int offset, int limit, out PageInfo page)
    {
        ArgumentNullException.ThrowIfNull(all);
        var items = all.Skip(offset).Take(limit).ToList();
        var next = offset + items.Count;
        page = new PageInfo(offset, limit, items.Count, all.Count, next < all.Count ? next : null);
        return items;
    }

    /// <summary>
    /// "Showing 11–20 of 38 devices; pass offset 20 for the next page." The
    /// one place a summary mentions paging, so every tool says it the same way.
    /// </summary>
    public static string Describe(PageInfo page, string singular, string plural)
    {
        ArgumentNullException.ThrowIfNull(page);
        var noun = page.Total == 1 ? singular : plural;

        if (page.Total == 0)
        {
            return $"No {plural}.";
        }

        if (page.Returned == 0)
        {
            return $"There {(page.Total == 1 ? "is" : "are")} {page.Total} {noun}, none at offset {page.Offset}. Start again at offset 0.";
        }

        if (page.Offset == 0 && page.NextOffset is null)
        {
            return page.Total == 1 ? $"1 {noun}." : $"All {page.Total} {noun}.";
        }

        var range = $"Showing {page.Offset + 1}–{page.Offset + page.Returned} of {page.Total} {noun}";
        return page.NextOffset is { } next ? $"{range}; pass offset {next} for the next page." : $"{range} (the last page).";
    }
}
