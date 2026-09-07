using ABOdds.Domain;

namespace ABOdds.NoSweat;

public sealed record HedgeMarket(NormalizedEvent Event, NormalizedQuote Promo, NormalizedQuote Hedge)
{
    public DateTimeOffset OldestSourceUpdatedAtUtc =>
        Promo.SourceUpdatedAtUtc < Hedge.SourceUpdatedAtUtc ? Promo.SourceUpdatedAtUtc : Hedge.SourceUpdatedAtUtc;
}

public sealed record BonusConversion(
    HedgeMarket Market,
    decimal BonusAmount,
    decimal HedgeAmount,
    decimal CashIfBonusWins,
    decimal CashIfHedgeWins)
{
    public decimal CashValue => Math.Min(CashIfBonusWins, CashIfHedgeWins);
    public decimal ConversionRate => CashValue / BonusAmount;
}

public sealed record NoSweatOpportunity(
    HedgeMarket QualifyingMarket,
    decimal QualifyingStake,
    decimal InitialHedge,
    decimal WinPathProfit,
    decimal LossPathCashBeforeConversion,
    BonusConversion Conversion,
    decimal LossPathProfit)
{
    public decimal InitialBankrollRequired => QualifyingStake + InitialHedge;
    public decimal LossPathProfitBeforeConversion => LossPathProfit - Conversion.CashValue;
    public decimal BankrollRequired => Math.Max(InitialBankrollRequired, Conversion.HedgeAmount - LossPathProfitBeforeConversion);
    public decimal ProjectedMinimumProfit => Math.Min(WinPathProfit, LossPathProfit);
}
