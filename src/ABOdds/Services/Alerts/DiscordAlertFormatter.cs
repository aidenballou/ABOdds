using System.Globalization;
using System.Text;
using ABOdds.Domain;

namespace ABOdds.Services.Alerts;

public static class DiscordAlertFormatter
{
    public static string Format(CalculatedEvOpportunity opportunity, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var sport = SportCatalog.Find(opportunity.SportKey);
        var conditional = opportunity.MarketKey == MarketKeys.Moneyline ||
            opportunity.Line is { } line && line == decimal.Truncate(line);
        var message = new StringBuilder();
        message.Append("**").Append(FormatSelection(opportunity)).Append("** · **")
            .Append(AmericanOdds.Format(opportunity.DecimalOdds)).AppendLine("**");
        message.Append(opportunity.BookmakerTitle).Append(" · **")
            .Append(FormatPercentage(opportunity.ExpectedValue, includeSign: true)).AppendLine(" EV**");
        message.AppendLine();
        message.Append(opportunity.AwayTeam).Append(" @ ").AppendLine(opportunity.HomeTeam);
        message.Append(sport?.DisplayName ?? opportunity.SportKey)
            .Append(" | ").Append(opportunity.MarketKey switch
            {
                MarketKeys.Moneyline => "Moneyline",
                MarketKeys.Spread => sport?.SpreadName ?? "Spread",
                MarketKeys.Total => "Total",
                _ => opportunity.MarketKey
            }).Append(" · Start: <t:")
            .Append(opportunity.CommenceTimeUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .AppendLine(":f>");
        message.AppendLine();
        message.Append("Fair **").Append(AmericanOdds.Format(opportunity.FairDecimalOdds))
            .Append("** · Win probability ")
            .AppendLine(FormatPercentage(opportunity.FairProbability, includeSign: false));
        var primary = opportunity.Sources.SingleOrDefault(source => source.Role == FairValueSourceRole.Primary);
        var validator = opportunity.Sources.SingleOrDefault(source => source.Role == FairValueSourceRole.Validation);
        if (primary is not null)
        {
            message.Append("Pinnacle no-vig · ");
            message.AppendLine(validator is not null ? "BetOnline confirmed" : "UNVALIDATED, BetOnline unavailable");
        }
        else if (opportunity.Sources.Any(source => source.Role == FairValueSourceRole.Fallback))
        {
            message.AppendLine("BetOnline no-vig · LOWER confidence, Pinnacle unavailable");
        }
        else
        {
            message.AppendLine("Historical consensus");
        }
        if (conditional) message.AppendLine("EV and win probability exclude pushes. Push probability is not estimated.");
        message.Append("-# Updated <t:")
            .Append(updatedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append(":R>");

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
