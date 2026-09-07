# No-sweat cash optimizer

This mode maximizes the minimum cash profit across the qualifying win and the two possible Bonus Bet conversion outcomes. It uses offered sportsbook prices directly. It does not calculate probabilities, no-vig prices, or expected value.

## Configuration

The starting settings are BetMGM, a $1,500 promotion limit, a $7,000 pooled cash bankroll, no minimum qualifying odds, and a single indivisible Bonus Bet on a loss. Only pregame half-point spreads and totals are eligible.

The settings have been added to `.env` and `.env.example`, with the mode disabled initially. To select it for a Docker run:

```dotenv
NO_SWEAT_ENABLED=true
NO_SWEAT_STAGE=Qualifying
NO_SWEAT_PROMO_BOOK=betmgm
NO_SWEAT_PROMOTION_LIMIT=1500
NO_SWEAT_BANKROLL=7000
NO_SWEAT_BONUS_BET_AMOUNT=1500
NO_SWEAT_MINIMUM_DECIMAL_ODDS=1
ODDS_API_ENABLED=true
DISCORD_ENABLED=true
```

Keep the existing API key and webhook settings. Restart/recreate the worker after changing its mode. `NO_SWEAT_ENABLED=false` selects the existing EV application again.

For an existing Bonus Bet, set `NO_SWEAT_STAGE=BonusConversion`, `NO_SWEAT_BONUS_BET_AMOUNT` to the actual token amount, and `NO_SWEAT_BANKROLL` to the cash currently available for hedging. The entire token is used on one selection. There is no splitting or sequential conversion of smaller tokens.

Hedge books live in `NoSweat:HedgeBooks` in `src/ABOdds/appsettings.json`, independently of EV targets. Defaults are FanDuel, DraftKings, Caesars, Fanatics, Hard Rock Bet, and theScore Bet. The promo sportsbook is excluded from hedge books. Requests use the promo book plus hedge books, capped at ten total, and only `spreads,totals`.

A value of `1` for minimum qualifying decimal odds means there is no additional promo odds restriction. For example, `2` requires +100 or longer. This restriction applies to the qualifying bet, not the conversion benchmark.

The existing polling cadence, source freshness, request timeout, credit cap, and integration enable flags still apply. Select leagues with `ODDS_API_SPORTS` in `.env`, shared with EV mode. With the default three sports, two markets, and seven books, a full nonempty cycle is estimated at six credits under [The Odds API quota rules](https://the-odds-api.com/liveapi/guides/v4/#usage-quota-costs).

Compose reads `.env`; a direct `dotnet run` uses standard .NET environment variables, such as `NoSweat__Enabled=true`, or command-line configuration. The application does not load `.env` itself.

## Architecture

`ApplicationHost` selects the mode at startup. No-sweat mode registers its own worker and options. It does not register the EV polling worker, calculation pipeline, alert repository, Discord outbox worker, or EF database factory. Previously queued EV alerts cannot be delivered while this mode is selected.

The shared pieces are the HTTP adapters, normalized odds types, clock, and polling schedule. `OddsRequest` gives the HTTP adapter the selected mode's book and market lists, so the adapter has no dependency on EV or no-sweat policy.

```text
The Odds API
  -> normalization
  -> NoSweatWorker's latest snapshot per sport
  -> HedgeMarketMatcher
  -> NoSweatOptimizer
  -> ranked top-five report
  -> application logs and Discord
```

The no-sweat code is under `src/ABOdds/NoSweat`. The optimizer has no database or network calls. It retains distinct qualifying events and hedge books but reuses cash calculations when their price pairs are identical.

Reports and notification deduplication are held in memory. No-sweat does not write into EV tables or create a second database schema. Restarting can repeat a report. Failed notification requests retry using fresh results on the next poll; successful-page deduplication and Discord cooldowns last for the process lifetime. There is no durable delivery guarantee for these advisory reports.

The application itself needs no database in this mode. The existing Compose stack still starts PostgreSQL because it also supports EV mode.

## Cash model

The qualifying stake `S` can range from one cent to the smaller of the promotion limit and bankroll. The refund is `S` as one Bonus Bet. Initial hedge `H` must satisfy `S + H <= Y`.

After a qualifying loss, available cash is:

```text
B = Y - S + H * (Dh - 1)
```

This includes uncommitted cash and the initial hedge's settled winnings. The conversion hedge must fit within `B`. It is not funded from the original, unchanged bankroll and is not added to the initial hedge as if both were outstanding simultaneously.

For Bonus Bet decimal odds `Db` and conversion hedge odds `Dc`, the unconstrained conversion hedge is:

```text
J = S * (Db - 1) / Dc
```

If cash is insufficient, the hedge is capped by `B`. The full indivisible Bonus Bet is still used. The tool reports both conversion outcomes instead of claiming they remain equal:

```text
Bonus wins:       S * (Db - 1) - J
Conversion hedge wins: J * (Dc - 1)
V = minimum of those two amounts
```

The initial outcomes are:

```text
Qualifying wins:  S * (Dp - 1) - H
Qualifying loses: -S + H * (Dh - 1) + V
```

Without binding cash constraints, the best initial hedge reduces to the supplied formula `(S * Dp - V) / Dh`. The implemented search also handles cases where the two stages compete for limited cash.

The continuous solution supplies upper bounds for the search. The optimizer then searches cent-sized qualifying stakes and initial hedges, selecting the conversion hedge at adjacent cash cents. Stake intervals whose continuous upper bound cannot improve the best plan are skipped. All winnings are rounded down to cents before final profits are calculated. Tests compare the result against exhaustive small-bankroll searches, including rounding and funding-boundary regressions.

Initial cash committed is `S + H`. Total bankroll required is the larger of that amount and the cash needed to fund the conversion hedge after accounting for the qualifying-loss result. Both values appear in the report.

## Reading the results

Qualifying reports include the promo and hedge books, selections, prices, stakes, initial and total cash requirements, winning profit, refund amount, projected conversion value/rate, conversion hedge, projected loss-path profit, and projected minimum profit. The highest five positive-profit plans are returned.

The selected conversion market is an indicative benchmark drawn from current quotes. It may be a different event or even the qualifying event itself. Those prices may not exist when the qualifying bet settles. Its hedge is not an instruction to place the later-stage bet now. Re-run `BonusConversion` once the token exists.

Existing-Bonus-Bet reports rank the highest minimum conversion cash and show the bonus-win and hedge-win outcomes separately. Neither mode places bets.

The calculations assume pooled cash can be allocated to the specified sportsbooks after settlement, matching settlement rules, acceptance of both bets at the quoted stakes/prices, and a full Bonus Bet refund under the stated promotion. The tool does not inspect account balances, book limits, withdrawal timing, promo eligibility, or token expiry. Half-point lines remove push outcomes from this model; they do not eliminate cancellation or rule differences.

`--once` makes at most one request per configured sport and always disables Discord. In this mode it prints reports and exits without using PostgreSQL. It makes real paid API requests if run with a real key. The automated tests substitute all external HTTP responses and use no real account credentials.
