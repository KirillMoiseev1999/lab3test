using NUnit.Framework;
using Subscriptions.Api.Domain;

namespace Subscriptions.Tests;

public class PaymentRulesTests
{
    private static readonly PaymentDetails Original = new("sub-1", 10000, "RUB", "2026-11-10");

    [Test]
    public void SameDetailsCanBeReplayed() =>
        Assert.That(PaymentRules.IsSamePayment(Original, new("sub-1", 10000, "RUB", "2026-11-10")), Is.True);

    [TestCase("sub-2", 10000, "RUB", "2026-11-10")]
    [TestCase("sub-1", 20000, "RUB", "2026-11-10")]
    [TestCase("sub-1", 10000, "USD", "2026-11-10")]
    [TestCase("sub-1", 10000, "RUB", "2026-11-09")]
    public void ChangingAnyPaymentDetailConflicts(string id, long minor, string currency, string paidOn) =>
        Assert.That(PaymentRules.IsSamePayment(Original, new(id, minor, currency, paidOn)), Is.False);

    [Test]
    public void TotalsKeepCurrenciesSeparateAndUseExactDecimals()
    {
        var totals = PaymentRules.TotalByCurrency(new[]
        {
            ("USD", 3.50m), ("RUB", 0.10m), ("RUB", 0.20m), ("EUR", 2m)
        });

        Assert.That(totals, Is.EqualTo(new CurrencyTotal[]
        {
            new("EUR", "2.00"), new("RUB", "0.30"), new("USD", "3.50")
        }));
    }

    [Test]
    public void EmptyInputHasNoTotals() =>
        Assert.That(PaymentRules.TotalByCurrency(Array.Empty<(string, decimal)>()), Is.Empty);
}
