# ABOdds

ABOdds is one .NET worker application that polls pregame football odds, calculates exact-line Pinnacle no-vig fair values with BetOnline validation, finds positive expected value, and sends deduplicated Discord alerts. PostgreSQL stores every normalized quote and every downstream decision.

There is no HTTP API, UI, message broker, bet placement, player-prop support, or CLV calculation in this version.

The [code and test audit](docs/code-test-audit-2026-09-05.md) documents the cleanup and verification, including real PostgreSQL and application-host tests. The earlier [readiness audit](docs/readiness-audit-2026-09-04.md) records the deployment fixes. The Docker image builds and starts with both integrations disabled. The next step is the bounded live smoke test below; account-specific book coverage and live delivery have not been verified. Both integrations remain disabled by default.

## Pipeline

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

## V1 rules

- Sports: NFL and NCAAF FBS
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

The 9 unique books count as one bookmaker group under The Odds API's current quota rules. One NFL and NCAAF cycle requesting all three markets costs 6 credits when both responses contain events. Five-minute polling around the clock would use 51,840 credits per 30 days before any one-minute windows. Responses with no events cost no credits, and actual usage is reported by the provider. Adding an eleventh book doubles the bookmaker component of the request cost. The worker records `x-requests-remaining`, `x-requests-used`, and `x-requests-last` on every successfully persisted poll. Review the provider's [current quota rules](https://the-odds-api.com/liveapi/guides/v4/) before enabling it.

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

`--once` enables odds fetching, forces Discord delivery off, and makes at most one request per configured league. It prints the request count and estimated per-request credits before fetching. With the default nine books and three markets, that is at most two requests, normally six credits total. It stores snapshots, calculations, and pending alerts, then exits. There are no automatic HTTP retries. Empty responses can cost less. This command uses a real API key and is not a free offline test.

After the smoke test succeeds and you review its logs, enable both integrations and choose `MAXIMUM_CREDITS_PER_RUN` for continuous operation:

```bash
docker compose up --build -d
docker compose logs -f worker
```

The application applies EF Core migrations during startup. PostgreSQL listens only on `127.0.0.1:${POSTGRES_PORT:-5432}` and stores data in the `postgres-data` volume. Set `POSTGRES_PORT` if port 5432 is already in use.

The worker stops with a nonzero exit code on a provider, persistence, or processing failure. Its Compose service intentionally does not restart automatically: inspect the error and fix it before restarting, so a persistent failure cannot drain paid credits. PostgreSQL retains its automatic restart policy. On a worker restart, existing unfinished batches are processed before fetching anything new.

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

Real-Postgres regression tests require `ABODDS_TEST_POSTGRES`, a connection string for a dedicated test server whose user can create databases. Each test creates and deletes its own randomly named `abodds_test_*` database, using the application's database retry configuration and migrations. The tests explicitly report skipped when this variable is absent; a unit-only test run is not the release check. No test calls The Odds API or Discord.

Run the full release check with Docker and the .NET SDK installed:

```bash
bash scripts/test.sh
```

The script starts a temporary PostgreSQL server on an available localhost port, runs all tests, and removes that server afterward. Application-host tests use the real startup registration, migrations, repositories, workers, and HTTP adapters. Only external HTTP responses are substituted. Settings load from the application output directory, so launching from another working directory does not lose `appsettings.json`.

## Stored data

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
