// Copyright (c) 2026 The White Stag Collection.
using System.Numerics;
namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInput
{
    private static string? Number(string? value) => value is not null && PricePattern().IsMatch(value) ? Format(Scaled(value)) : value;
    private static DraftDiscount? NormalizeDiscount(DraftDiscount? discount) => discount is null ? null : discount with { Value = Number(discount.Value)! };
    private static BigInteger? Reduction(BigInteger? basis, DraftDiscount? discount)
    {
        if (basis is null) return null;
        if (discount is null) return BigInteger.Zero;
        if (discount.Mode == "fixed") return Scaled(discount.Value);
        var result = BigInteger.DivRem(basis.Value * Scaled(discount.Value), 1000000, out var remainder);
        return result + (remainder >= 500000 ? 1 : 0);
    }
    private static string? Amount(BigInteger? value) => value is { } amount ? Format(amount) : null;
    public static DraftCalculationResponse Calculate(DraftContent draft)
    {
        var lines = draft.Entries.Select(e => { var gross = Gross(e); var discount = Reduction(gross, e.Discount); return (e.Id, Gross: gross, Discount: discount, Net: gross - discount); }).ToArray();
        var known = lines.Where(e => e.Gross is not null).ToArray();
        var complete = lines.Length > 0 && known.Length == lines.Length;
        BigInteger? grossTotal = known.Length == 0 ? null : known.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Gross!.Value);
        BigInteger? lineDiscounts = complete ? lines.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Discount!.Value) : null;
        var net = complete ? grossTotal - lineDiscounts : null;
        var orderReduction = Reduction(net, draft.OrderDiscount);
        var discounted = net - orderReduction;
        BigInteger? ChargeTotal(string kind)
        {
            var rows = draft.Charges.Where(c => c.PayeeKind == kind).ToArray();
            return rows.Any(c => c.Amount is null) ? null : rows.Aggregate(BigInteger.Zero, (sum, c) => sum + Scaled(c.Amount!));
        }
        var supplier = ChargeTotal("supplier"); var thirdParty = ChargeTotal("thirdParty");
        return new(lines.Select(e => new DraftLineCalculation(e.Id, Amount(e.Gross), Amount(e.Gross), Amount(e.Discount), Amount(e.Net))).ToArray(),
            lines.Length - known.Length, Amount(grossTotal), Amount(lineDiscounts), Amount(net), Amount(net), Amount(orderReduction),
            Amount(discounted), Amount(supplier), Amount(thirdParty), Amount(discounted + supplier), Amount(discounted + supplier + thirdParty), draft.Charges.Count(c => c.Amount is null));
    }
    private static void ValidateDiscount(DraftDiscount? discount, string field, string? currency, BigInteger? basis, Dictionary<string, string[]> errors)
    {
        if (discount is null) return;
        if (discount.Mode is not ("fixed" or "percentage")) errors[field + ".mode"] = ["Choose fixed amount or percentage."];
        if (discount.Value is null || !PricePattern().IsMatch(discount.Value)) { errors[field + ".value"] = ["Enter a nonnegative value with up to 19 integer digits and four decimal places."]; return; }
        var value = Scaled(discount.Value);
        if (discount.Mode == "percentage" && value > 1000000) errors[field + ".value"] = ["Use a percentage from 0 to 100."];
        if (discount.Mode == "fixed")
        {
            if (currency is null) errors["draft.currency"] = ["Choose a currency when entering an amount."];
            if (basis is not null && value > basis) errors[field + ".value"] = ["The discount cannot exceed its eligible base."];
        }
    }
    private static void ValidateAdjustments(DraftContent draft, Dictionary<string, string[]> errors)
    {
        BigInteger? net = draft.Entries?.Count > 0 ? BigInteger.Zero : null;
        if (draft.Entries is not null)
            for (var i = 0; i < Math.Min(draft.Entries.Count, 100); i++)
            {
                var entry = draft.Entries[i]; if (entry is null) { net = null; continue; }
                var field = $"draft.entries[{i}]";
                var gross = errors.Keys.Any(k => k.StartsWith(field, StringComparison.Ordinal)) ? null : Gross(entry);
                ValidateDiscount(entry.Discount, field + ".discount", draft.Currency, gross, errors);
                net = errors.Keys.Any(k => k.StartsWith(field, StringComparison.Ordinal)) ? null : net + gross - Reduction(gross, entry.Discount);
            }
        ValidateDiscount(draft.OrderDiscount, "draft.orderDiscount", draft.Currency, net, errors);
        if (draft.Charges is null || draft.Charges.Count > 50) { errors["draft.charges"] = ["Supply up to 50 charges."]; return; }
        HashSet<Guid> ids = [];
        for (var i = 0; i < draft.Charges.Count; i++)
        {
            var c = draft.Charges[i]; var field = $"draft.charges[{i}]";
            if (c is null) { errors[field] = ["Supply a charge."]; continue; }
            if (c.Id == Guid.Empty || !ids.Add(c.Id)) errors[field + ".id"] = ["Use a unique nonempty charge identifier."];
            if (c.Category is not ("shipping" or "handling" or "insurance" or "salesTax" or "vatGst" or "customsDuty" or "otherTax" or "brokerage" or "paymentFee" or "inspection" or "other")) errors[field + ".category"] = ["Choose a charge category."];
            if (string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 200 || (c.Category == "other" && string.Equals(c.Label.Trim(), "Other", StringComparison.OrdinalIgnoreCase))) errors[field + ".label"] = ["Enter a label of up to 200 characters."];
            if (c.PayeeKind is not ("supplier" or "thirdParty")) errors[field + ".payeeKind"] = ["Choose supplier or third party."];
            if ((c.PayeeKind == "supplier" && c.PayeeName is not null) || c.PayeeName?.Length > 200 || (c.PayeeKind == "thirdParty" && string.IsNullOrWhiteSpace(c.PayeeName))) errors[field + ".payeeName"] = ["Enter a third-party payee name of up to 200 characters."];
            if (c.AmountStatus is not ("estimated" or "confirmed")) errors[field + ".amountStatus"] = ["Choose estimated or confirmed."];
            if ((c.Amount is not null && !PricePattern().IsMatch(c.Amount)) || (c.AmountStatus == "confirmed" && c.Amount is null)) errors[field + ".amount"] = ["Enter a nonnegative amount with up to 19 integer digits and four decimal places; confirmed charges require an amount."];
            if (c.Amount is not null && draft.Currency is null) errors["draft.currency"] = ["Choose a currency when entering an amount."];
            if (c.Reference?.Length > 200) errors[field + ".reference"] = ["Use up to 200 characters."];
            if (c.Notes?.Length > 2000) errors[field + ".notes"] = ["Use up to 2000 characters."];
        }
        if (errors.Count == 0)
        {
            var calculation = Calculate(draft);
            if (new[] { calculation.MerchandiseEstimate, calculation.SupplierCharges, calculation.ThirdPartyCharges, calculation.SupplierEstimate, calculation.PurchaseEstimate }.Any(value => value is not null && Scaled(value) >= BigInteger.Pow(10, 25)))
                errors["draft.charges"] = ["The estimate exceeds 21 integer digits."];
        }
    }
    public static Dictionary<string, string[]> ValidateConfirmedCorrections(IReadOnlyList<DraftCharge> saved, IReadOnlyList<DraftCharge> replacement, bool supplierChanged = false)
    {
        Dictionary<string, string[]> errors = [];
        for (var i = 0; i < replacement.Count; i++)
        {
            var current = replacement[i]; var old = saved.FirstOrDefault(c => c.Id == current.Id);
            if (old?.AmountStatus == "confirmed" && (old.Amount != current.Amount || old.PayeeKind != current.PayeeKind || old.PayeeName != current.PayeeName || old.AmountStatus != current.AmountStatus || (old.PayeeKind == "supplier" && supplierChanged)) && (string.IsNullOrWhiteSpace(current.Notes) || current.Notes == old.Notes))
                errors[$"draft.charges[{i}].notes"] = ["Append a new explanation for changing this confirmed charge's amount, payee or status."];
        }
        return errors;
    }
}
