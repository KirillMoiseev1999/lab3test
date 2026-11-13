namespace Subscriptions.Api.Domain;

public static class PaymentSchedule
{
    public static IEnumerable<DateOnly> Calculate(
        DateOnly first, int billingDay, int billingMonth, bool yearly,
        DateOnly start, DateOnly end)
    {
        var earliest = first > start ? first : start;
        var firstMonth = (earliest.Year - 1) * 12 + earliest.Month - 1;
        var lastMonth = (end.Year - 1) * 12 + end.Month - 1;

        for (var monthIndex = firstMonth; monthIndex <= lastMonth; monthIndex++)
        {
            var year = monthIndex / 12 + 1;
            var month = monthIndex % 12 + 1;
            if (yearly && month != billingMonth)
            {
                continue;
            }

            var day = Math.Min(billingDay, DateTime.DaysInMonth(year, month));
            var date = new DateOnly(year, month, day);
            if (date >= earliest && date < end)
            {
               yield return date;
            }
        }
    }
}
