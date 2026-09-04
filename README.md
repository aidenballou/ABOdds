# ABOdds

ABOdds is one .NET worker application that polls pregame football odds, calculates exact-line no-vig fair values, finds positive expected value, and sends deduplicated Discord alerts. PostgreSQL stores every normalized quote and every downstream decision.

There is no HTTP API, UI, message broker, bet placement, player-prop support, or CLV calculation in this version.

## Pipeline

```text
The Odds API
  -> OddsPollingWorker + normalization
  -> PostgreSQL odds snapshots
  -> FairValueWorker
  -> EvDetectionWorker
  -> AlertRuleWorker
  -> PostgreSQL alert outbox
  -> DiscordAlertWorker
```

Bounded in-memory channels wake each stage quickly. PostgreSQL stage markers are the recovery path. Each worker rescans for unfinished batches, so a restart between persistence and a channel write does not lose the batch.

Run one worker replica in V1. Alert-state reconciliation and Discord delivery are serialized inside that process so a superseded opportunity cannot be posted.

## V1 rules

- Sports: NFL and NCAAF FBS
- Markets: moneyline, spreads, and totals
- Pregame only
- Poll every five minutes normally and every minute when any known event starts within six hours
- Reject provider quotes older than 90 seconds at the time of the poll
- Request decimal odds and use decimal arithmetic for probabilities and EV
- Remove each reference book's vig before applying consensus weights
- Require two fresh reference books
- Compare spreads and totals only at the exact same line
- Alert at EV of 3% or more
- Re-alert after disappearance and reappearance, or after EV improves by at least one percentage point from the last alert
- Expire queued alerts if kickoff passes, a newer poll replaces them, or any contributing price becomes stale

Reference books and weights:

| API key | Book | Weight |
| --- | --- | ---: |
| `pinnacle` | Pinnacle | 50% |
| `betonlineag` | BetOnline | 30% |
| `lowvig` | LowVig | 20% |

Target books:

| API key | Book |
| --- | --- |
| `fanduel` | FanDuel |
| `draftkings` | DraftKings |
| `betmgm` | BetMGM |
| `williamhill_us` | Caesars |
| `fanatics` | Fanatics |
| `betrivers` | BetRivers |
| `hardrockbet` | Hard Rock Bet |
| `espnbet` | theScore Bet |

The names and keys follow [The Odds API bookmaker catalog](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html). These three references are provisional. The provider warns that its public Pinnacle feed may be delayed, and BetOnline and LowVig should not be treated as proven independent sharp signals.

The 11 unique books count as two bookmaker groups under The Odds API's current quota rules. One NFL and NCAAF cycle with three markets costs about 12 credits. Five-minute polling around the clock is about 103,680 credits per 30 days before any one-minute windows. The worker records `x-requests-remaining`, `x-requests-used`, and `x-requests-last` on every successful poll. Review the provider's [current quota rules](https://the-odds-api.com/liveapi/guides/v4/) before enabling it.

## Run with Docker

Prerequisites are Docker Desktop, an Odds API key, and a Discord webhook.

```bash
cp .env.example .env
```

Replace every placeholder in `.env`, set both `*_ENABLED` values to `true`, then run:

```bash
docker compose up --build -d
docker compose logs -f worker
```

The application applies EF Core migrations during startup. PostgreSQL listens only on `127.0.0.1:${POSTGRES_PORT:-5432}` and stores data in the `postgres-data` volume. Set `POSTGRES_PORT` if port 5432 is already in use.

Both integrations default to disabled. This keeps a fresh checkout from making paid requests or posting alerts. In Compose, set `ODDS_API_ENABLED=true` and `DISCORD_ENABLED=true` in `.env` when the credentials are ready.

Stop the application without deleting its database:

```bash
docker compose down
```

## Run and test locally

Start PostgreSQL, then run the worker from the host:

```bash
docker compose up -d postgres
dotnet tool restore
dotnet run --project src/ABOdds/ABOdds.csproj
```

Configuration uses standard .NET keys. Environment variables use double underscores, for example:

```bash
OddsApi__Enabled=true
OddsApi__ApiKey=replace-me
Discord__Enabled=true
Discord__WebhookUrl=https://discord.com/api/webhooks/replace-me
```

Run the checks:

```bash
dotnet restore ABOdds.slnx
dotnet build ABOdds.slnx --no-restore
dotnet test ABOdds.slnx --no-build --no-restore
docker compose config
```

## Stored data

- `poll_batches` records each successful provider response, observation time, quota headers, and processing status.
- `events` stores provider event IDs, teams, sport, and kickoff time.
- `markets` identifies the event, market type, and pregame period.
- `odds_snapshots` stores every normalized bookmaker outcome with both local observation time and provider source-update time.
- `fair_values` stores the consensus probability, fair price, line, reference count, and source breakdown.
- `ev_opportunities` stores qualifying target prices and links each one to its quote and fair value.
- `alert_states` stores the structured dedup key and active, last-seen, last-alerted, and disappearance state.
- `alerts` is both alert history and the Discord delivery outbox.

This is enough to add CLV without changing historical quote collection. The next slice should select the last fresh pregame consensus before kickoff, preserve exact-line matching, and compare it with the alerted price. If the original line has no closing consensus, leave CLV unpriced instead of interpolating one.

## Fair-value calculation

For each complete reference-book market:

```text
implied probability = 1 / decimal odds
no-vig probability  = implied probability / sum of all outcome probabilities
fair probability    = sum(no-vig probability * configured weight) / sum(contributing weights)
EV                  = fair probability * target decimal odds - 1
```

Weights are renormalized when a configured reference book is absent or stale, but the two-book minimum still applies. A `+3.5` spread never contributes to a `+3` fair value, and a total of `45.5` never contributes to `46`.
