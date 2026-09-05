using System.Globalization;
using System.Text;
using ABOdds.Domain;

namespace ABOdds.Services.Alerts;

public static class DiscordAlertFormatter
{
    public static string Format(CalculatedEvOpportunity opportunity, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var conditional = opportunity.MarketKey == MarketKeys.Moneyline ||
            opportunity.Line is { } line && line == decimal.Truncate(line);
        var probabilityLabel = conditional ? "Fair Probability conditional on no push" : "Fair Probability";
        var evLabel = conditional ? "EV conditional on no push" : "EV";
        var message = new StringBuilder();
        message.AppendLine("🟢 **+EV BET**");
        message.AppendLine();
        message.Append(opportunity.AwayTeam).Append(" @ ").AppendLine(opportunity.HomeTeam);
        message.Append(opportunity.SportKey == "americanfootball_nfl" ? "NFL" : "NCAAF")
            .Append(" | ").AppendLine(opportunity.MarketKey switch
            {
                MarketKeys.Moneyline => "Moneyline",
                MarketKeys.Spread => "Spread",
                MarketKeys.Total => "Total",
                _ => opportunity.MarketKey
            });
        message.Append("Kickoff: <t:")
            .Append(opportunity.CommenceTimeUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .AppendLine(":f>");
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
        message.Append(probabilityLabel).Append(": **")
            .Append(FormatPercentage(opportunity.FairProbability, includeSign: false))
            .AppendLine("**");
        message.Append(evLabel).Append(": **")
            .Append(FormatPercentage(opportunity.ExpectedValue, includeSign: true))
            .AppendLine("**");
        if (conditional) message.AppendLine("Push probability is not estimated.");
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
