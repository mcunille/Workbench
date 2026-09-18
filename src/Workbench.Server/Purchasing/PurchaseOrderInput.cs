// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
namespace Workbench.Server.Purchasing;

public static class PurchaseOrderInput
{
    public static Dictionary<string, string[]> Validate(DraftContent? draft, string? orderDate, string? reason, bool amendment)
    {
        var errors = DraftOrderInput.Validate(draft);
        if (!DateOnly.TryParseExact(orderDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors["orderDate"] = ["Enter a valid order date."];
        if (amendment && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000))
            errors["reason"] = ["Explain the amendment in 1 to 2,000 characters."];
        if (draft is null) return errors;
        if (string.IsNullOrWhiteSpace(draft.SupplierName)) errors["draft.supplierName"] = ["Enter the supplier name."];
        if (draft.Currency is null) errors["draft.currency"] = ["Choose the purchase currency."];
        if (draft.Entries is { Count: 0 }) errors["draft.entries"] = ["Add at least one complete line."];
        if (draft.Entries is not null)
            for (var i = 0; i < draft.Entries.Count; i++)
            {
                var line = draft.Entries[i]; if (line is null) continue;
                var field = $"draft.entries[{i}]";
                if (string.IsNullOrWhiteSpace(line.Description)) errors[field + ".description"] = ["Describe the ordered item."];
                if (line.Quantity is null) errors[field + ".quantity"] = ["Enter a positive quantity."];
                if (line.UnitOfMeasure is null) errors[field + ".unitOfMeasure"] = ["Choose the quantity unit."];
                if (line.LegacyPricing is not null || line.IndicativePrice is not null) errors[field + ".legacyPricing"] = ["Resolve or clear the retained quote before ordering."];
            }
        return errors;
    }
}
