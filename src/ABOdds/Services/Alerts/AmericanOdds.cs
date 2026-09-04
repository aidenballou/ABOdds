using System.Globalization;

namespace ABOdds.Services.Alerts;

public static class AmericanOdds
{
    public static int FromDecimal(decimal decimalOdds)
    {
        if (decimalOdds <= 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalOdds),
                decimalOdds,
                "Decimal odds must be greater than 1.");
        }

        var americanOdds = decimalOdds >= 2m
            ? (decimalOdds - 1m) * 100m
            : -100m / (decimalOdds - 1m);

        return checked((int)decimal.Round(americanOdds, 0, MidpointRounding.AwayFromZero));
    }

    public static string Format(decimal decimalOdds) =>
        FromDecimal(decimalOdds).ToString("+0;-0;0", CultureInfo.InvariantCulture);
}
