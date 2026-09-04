using System.Globalization;
using System.Text;
using ABOdds.Domain;

namespace ABOdds.Services.Alerts;

public static class DiscordAlertFormatter
{
    public static string Format(CalculatedEvOpportunity opportunity, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var message = new StringBuilder();
        message.AppendLine("🟢 **+EV BET**");
        message.AppendLine();
        message.Append("**").Append(FormatSelection(opportunity)).AppendLine("**");
        message.Append(opportunity.BookmakerTitle)
            .Append(": **")
            .Append(AmericanOdds.Format(opportunity.DecimalOdds))
            .AppendLine("**");
        message.AppendLine();
        message.Append("Fair Odds: **")
            .Append(AmericanOdds.Format(opportunity.FairDecimalOdds))
            .AppendLine("**");
        message.Append("Fair Probability: **")
            .Append(FormatPercentage(opportunity.FairProbability, includeSign: false))
            .AppendLine("**");
        message.Append("EV: **")
            .Append(FormatPercentage(opportunity.ExpectedValue, includeSign: true))
            .AppendLine("**");
        message.AppendLine();
        message.AppendLine("Sharp consensus:");

        foreach (var source in opportunity.Sources)
        {
            message.Append(source.BookmakerTitle)
                .Append(' ')
                .AppendLine(AmericanOdds.Format(source.OfferedDecimalOdds));
        }

        message.AppendLine();
        message.Append("Updated: <t:")
            .Append(updatedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append(":T>");

        return message.ToString();
    }

    private static string FormatSelection(CalculatedEvOpportunity opportunity)
    {
        if (!opportunity.Line.HasValue || opportunity.MarketKey == MarketKeys.Moneyline)
        {
            return opportunity.SelectionDisplayName;
        }

        var line = opportunity.MarketKey == MarketKeys.Spread
            ? opportunity.Line.Value.ToString("+0.############################;-0.############################;0", CultureInfo.InvariantCulture)
            : opportunity.Line.Value.ToString("0.############################", CultureInfo.InvariantCulture);

        return $"{opportunity.SelectionDisplayName} {line}";
    }

    private static string FormatPercentage(decimal value, bool includeSign)
    {
        var format = includeSign ? "+0.0%;-0.0%;0.0%" : "0.0%";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
