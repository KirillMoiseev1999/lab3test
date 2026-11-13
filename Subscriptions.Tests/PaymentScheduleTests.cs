using NUnit.Framework;
using Subscriptions.Api.Domain;

namespace Subscriptions.Tests;

public class PaymentScheduleTests
{
    [Test]
    public void Monthly_ShortMonthDoesNotChangeBillingDay()
    {
        var dates = PaymentSchedule.Calculate(new(2026, 1, 31), 31, 1, false,
            new(2026, 2, 1), new(2026, 4, 1));

        Assert.That(dates, Is.EqualTo(new DateOnly[] { new(2026, 2, 28), new(2026, 3, 31) }));
    }

    [Test]
    public void Monthly_IncludesStartAndExcludesEnd()
    {
        var dates = PaymentSchedule.Calculate(new(2026, 1, 10), 10, 1, false,
            new(2026, 2, 10), new(2026, 3, 10));

        Assert.That(dates, Is.EqualTo(new DateOnly[] { new(2026, 2, 10) }));
    }

    [Test]
    public void Monthly_ExcludesPaymentBeforePeriodInSameMonth()
    {
        var dates = PaymentSchedule.Calculate(new(2026, 1, 10), 10, 1, false,
            new(2026, 2, 11), new(2026, 3, 11));

        Assert.That(dates, Is.EqualTo(new DateOnly[] { new(2026, 3, 10) }));
    }

    [Test]
    public void Monthly_NeverSchedulesBeforeFirstPayment()
    {
        var dates = PaymentSchedule.Calculate(new(2026, 3, 15), 15, 3, false,
            new(2026, 1, 1), new(2026, 4, 1));

        Assert.That(dates, Is.EqualTo(new DateOnly[] { new(2026, 3, 15) }));
    }

    [Test]
    public void FirstPaymentAfterPeriod_ReturnsEmpty()
    {
        var dates = PaymentSchedule.Calculate(new(2026, 5, 10), 10, 5, false,
            new(2026, 2, 1), new(2026, 3, 1));

        Assert.That(dates, Is.Empty);
    }

    [TestCase(2025, 28)]
    [TestCase(2028, 29)]
    public void Yearly_PreservesFebruaryAnchorAcrossLeapYears(int year, int day)
    {
        var dates = PaymentSchedule.Calculate(new(2024, 2, 29), 29, 2, true,
            new(year, 1, 1), new(year, 5, 1));

        Assert.That(dates, Is.EqualTo(new DateOnly[] { new(year, 2, day) }));
    }
}
