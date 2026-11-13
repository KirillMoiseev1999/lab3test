using System.Globalization;

namespace Subscriptions.Api.Domain;

public readonly record struct PaymentDetails(
    string SubscriptionId, long AmountMinor, string Currency, string PaidOn);

public sealed record CurrencyTotal(string Currency, string Amount);

public static class PaymentRules
{
    public static bool IsSamePayment(PaymentDetails previous, PaymentDetails requested) =>
        previous.SubscriptionId == requested.SubscriptionId &&
        previous.AmountMinor == requested.AmountMinor &&
        previous.Currency == requested.Currency &&
        previous.PaidOn == requested.PaidOn;

    public static IReadOnlyList<CurrencyTotal> TotalByCurrency(
        IEnumerable<(string Currency, decimal Amount)> amounts) =>
        amounts.GroupBy(row => row.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CurrencyTotal(group.Key,
                group.Sum(row => row.Amount).ToString("F2", CultureInfo.InvariantCulture)))
            .ToArray();
}
