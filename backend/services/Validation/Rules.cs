namespace TechStore.Services.Validation;

/// <summary>The input rules from docs/services.md section 2.3. Each method records problems and returns the cleaned value.</summary>
internal static class Rules
{
    public const int MaxQuantity = 1_000_000;
    public const decimal MaxPrice = 9_999_999_999.99m; // largest value numeric(12,2) can hold

    /// <summary>Trims and upper-cases, so "lap-1" and "LAP-1" are the same SKU.</summary>
    public static string Sku(string? sku, ValidationErrors errors)
    {
        var normalized = (sku ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > 50)
        {
            errors.Add("sku", "SKU is required and must be at most 50 characters.");
        }

        return normalized;
    }

    public static string Name(string? name, ValidationErrors errors)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 255)
        {
            errors.Add("name", "Name is required and must be at most 255 characters.");
        }

        return trimmed;
    }

    public static void Id(long id, string field, ValidationErrors errors)
    {
        if (id < 1)
        {
            errors.Add(field, $"{field} must be a positive number.");
        }
    }

    public static void Quantity(int quantity, ValidationErrors errors)
    {
        if (quantity is < 1 or > MaxQuantity)
        {
            errors.Add("quantity", "Quantity must be between 1 and 1,000,000.");
        }
    }

    public static void SignedQuantity(int change, ValidationErrors errors)
    {
        if (change == 0 || Math.Abs((long)change) > MaxQuantity)
        {
            errors.Add("quantityChange", "Quantity change must be non-zero and between -1,000,000 and 1,000,000.");
        }
    }

    /// <summary>Required text, trimmed, at most 255 characters.</summary>
    public static string RequiredText(string? text, string field, ValidationErrors errors)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 255)
        {
            errors.Add(field, $"{field} is required and must be at most 255 characters.");
        }

        return trimmed;
    }

    /// <summary>Optional text: blank becomes null; longer than 255 characters is rejected.</summary>
    public static string? OptionalNote(string? note, string field, ValidationErrors errors)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > 255)
        {
            errors.Add(field, $"{field} must be at most 255 characters.");
        }

        return trimmed;
    }

    /// <summary>For operations whose only input is an ID (for example submit, ship, get order).</summary>
    public static void RequireId(long id, string field)
    {
        var errors = new ValidationErrors();
        Id(id, field, errors);
        errors.ThrowIfAny();
    }

    public static void Paging(int page, int pageSize, ValidationErrors errors)
    {
        if (page < 1)
        {
            errors.Add("page", "Page must be 1 or greater.");
        }

        if (pageSize is < 1 or > 200)
        {
            errors.Add("pageSize", "Page size must be between 1 and 200.");
        }
    }

    /// <summary>Rejects (rather than rounds) prices with more than 2 decimals.</summary>
    public static void Price(decimal price, ValidationErrors errors)
    {
        if (price < 0 || price > MaxPrice || decimal.Round(price, 2) != price)
        {
            errors.Add("price", "Price must be between 0 and 9,999,999,999.99 with at most 2 decimal places.");
        }
    }
}
