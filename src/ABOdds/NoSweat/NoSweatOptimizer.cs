namespace ABOdds.NoSweat;

public static class NoSweatOptimizer
{
    public static IReadOnlyList<NoSweatOpportunity> FindQualifying(
        IReadOnlyList<HedgeMarket> markets, NoSweatOptions options)
    {
        var results = new List<NoSweatOpportunity>();
        // Identical price pairs have identical cash math under the pooled-bankroll model.
        // Calculate them once while retaining distinct qualifying events and sportsbooks.
        var conversions = markets.GroupBy(market => (market.Promo.DecimalOdds, HedgeOdds: market.Hedge.DecimalOdds))
            .Select(group => group.OrderByDescending(market => market.OldestSourceUpdatedAtUtc)
                .ThenByDescending(market => market.Event.CommenceTimeUtc).First())
            .OrderByDescending(market => (market.Promo.DecimalOdds - 1m) * (market.Hedge.DecimalOdds - 1m) / market.Hedge.DecimalOdds)
            .ToArray();
        foreach (var qualifyingGroup in markets.Where(market => market.Promo.DecimalOdds >= options.MinimumQualifyingDecimalOdds)
                     .GroupBy(market => (market.Promo.DecimalOdds, HedgeOdds: market.Hedge.DecimalOdds)))
        {
            NoSweatOpportunity? best = null;
            foreach (var conversion in conversions)
            {
                var candidate = Optimize(qualifyingGroup.First(), conversion, options.PromotionLimit, options.Bankroll,
                    best?.ProjectedMinimumProfit);
                if (candidate is not null && (best is null || candidate.ProjectedMinimumProfit > best.ProjectedMinimumProfit ||
                    candidate.ProjectedMinimumProfit == best.ProjectedMinimumProfit && candidate.InitialBankrollRequired < best.InitialBankrollRequired))
                    best = candidate;
            }
            if (best is { ProjectedMinimumProfit: > 0 })
                results.AddRange(qualifyingGroup.Select(market => best with { QualifyingMarket = market }));
        }
        return results.OrderByDescending(value => value.ProjectedMinimumProfit)
            .ThenBy(value => value.InitialBankrollRequired)
            .ThenBy(value => value.QualifyingMarket.Event.ProviderEventId, StringComparer.Ordinal)
            .ThenBy(value => value.QualifyingMarket.Promo.SelectionKey, StringComparer.Ordinal)
            .ThenBy(value => value.QualifyingMarket.Hedge.BookmakerKey, StringComparer.Ordinal)
            .Take(5).ToArray();
    }

    public static IReadOnlyList<BonusConversion> FindConversions(
        IReadOnlyList<HedgeMarket> markets, decimal bonusAmount, decimal bankroll) =>
        markets.Select(market => Convert(market, bonusAmount, bankroll))
            .Where(value => value.CashValue > 0)
            .OrderByDescending(value => value.CashValue).ThenBy(value => value.HedgeAmount)
            .ThenBy(value => value.Market.Event.ProviderEventId, StringComparer.Ordinal)
            .ThenBy(value => value.Market.Promo.SelectionKey, StringComparer.Ordinal)
            .ThenBy(value => value.Market.Hedge.BookmakerKey, StringComparer.Ordinal)
            .Take(5).ToArray();

    public static BonusConversion Convert(HedgeMarket market, decimal bonusAmount, decimal availableCash)
    {
        var bonusWinnings = FloorCash(bonusAmount * (market.Promo.DecimalOdds - 1m));
        var hedge = Math.Clamp(bonusWinnings / market.Hedge.DecimalOdds, 0m, FloorCash(availableCash));
        return CashNeighbors(hedge).Where(amount => amount <= availableCash)
            .Select(amount => new BonusConversion(market, bonusAmount, amount,
                bonusWinnings - amount, FloorCash(amount * (market.Hedge.DecimalOdds - 1m))))
            .OrderByDescending(value => value.CashValue).ThenBy(value => value.HedgeAmount).First();
    }

    private static NoSweatOpportunity? Optimize(HedgeMarket qualifying, HedgeMarket conversion, decimal limit, decimal bankroll,
        decimal? profitToBeat)
    {
        var a = qualifying.Promo.DecimalOdds;
        var b = qualifying.Hedge.DecimalOdds;
        var c = conversion.Promo.DecimalOdds;
        var d = conversion.Hedge.DecimalOdds;
        var rate = (c - 1m) * (d - 1m) / d;
        var balancedHedgePerStake = (a - rate) / b;
        var cashLimitedDenominator = 1m + d * (b - 1m);
        var cap = Math.Min(limit, bankroll);

        // With stake S and initial hedge H, the three terminal profit bounds are:
        // S(a-1)-H; S(rate-1)+H(b-1); (d-1)Y-dS+dH(b-1).
        // The best H is where the decreasing win path meets the increasing loss path,
        // clamped to [0,Y-S]. Its slope changes only at these stake breakpoints.
        var stakes = new List<decimal> { 0m, cap };
        AddBreakpoint((d - 1m) * bankroll, a + d - 1m);
        AddBreakpoint((d - 1m) * bankroll, a + d - 1m - balancedHedgePerStake * cashLimitedDenominator);
        AddBreakpoint(bankroll, 1m + balancedHedgePerStake);
        AddBreakpoint(bankroll * (cashLimitedDenominator + d - 1m), a + d - 1m + cashLimitedDenominator);
        // At H=Y-S, the two loss outcomes can cross while the win path stays higher.
        AddBreakpoint(bankroll * b * (d - 1m), rate + b * (d - 1m));

        var maximumUnroundedProfit = stakes.Max(ContinuousProfit);
        if (profitToBeat is { } prior && UpperCashBound(maximumUnroundedProfit) < prior) return null;

        NoSweatOpportunity? best = null;
        foreach (var stake in stakes.SelectMany(CashNeighbors).Append(0.01m).Distinct().Where(stake => stake > 0 && stake <= cap))
            EvaluateStake(stake);

        // Rounding payouts can move the cent-sized optimum away from a continuous
        // breakpoint. Search only stake intervals whose unrounded profit bound can
        // still improve the best executable plan.
        var intervals = new PriorityQueue<(decimal Low, decimal High), decimal>();
        Enqueue(1m, decimal.Floor(cap * 100m));
        while (intervals.TryDequeue(out var interval, out var priority))
        {
            if (best is not null && -priority <= best.ProjectedMinimumProfit) continue;
            var middle = decimal.Floor((interval.Low + interval.High) / 2m);
            EvaluateStake(middle / 100m);
            Enqueue(interval.Low, middle - 1m);
            Enqueue(middle + 1m, interval.High);
        }
        return best;

        void Enqueue(decimal low, decimal high)
        {
            if (low > high) return;
            var bound = Math.Max(ContinuousProfit(low / 100m), ContinuousProfit(high / 100m));
            foreach (var point in stakes.Where(point => point * 100m >= low && point * 100m <= high))
                bound = Math.Max(bound, ContinuousProfit(point));
            // Round the numerical bound upward before flooring to cash cents.
            bound = UpperCashBound(bound);
            if (best is null || bound > best.ProjectedMinimumProfit) intervals.Enqueue((low, high), -bound);
        }

        decimal ContinuousProfit(decimal stake)
        {
            var hedge = Math.Clamp(Math.Max(stake * balancedHedgePerStake,
                ((a + d - 1m) * stake - (d - 1m) * bankroll) / cashLimitedDenominator), 0m, bankroll - stake);
            var loss = -stake + hedge * (b - 1m);
            var conversionCash = Math.Min(stake * rate, (bankroll + loss) * (d - 1m));
            return Math.Min(stake * (a - 1m) - hedge, loss + conversionCash);
        }

        void EvaluateStake(decimal stake)
        {
            var low = 0m;
            var high = decimal.Floor((bankroll - stake) * 100m);
            // Win profit decreases with H; loss profit, including conversion, increases.
            // Their crossing and the preceding cash cent contain the best hedge.
            while (low < high)
            {
                var middle = decimal.Floor((low + high) / 2m);
                var plan = EvaluateHedge(stake, middle / 100m);
                if (plan.WinPathProfit > plan.LossPathProfit) low = middle + 1m;
                else high = middle;
            }
            Keep(EvaluateHedge(stake, low / 100m));
            if (low > 0) Keep(EvaluateHedge(stake, (low - 1m) / 100m));
        }

        NoSweatOpportunity EvaluateHedge(decimal stake, decimal hedge)
        {
            var cashAfterLoss = bankroll - stake + FloorCash(hedge * (b - 1m));
            var bonus = Convert(conversion, stake, cashAfterLoss);
            return new(qualifying, stake, hedge, FloorCash(stake * (a - 1m)) - hedge,
                cashAfterLoss, bonus, cashAfterLoss - bankroll + bonus.CashValue);
        }

        void Keep(NoSweatOpportunity plan)
        {
            if (best is null || plan.ProjectedMinimumProfit > best.ProjectedMinimumProfit ||
                plan.ProjectedMinimumProfit == best.ProjectedMinimumProfit && plan.InitialBankrollRequired < best.InitialBankrollRequired)
                best = plan;
        }

        void AddBreakpoint(decimal numerator, decimal denominator)
        {
            if (denominator > 0 && numerator / denominator <= cap) stakes.Add(numerator / denominator);
        }
    }

    private static decimal UpperCashBound(decimal value) => FloorCash(decimal.Round(value, 8, MidpointRounding.ToPositiveInfinity));
    private static decimal FloorCash(decimal value) => decimal.Floor(value * 100m) / 100m;
    private static IEnumerable<decimal> CashNeighbors(decimal value) =>
        new[] { FloorCash(value), decimal.Ceiling(value * 100m) / 100m };
}
