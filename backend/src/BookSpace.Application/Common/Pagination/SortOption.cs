namespace BookSpace.Application.Common.Pagination;

// A parsed `sort` query parameter: `name` ascending, `-name` descending.
//
// The allowed fields are always supplied by the caller as an explicit
// whitelist. A `sort` value never reaches a LINQ expression as a string — each
// query maps the canonical field name onto a typed OrderBy itself — so this is
// a contract check, not an injection guard, but the whitelist also keeps the
// API's sortable surface deliberate rather than "whatever happens to be a
// column".
public sealed record SortOption(string Field, bool Descending)
{
    // Returns true for a null/whitespace value with sort = null, meaning "the
    // endpoint's own default order" — omitting the parameter is not an error.
    // Returns false only when a value was supplied and is not on the whitelist.
    public static bool TryParse(
        string? value,
        IReadOnlyCollection<string> allowedFields,
        out SortOption? sort)
    {
        sort = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        var descending = trimmed.StartsWith('-');
        var field = descending ? trimmed[1..] : trimmed;

        // Match case-insensitively but return the whitelist's spelling, so a
        // handler can switch on the canonical name without normalizing again.
        var canonical = allowedFields.FirstOrDefault(
            f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));

        if (canonical is null)
        {
            return false;
        }

        sort = new SortOption(canonical, descending);
        return true;
    }
}
