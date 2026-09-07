# ABOdds

ABOdds is one .NET worker application that polls pregame NFL, NCAAF, and MLB odds and runs one of two modes. The default EV mode calculates exact-line Pinnacle no-vig fair values with BetOnline validation and sends deduplicated Discord alerts. The optional no-sweat mode optimizes qualifying bets and Bonus Bet conversion for minimum cash profit. PostgreSQL stores quotes and decisions in EV mode; no-sweat mode keeps its snapshots and reports in memory.

There is no HTTP API, UI, message broker, bet placement, player-prop support, or CLV calculation in this version.

Select the [no-sweat cash optimizer](docs/no-sweat.md) with `NO_SWEAT_ENABLED=true`. The application then registers only the no-sweat worker. EV calculations, EV notifications, and database services do not run, including delivery of previously queued EV alerts.

The [code and test audit](docs/code-test-audit-2026-09-05.md) documents the cleanup and verification, including real PostgreSQL and application-host tests. The earlier [readiness audit](docs/readiness-audit-2026-09-04.md) records the deployment fixes. The Docker image builds and starts with both integrations disabled. The next step is the bounded live smoke test below; account-specific book coverage and live delivery have not been verified. Both integrations remain disabled by default.

## No-sweat mode

The optimizer compares opposite selections at matching half-point spreads and totals across sportsbooks. It ranks the top five positive-profit plans and sends the results to application logs and Discord when delivery is enabled. Moneylines, whole-number lines, and quarter-point lines are excluded.

For Docker, keep your API key and webhook in `.env` and set:

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

Recreate the worker to apply the settings:

```bash
docker compose up --build -d worker
docker compose logs -f worker
```

`Qualifying` chooses a cash stake up to the promotion limit and an initial hedge. It accounts for the cash available after a qualifying loss when estimating conversion of the refund as one indivisible Bonus Bet. A minimum decimal odds setting of `1` adds no qualifying odds restriction.

Each result includes both sportsbooks and selections, odds, stakes, initial hedge, total bankroll required, qualifying-win profit, projected Bonus Bet value and conversion rate, projected loss-path profit, and projected minimum profit. Future conversion prices are current benchmarks, so the initial result is a projection rather than locked profit.

Once you receive a Bonus Bet, change `NO_SWEAT_STAGE=BonusConversion`, set `NO_SWEAT_BONUS_BET_AMOUNT` to the actual token amount, and set `NO_SWEAT_BANKROLL` to the cash now available for hedging. Recreate the worker again. This stage ranks conversion plans for the entire token and shows the cash result for both outcomes.

Set `NO_SWEAT_ENABLED=false` and recreate the worker to return to EV mode. The shared polling cadence, quote freshness, API credit cap, and integration flags apply to both modes. Hedge books are configured separately under `NoSweat:HedgeBooks` in `src/ABOdds/appsettings.json`.

```text
The Odds API -> normalization -> latest snapshots in memory
  -> exact-line matching -> cash optimizer -> top-five report
  -> application logs and Discord
```

No-sweat mode assumes cash can move between sportsbooks after settlement. It does not check account balances, promo eligibility, bet limits, or token expiry. Reports and Discord deduplication do not survive a restart. The app needs no database in this mode, though the existing Compose stack still starts PostgreSQL. See the [full no-sweat guide](docs/no-sweat.md) for the cash formulas and assumptions.

## EV pipeline

```text
The Odds API
  -> OddsPollingWorker + normalization
  -> PostgreSQL odds snapshots
  -> OddsPipeline: fair value -> EV -> alert rules
  -> PostgreSQL alert outbox
  -> DiscordAlertWorker
```

Two background workers run inside one process: polling/processing and Discord delivery. Calculations run sequentially as ordinary functions. PostgreSQL stage markers allow unfinished paid snapshots to resume before another request is made. Each stage commits its data and completion marker together; alert rules commit state and the outbox together. Replaying completed stages does not create duplicates. The outbox is checked every two seconds by default.

Run one worker replica in V1. Alert-state database updates are serialized, but Discord HTTP does not hold that lock. Each send checks validity and is limited by a ten-second request timeout, source expiry, and kickoff. An in-flight message may still reach Discord after a newer snapshot arrives. A crash after Discord accepts a message but before its delivery is recorded can cause a duplicate on restart.

Discord rate limits pause the whole webhook, including newly queued alerts. The cooldown is persisted so restarting the worker does not bypass it. Successful responses with an exhausted rate-limit bucket also pause delivery until Discord's reset time, with a 250 ms margin. Requests use `wait=true` for server confirmation. A 429 keeps the alert pending; stale, superseded, or started-event alerts still expire rather than sending outdated prices.

Notifications use a compact green embed: selection and offered price, sportsbook and EV, matchup and kickoff, then fair odds and reference confidence. Push caveats remain visible. Reference-price lists and validation arithmetic remain available in stored calculation history rather than occupying the notification. Existing queued messages retain their saved text; newly created alerts use the compact layout.

## EV rules

- Supported sports: NFL, NCAAF FBS, and MLB; select the active leagues with `ODDS_API_SPORTS`
- Markets: moneyline, spreads, and totals
- Pregame only
- Poll each league every five minutes normally and every minute when that league has a known event within six hours
- Reject provider quotes older than 90 seconds at the time of the poll
- Request decimal odds and use decimal arithmetic for probabilities and EV
- Remove each reference book's vig independently
- Use Pinnacle no-vig fair probabilities; use BetOnline only for validation
- Suppress a target opportunity if the absolute Pinnacle/BetOnline EV difference exceeds 3 percentage points
- Send Pinnacle-only opportunities as UNVALIDATED; use BetOnline alone with LOWER confidence when Pinnacle is unavailable
- Reject reference and target moneyline markets containing a draw or any outcome other than the two teams
- Compare spreads and totals only at the exact same line
- Alert at EV of 3% or more
- Re-alert after disappearance and reappearance, or after EV improves by at least one percentage point from the last alert
- Expire queued alerts if kickoff passes, a newer poll replaces them, or any contributing price becomes stale

Startup rejects duplicate sports/markets, blank or duplicate book keys after trimming and case normalization, nonpositive EV thresholds or validation tolerance, reference/target overlap, references other than exactly Pinnacle and BetOnline, and more than ten total books. To change the bookmaker set, replace an existing book instead of adding an eleventh one.

Reference books:

| API key | Book | Role |
| --- | --- | --- |
| `pinnacle` | Pinnacle | Primary fair probability |
| `betonlineag` | BetOnline | Validation; fallback if Pinnacle is unavailable |

LowVig is excluded from fair-value calculations and API requests.

Target books:

| API key | Book |
| --- | --- |
| `fanduel` | FanDuel |
| `draftkings` | DraftKings |
| `betmgm` | BetMGM |
| `williamhill_us` | Caesars |
| `fanatics` | Fanatics |
| `hardrockbet` | Hard Rock Bet |
| `espnbet` | theScore Bet |

The names and keys follow [The Odds API bookmaker catalog](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html). The provider warns that its public Pinnacle feed may be delayed. BetOnline agreement is a cross-check, not a guarantee of accuracy.

The 9 unique books count as one bookmaker group under The Odds API's current quota rules. One NFL, NCAAF, and MLB cycle requesting all three markets costs 9 credits when all three responses contain events. Five-minute polling around the clock would use 77,760 credits per 30 days before any one-minute windows. Responses with no events cost no credits, and actual usage is reported by the provider. Adding an eleventh book doubles the bookmaker component of the request cost. The worker records `x-requests-remaining`, `x-requests-used`, and `x-requests-last` on every successfully persisted poll. Review the provider's [current quota rules](https://the-odds-api.com/liveapi/guides/v4/) before enabling it.

## Select sports

In `.env`, set `ODDS_API_SPORTS` to the exact leagues you want to request. This replaces the whole selection and applies to both EV and no-sweat modes.

Currently supported values:

| Value | League |
| --- | --- |
| `americanfootball_nfl` | NFL |
| `americanfootball_ncaaf` | College football, NCAAF FBS |
| `baseball_mlb` | MLB |

Use one value or combine values with commas. [SportCatalog.cs](src/ABOdds/Domain/SportCatalog.cs) defines the complete list accepted by this application. Other Odds API sport keys require application support before they can be selected here.

```dotenv
# All supported sports, the default
ODDS_API_SPORTS=americanfootball_nfl,americanfootball_ncaaf,baseball_mlb

# MLB only, use this instead of the line above
ODDS_API_SPORTS=baseball_mlb
```

Any nonempty subset is accepted. Keys are case-insensitive and surrounding spaces are ignored. Empty lists, empty entries, duplicates, and unsupported keys fail startup before any API request. To stop all polling, use `ODDS_API_ENABLED=false`. Changes take effect after recreating the worker with `docker compose up --build -d worker`.

Compose maps this setting to `OddsApi:EnabledSports`. Direct .NET launches use `OddsApi__EnabledSports` or `--OddsApi:EnabledSports=baseball_mlb`; they do not load `.env`. This scalar setting replaces the old `OddsApi:Sports` indexed array, avoiding .NET array merging that could leave unwanted default leagues enabled. Migrate any custom `OddsApi__Sports__0` overrides to the new setting.

Each selected sport uses one shared request path, normalizer, database schema, and calculation pipeline. MLB uses `baseball_mlb` with `h2h`, `spreads` for run lines, and `totals`, as documented in [The Odds API MLB guide](https://the-odds-api.com/sports/mlb-odds.html). Only full-game pregame markets are supported. Player props, innings markets, and futures are excluded. Distinct provider event IDs keep doubleheader games separate.

To add another league with the same market rules, add its API sport key, display name, and spread label in [SportCatalog.cs](src/ABOdds/Domain/SportCatalog.cs), add representative response and pipeline tests, then include its key in `ODDS_API_SPORTS`. Update the supported-values table above when adding a league. Registration does not automatically enable a new league. No separate client or worker is needed. Sports with different outcomes or settlement rules, such as three-way moneylines, require calculation support first. Verify reference-book coverage and matching settlement rules for each new sport.

With nine books, EV mode estimates three credits per selected sport per poll; no-sweat mode estimates two. Removing a league stops new requests for it after restart. In EV mode, existing paid snapshots and queued alerts still follow normal recovery and expiry rules.

## Run with Docker

Prerequisites are Docker Desktop, an Odds API key, and a Discord webhook.

```bash
cp .env.example .env
```

Replace the placeholders in `.env`. Keep Discord disabled for the first smoke test:

```bash
docker compose build worker
docker compose up -d postgres
docker compose run --rm worker --once
```

`--once` enables odds fetching, forces Discord delivery off, and makes at most one request per configured league. It prints the request count and estimated per-request credits before fetching. In EV mode, the default nine books and three markets mean at most three requests, normally nine credits total. It stores snapshots, calculations, and pending alerts, then exits. In no-sweat mode, it prints the cash optimization reports without database writes; the default seven books and two markets normally cost six credits total across the three default sports. There are no automatic HTTP retries. Empty responses can cost less. This command uses a real API key and is not a free offline test.

After the smoke test succeeds and you review its logs, enable both integrations and choose `MAXIMUM_CREDITS_PER_RUN` for continuous operation:

```bash
docker compose up --build -d
docker compose logs -f worker
```

In EV mode, the application applies EF Core migrations during startup. PostgreSQL listens only on `127.0.0.1:${POSTGRES_PORT:-5432}` and stores data in the `postgres-data` volume. Set `POSTGRES_PORT` if port 5432 is already in use.

The worker stops with a nonzero exit code on a provider, persistence, or processing failure. Its Compose service intentionally does not restart automatically: inspect the error and fix it before restarting, so a persistent failure cannot drain paid credits. PostgreSQL retains its automatic restart policy. In EV mode, a worker restart processes existing unfinished batches before fetching anything new.

`Polling:MaximumCreditsPerRun` is an optional per-process cap, exposed as `MAXIMUM_CREDITS_PER_RUN` in Compose. The worker checks the estimated next-request cost against the remaining cap and reported provider balance. Missing quota headers stop further requests after saving the response. Quota is logged before deserialization or database writes. A blank cap means no configured per-run ceiling; the provider-balance guard still applies. The cap resets on process restart and is not a monthly account budget. Other applications using the same key can spend credits independently.

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

Direct `dotnet run` does not load `.env`. Configuration uses standard .NET keys. Environment variables use double underscores, for example:

```bash
OddsApi__Enabled=true
OddsApi__ApiKey=replace-me
OddsApi__EnabledSports=baseball_mlb
Discord__Enabled=true
Discord__WebhookUrl=https://discord.com/api/webhooks/replace-me
NoSweat__Enabled=true
NoSweat__Stage=Qualifying
```

Run the checks:

```bash
dotnet restore ABOdds.slnx
dotnet build ABOdds.slnx --no-restore
dotnet test ABOdds.slnx --no-build --no-restore
docker compose config
```

Real-Postgres regression tests require `ABODDS_TEST_POSTGRES`, a connection string for a dedicated test server whose user can create databases. Each test creates and deletes its own randomly named `abodds_test_*` database, using the application's database retry configuration and migrations. The tests explicitly report skipped when this variable is absent; a unit-only test run is not the release check. No test calls The Odds API or Discord.

Run the full release check with Docker and the .NET SDK installed:

```bash
bash scripts/test.sh
```

The script starts a temporary PostgreSQL server on an available localhost port, runs all tests, and removes that server afterward. Application-host tests use the real startup registration, migrations, repositories, workers, and HTTP adapters. Only external HTTP responses are substituted. Settings load from the application output directory, so launching from another working directory does not lose `appsettings.json`.

## EV stored data

- `poll_batches` records each successfully persisted provider response, observation time, quota headers, processing status, and immutable event metadata captured at that observation.
- `events` stores provider event IDs, teams, sport, and kickoff time.
- `markets` identifies the event, market type, and pregame period.
- `odds_snapshots` stores every normalized bookmaker outcome with both local observation time and provider source-update time.
- `fair_values` stores the selected no-vig probability, fair price, line, reference count, source breakdown, and calculation-method version.
- `ev_opportunities` stores qualifying target prices and links each one to its quote and fair value.
- `alert_states` stores the structured dedup key and active, last-seen, last-alerted, and disappearance state.
- `alerts` is both alert history and the Discord delivery outbox.

This is enough to add CLV without changing historical quote collection. The next slice should select the last fresh pregame fair value before kickoff, preserve exact-line matching, and compare it with the alerted price. If the original line has no closing fair value, leave CLV unpriced instead of interpolating one.

Historical calculations use the matchup and kickoff captured in `poll_batches.EventMetadataJson`, not the mutable latest event row. Calculation, detection, stage-completion, and alert-creation timestamps record actual processing time. Observation timestamps remain separate and drive freshness and deduplication. `alert_states.LastAlertedAtUtc` identifies the observation underlying the latest queued baseline; `alerts.SentAtUtc` records delivery. Delivery still checks the latest known kickoff as a safety guard.

For databases created before this change, migrations preserve existing data. Older batches have null event metadata and older fair values carry `legacy-unknown` as their method version. Their original kickoff metadata and actual processing times cannot be reconstructed; exclude them from analyses requiring that provenance.

## Fair-value calculation

For each complete, fresh reference-book market:

```text
implied probability = 1 / decimal odds
no-vig probability = implied probability / sum of all outcome probabilities
fair probability = Pinnacle no-vig probability, or BetOnline if Pinnacle is unavailable
fair odds = 1 / fair probability
EV = fair probability * target decimal odds - 1
reference EV difference = abs(Pinnacle probability - BetOnline probability) * target decimal odds
```

When both references are available for the same selection, exact line, and outcome set, BetOnline validates Pinnacle without changing its fair probability. Reject a target opportunity when the absolute EV difference exceeds `Ev:MaximumReferenceEvDifference`, default `0.03`, or 3 percentage points. Equality passes. This is an EV difference at the target price, not a probability difference. For example, probabilities of 55% and 53.5% differ by 3 EV points at decimal odds 2.00, but 4.5 points at 3.00.

If BetOnline is missing, stale, incomplete, or has a different line or outcome set, send qualifying Pinnacle opportunities marked `UNVALIDATED`. If Pinnacle is unavailable for that selection and exact line, use BetOnline no-vig and mark confidence `LOWER`. If neither reference has a complete fresh market, produce no fair value. A `+3.5` spread never validates a `+3` spread, and a total of `45.5` never validates `46`.

Source roles are persisted with the reference prices and probabilities. New fair values use calculation version `pinnacle-devig-betonline-validation-v2`; historical calculations retain their original version and weights. No database migration is required for the new JSON source roles.

Whole-number spreads/totals and football moneylines are retained. Their alerts label EV and fair probability as **conditional on no push**. The configured EV threshold and re-alert improvement apply to those conditional values. Two prices cannot establish push probability, so the app does not estimate it. If the push probability is `q`, unconditional stake EV is `(1 - q) * conditional EV`. For example, conditional EV of 3.1% with a hypothetical 10% push probability is 2.79% stake EV. Moneyline comparisons assume matching two-way settlement with stakes returned on a tie; verify the book's settlement rules before acting. Half-point spreads and totals keep the ordinary EV label.
