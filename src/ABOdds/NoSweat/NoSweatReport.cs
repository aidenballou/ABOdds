using System.Globalization;
using System.Text;
using ABOdds.Domain;
using ABOdds.Services.Alerts;

namespace ABOdds.NoSweat;

public sealed record NoSweatReport(IReadOnlyList<string> Pages, DateTimeOffset ValidUntilUtc);

public static class NoSweatFormatter
{
    public static NoSweatReport Qualifying(IReadOnlyList<NoSweatOpportunity> plans, TimeSpan maximumSourceAge)
    {
        var entries = plans.Select((plan, index) =>
        {
            var market = plan.QualifyingMarket;
            return $"**#{index + 1} Projected minimum profit: {Money(plan.ProjectedMinimumProfit)}**\n" +
                Describe(market) +
                $"Qualify: {Money(plan.QualifyingStake)} on {market.Promo.BookmakerTitle}\n" +
                $"Initial hedge: {Money(plan.InitialHedge)} on {market.Hedge.BookmakerTitle}\n" +
                $"Initial cash committed: {Money(plan.InitialBankrollRequired)}; total bankroll required: {Money(plan.BankrollRequired)}\n" +
                $"Qualifying win: {Money(plan.WinPathProfit)} profit\n" +
                $"Qualifying loss: {Money(plan.Conversion.BonusAmount)} Bonus Bet; cash remaining {Money(plan.LossPathCashBeforeConversion)}\n" +
                $"Projected conversion: {Money(plan.Conversion.CashValue)} ({plan.Conversion.ConversionRate.ToString("P2", CultureInfo.InvariantCulture)})\n" +
                $"Projected loss-path profit: {Money(plan.LossPathProfit)}\n" +
                $"Conversion hedge cash: {Money(plan.Conversion.HedgeAmount)}\n" +
                $"Benchmark only: {Describe(plan.Conversion.Market)}";
        });
        var markets = plans.SelectMany(plan => new[] { plan.QualifyingMarket, plan.Conversion.Market });
        return Build("NO-SWEAT · Qualifying plans\nFuture Bonus Bet conversion is projected from current prices and cannot be locked now.",
            entries, markets, maximumSourceAge);
    }

    public static NoSweatReport Bonus(IReadOnlyList<BonusConversion> plans, TimeSpan maximumSourceAge)
    {
        var entries = plans.Select((plan, index) =>
            $"**#{index + 1} Minimum conversion cash: {Money(plan.CashValue)} ({plan.ConversionRate.ToString("P2", CultureInfo.InvariantCulture)})**\n" +
            Describe(plan.Market) +
            $"Bonus stake: {Money(plan.BonusAmount)}; cash hedge: {Money(plan.HedgeAmount)}\n" +
            $"Bonus wins: {Money(plan.CashIfBonusWins)} cash; hedge wins: {Money(plan.CashIfHedgeWins)} cash\n");
        return Build("NO-SWEAT · Existing Bonus Bet conversion\nCash outcomes assume both bets are accepted at these prices with matching settlement rules.",
            entries, plans.Select(plan => plan.Market), maximumSourceAge);
    }

    private static NoSweatReport Build(string heading, IEnumerable<string> entries,
        IEnumerable<HedgeMarket> markets, TimeSpan maximumSourceAge)
    {
        var pages = new List<string>();
        var page = new StringBuilder(heading);
        foreach (var entry in entries)
        {
            if (page.Length + entry.Length + 2 > 3900)
            {
                pages.Add(page.ToString());
                page.Clear().Append(heading);
            }
            page.Append("\n\n").Append(entry);
        }
        if (!markets.Any()) page.Append("\nNo positive-profit plans with fresh matching prices and the configured bankroll.");
        pages.Add(page.ToString());
        var validUntil = markets.Select(market =>
            market.Event.CommenceTimeUtc < market.OldestSourceUpdatedAtUtc + maximumSourceAge
                ? market.Event.CommenceTimeUtc : market.OldestSourceUpdatedAtUtc + maximumSourceAge)
            .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        return new(pages, validUntil);
    }

    private static string Describe(HedgeMarket market) =>
        $"{market.Event.AwayTeam} @ {market.Event.HomeTeam} · {SportCatalog.Find(market.Event.SportKey)?.DisplayName ?? market.Event.SportKey}\n" +
        $"Start <t:{market.Event.CommenceTimeUtc.ToUnixTimeSeconds()}:f> · {market.Promo.MarketKey}\n" +
        $"{market.Promo.BookmakerTitle}: {Selection(market.Promo)} {Price(market.Promo.DecimalOdds)}\n" +
        $"{market.Hedge.BookmakerTitle}: {Selection(market.Hedge)} {Price(market.Hedge.DecimalOdds)}\n";

    private static string Selection(NormalizedQuote quote) => quote.SelectionDisplayName + " " +
        quote.Line!.Value.ToString(quote.MarketKey == MarketKeys.Spread ? "+0.0;-0.0;0" : "0.0", CultureInfo.InvariantCulture);
    private static string Price(decimal odds) => $"{AmericanOdds.Format(odds)} / {odds.ToString("0.####", CultureInfo.InvariantCulture)}";
    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
