using NUnit.Framework;
using Subscriptions.Api.Domain;

namespace Subscriptions.Tests;

public class InputRulesTests
{
    [TestCase("0.01", 1)]
    [TestCase("1", 100)]
    [TestCase("12.5", 1250)]
    [TestCase("999999999.99", 99999999999L)]
    public void Money_AcceptsPositiveAmountsWithoutRounding(string text, long minorUnits)
    {
        Assert.That(InputRules.TryReadPositiveMoney(text, out var amount), Is.True);
        Assert.That(amount * 100m, Is.EqualTo(minorUnits));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0.00")]
    [TestCase("-1.00")]
    [TestCase("1.001")]
    [TestCase("1000000000.00")]
    [TestCase("1,50")]
    [TestCase("1e2")]
    public void Money_RejectsInvalidAmounts(string? text) =>
        Assert.That(InputRules.TryReadPositiveMoney(text, out _), Is.False);

    [TestCase(null, "2026-03-01")]
    [TestCase("2026-02-29", "2026-03-01")]
    [TestCase("2026-02-28", "01.03.2026")]
    [TestCase("2026-03-01", "2026-03-01")]
    [TestCase("2026-03-02", "2026-03-01")]
    public void Period_RejectsInvalidBounds(string? from, string? to) =>
        Assert.That(InputRules.TryReadPeriod(from, to, out _, out _), Is.False);

    [Test]
    public void Period_ParsesBoundsWhenNoLimitIsSpecified()
    {
        Assert.That(InputRules.TryReadPeriod("2024-02-29", "2026-03-01",
            out var start, out var end), Is.True);
        Assert.That(start, Is.EqualTo(new DateOnly(2024, 2, 29)));
        Assert.That(end, Is.EqualTo(new DateOnly(2026, 3, 1)));
    }

    [TestCase("2025-01-01", true)]
    [TestCase("2025-01-02", false)]
    public void Period_Enforces366DayLimit(string to, bool expected) =>
        Assert.That(InputRules.TryReadPeriod("2024-01-01", to, out _, out _, 366), Is.EqualTo(expected));
}
