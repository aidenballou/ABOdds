using ABOdds.Configuration;

namespace ABOdds.NoSweat;

public enum NoSweatStage { Qualifying, BonusConversion }

public sealed class NoSweatOptions
{
    public const string SectionName = "NoSweat";
    public bool Enabled { get; init; }
    public NoSweatStage Stage { get; init; }
    public string PromoBook { get; init; } = "betmgm";
    public decimal PromotionLimit { get; init; } = 1500m;
    public decimal Bankroll { get; init; } = 7000m;
    public decimal BonusBetAmount { get; init; } = 1500m;
    public decimal MinimumQualifyingDecimalOdds { get; init; } = 1m;
    public IReadOnlyList<BookOptions> HedgeBooks { get; init; } = [];

    public static bool IsValid(NoSweatOptions value) =>
        Enum.IsDefined(value.Stage) &&
        !string.IsNullOrWhiteSpace(value.PromoBook) &&
        value.PromotionLimit > 0 && value.Bankroll > 0 && value.BonusBetAmount > 0 &&
        value.PromotionLimit == decimal.Round(value.PromotionLimit, 2) &&
        value.Bankroll == decimal.Round(value.Bankroll, 2) &&
        value.BonusBetAmount == decimal.Round(value.BonusBetAmount, 2) &&
        value.MinimumQualifyingDecimalOdds >= 1 &&
        value.HedgeBooks.Count is > 0 and < 10 &&
        value.HedgeBooks.All(book => !string.IsNullOrWhiteSpace(book.Key) &&
            !string.Equals(book.Key.Trim(), value.PromoBook.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        value.HedgeBooks.Select(book => book.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.HedgeBooks.Count;
}
