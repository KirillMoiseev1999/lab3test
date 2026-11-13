using System.Globalization;
using System.Text.RegularExpressions;

namespace Subscriptions.Api.Domain;

public static class InputRules
{
    private static readonly Regex MoneyFormat = new(@"\A[0-9]{1,9}(\.[0-9]{1,2})?\z");

    public static bool TryReadPositiveMoney(string? text, out decimal amount)
    {
        amount = 0m;
        if (text is null || !MoneyFormat.IsMatch(text))
        {
            return false;
        }

        // The format limits the value to nine integral and two fractional digits.
        amount = decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return amount > 0m;
    }

    public static bool TryReadPeriod(
        string? from, string? to, out DateOnly start, out DateOnly end, int? maxDays = null)
    {
        const string format = "yyyy-MM-dd";
        end = default;
        if (!DateOnly.TryParseExact(from, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
            !DateOnly.TryParseExact(to, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out end) ||
            start >= end)
        {
            return false;
        }

        return maxDays is null || end.DayNumber - start.DayNumber <= maxDays.Value;
    }
}
