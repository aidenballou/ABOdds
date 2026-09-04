namespace ABOdds.Domain;

public static class MarketKeys
{
    public const string Moneyline = "h2h";
    public const string Spread = "spreads";
    public const string Total = "totals";
}

public static class MarketPeriods
{
    public const string Pregame = "pregame";
}

public static class Providers
{
    public const string TheOddsApi = "the-odds-api";
}

public enum AlertReason
{
    New,
    Improved,
    Reappeared
}

public enum AlertDeliveryStatus
{
    Pending,
    Sent,
    Failed,
    Expired
}
