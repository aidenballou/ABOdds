# ABOdds readiness audit

Audited September 4, 2026, against commit `fbcdcc7` and the requested small, pregame-only V1. Original verdict: **not ready for a paid API key**. At that commit, the main persistence path failed under the application's configured database retry strategy. The remediation status below supersedes that verdict.

Scope: code review, existing tests, official provider documentation, and fault probes against a disposable PostgreSQL 17 database using the actual application repositories and Discord worker. All odds and webhook responses were synthetic. No paid API calls, real Discord messages, or changes to existing databases were made.

At audit time, only BetRivers removal and documentation had changed. The findings and code locations below the remediation section preserve that original review; they describe the original commit, not the repaired implementation.

## Remediation progress

Updated September 5, 2026: findings were addressed sequentially, with regression checks before advancing. All eight findings, the two-worker simplification, and the historical-data and configuration concerns are resolved. The next step is a bounded live smoke test, not unattended polling.

| Finding | Current status | Verification |
| --- | --- | --- |
| 1. Persistence | Fixed | Production retry configuration; PostgreSQL pipeline persistence, replay, and atomic rollback |
| 2. Missing game in totals | Fixed | Separate matchups, market names, and kickoff timestamps in formatted alerts |
| 3. Reappearance suppression | Fixed | Failed and expired reappearance alerts get replacements without repeated alerts |
| 4. Discord lock and deadlines | Fixed | Reconciliation proceeds during HTTP; source, kickoff, and request deadlines cancel blocked sends |
| 5. Webhook rate limit | Fixed | Global webhook cooldown persists across new alerts and worker restart |
| 6. Push-capable EV | Fixed per user decision | Whole-number spreads/totals and moneylines retained; EV and fair probability labeled conditional on no push |
| 7. Paid-request safeguards | Fixed | Pregame request filter, per-league cadence, bounded `--once`, credit cap/balance checks, quota logging, and stop-on-failure regressions |
| 8. Deployed-pipeline coverage | Fixed | Actual application host, production database retry settings, migrations, repositories, and both workers tested with synthetic HTTP |
| Stage coordination | Simplified | Two workers; sequential processing with durable replay before further fetching |
| Historical provenance | Fixed for new data | Immutable observed matchup/kickoff, separate processing timestamps, calculation version, and additive migrations |
| Configuration | Fixed | Duplicate/invalid inputs, reference-target overlap, quorum, and ten-book boundary rejected at startup |
| Provider body timeout | Fixed during final review | Request deadline also cancels a stalled response body; regression test covers cancellation |

Latest verification:

- `bash scripts/test.sh`: 90 passed, zero skipped, including disposable PostgreSQL regressions and application-host tests.
- EF Core reports no pending model changes. Existing data is preserved by the new migrations; unavailable legacy provenance is documented in the README.
- Docker image builds; a disposable runtime starts, applies all migrations, and reports `Application started` with both integrations disabled.
- Compose configuration validates. NuGet reports no known vulnerable direct or transitive packages in the current dependency set.
- No paid API calls, real Discord messages, or changes to existing user databases were made.

Use the [README smoke-test instructions](../README.md#run-with-docker) for the first live request. Live entitlement, account-specific reference coverage, and real Discord delivery remain unverified. Reference weights remain provisional; these checks establish software behavior, not a profitable edge. Actual CLV processing remains deferred.

## Credits: remove the eleventh book

The provider bills explicit bookmakers in groups of ten. The original three references plus eight targets crossed into a second group; removing BetRivers leaves ten. The actual HTTP request builder was exercised with current appsettings and a recording HTTP handler: ten bookmaker keys, no BetRivers. Weights remain 50/30/20. See [The Odds API quota documentation](https://the-odds-api.com/liveapi/guides/v4/).

| Both sports, three markets | Before: 11 books | Now: 10 books |
| --- | ---: | ---: |
| One cycle | 12 credits | 6 credits |
| Five-minute polling, per hour | 144 | 72 |
| One-minute polling, per hour | 720 | 360 |
| Five-minute polling, 30 days | 103,680 | 51,840 |

These are cadence estimates assuming nonempty responses throughout; HTTP/processing time slightly reduces actual frequency. Responses without events are free. Each hour switched from five-minute to one-minute polling adds roughly 288 credits with ten books. These are quota credits, not dollar charges.

## Findings requiring attention before live testing

### 1. P1: database retries and manual transactions are incompatible

Evidence: `src/ABOdds/Program.cs:45`, `Infrastructure/Persistence/OddsIngestionRepository.cs:11`, `CalculationRepository.cs:81` and `:147`, and `AlertRepository.cs:29`.

The application enables Npgsql's retrying execution strategy, but all four repository write paths begin their own transactions without an execution-strategy wrapper. Each failed against PostgreSQL with `InvalidOperationException` identifying `NpgsqlRetryingExecutionStrategy` and user-initiated transactions. The first ingestion attempt left **zero poll batches and zero quotes**. Startup migrations can succeed despite this error. This is consistent with [EF Core's documented transaction requirements](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions).

The poller fetches paid odds before saving them, catches the database exception, and continues polling. Therefore, enabling a key can consume credits while storing nothing and producing no alerts. Quota logging also happens after the failing save.

Fix the transaction boundary first. These methods currently use a single `SaveChangesAsync`; evaluate whether its implicit transaction suffices for the single-writer design. Where explicit transactions remain necessary, retry the complete unit with stable identities and verify replay behavior. Do not just suppress the exception or retry the paid HTTP request.

Verification: use the production database configuration, migrate a real PostgreSQL instance, and persist a synthetic poll through fair values, opportunities, outbox, and delivery. Test replay and database failure as well as the happy path.

### 2. P2: total alerts do not identify the game

Evidence: `src/ABOdds/Services/Alerts/DiscordAlertFormatter.cs:14`.

A generated alert began `Over 44.5`, followed by FanDuel odds, fair value, and reference books. Neither team, sport, nor kickoff appeared anywhere. An NCAAF total is not actionable without its game. The opportunity already contains the missing information; no extra provider request or schema is needed.

Include the matchup, market, and kickoff in every message. Test totals from two different games, not merely that the string contains “Over”.

### 3. P2: failed reappearance alerts can be permanently suppressed

Evidence: `src/ABOdds/Infrastructure/Persistence/AlertRepository.cs:86`, `:111`, and `:263`.

Reproduced sequence: deliver an opportunity; let it disappear; let it reappear at the same EV; fail the reappearance alert; process another fresh qualifying poll. One reappearance alert was created, but **zero replacement alerts** were created after failure.

Failure handling restores the older delivered EV baseline while leaving the opportunity active. The next poll consequently looks like an already-alerted, unchanged opportunity. Expiration uses the same baseline-restoration routine. An alert from a previous appearance should not count as delivery for the current appearance.

Keep the distinction between currently qualifying, queued, and delivered for the current appearance. Test failures and expiration after a prior successful alert, not just the first-ever alert.

### 4. P2: Discord HTTP blocks alert-state reconciliation

Evidence: `src/ABOdds/Workers/DiscordAlertWorker.cs:70` and `:127`; `Workers/AlertRuleWorker.cs:43`.

The shared semaphore stays locked across the webhook HTTP call. The alert-rule worker needs the same semaphore. A blocked fake webhook confirmed that reconciliation could not acquire it. Discord's HTTP client also has no application-specific timeout; it inherits the normal 100-second HttpClient default, longer than the configured 90-second source-age limit.

A slow webhook can delay both sports' state updates while odds age or kickoff passes. The pre-send freshness check does not bound an in-flight request. The README's claim that serialization prevents every superseded post is too strong: newer snapshots can already exist upstream while reconciliation waits.

Use short state transitions, release synchronization before network I/O, and bound the send attempt by a short timeout and validity deadline. Preserve the durable outbox. Document that a crash after Discord accepts a message but before marking it sent can cause a duplicate; the current design does not provide exactly-once delivery.

### 5. P2: a Discord rate limit pauses one message, not the webhook

Evidence: `src/ABOdds/Workers/DiscordAlertWorker.cs:135` and `:181`; `Infrastructure/Persistence/AlertRepository.cs:148`.

With two pending opportunities and a fake webhook returning HTTP 429 with a 60-second retry delay, the worker sent the next message approximately **4 milliseconds later**. Only the first row's next-attempt time was delayed. The loop immediately selected another pending row for the same webhook.

Respect the cooldown across this one webhook's entire delivery loop. No general-purpose distributed rate limiter is needed. See [Discord's rate-limit requirements](https://docs.discord.com/developers/topics/rate-limits).

### 6. P2: integer-line EV is conditional on no push, but not labeled that way

Evidence: `src/ABOdds/Services/Calculations/FairValueCalculator.cs:76`; `EvScanner.cs:61`.

The two-outcome devig formula normalizes probabilities to one. For a market where a push returns the stake, those are probabilities conditional on a non-push result. The scanner presents `p × decimalOdds − 1` as stake EV without a push probability.

Mathematically, with push probability `q`, stake EV is `(1 − q) × (p × decimalOdds − 1)`. For example, a conditional edge of 3.1% with a hypothetical 10% push probability is 2.79% stake EV, below the configured 3% threshold. This does not reverse the edge's sign for otherwise matching settlement terms, but it changes its magnitude and the meaning of “fair probability”.

Make the interpretation explicit before trusting thresholds. A small V1 can label push-capable prices as conditional estimates, or exclude whole-number spreads/totals from strict-ROI alerts while still storing them. Two-way football moneylines also need consistent tie/void settlement assumptions. Do not invent a push probability or build a prediction platform to conceal this limitation.

### 7. P2: paid-request safeguards are incomplete

Evidence: `src/ABOdds/Providers/TheOddsApiClient.cs:98`; `Workers/OddsPollingWorker.cs:58` and `:87`.

The recorded request lacks `commenceTimeFrom`. Started games are discarded only after fetching; the provider supports filtering them out in the request. A live-only response can therefore incur cost for data V1 immediately discards. Add the upstream pregame filter and retain the local safety check.

Cadence is global: an approaching NFL game makes NCAAF poll every minute too, even if all college games are days away. This matches the README's “any known event” rule, so it is a cost tradeoff rather than an undisclosed implementation mismatch. Per-sport due times are a small, worthwhile refinement.

There is no bounded single-cycle test mode, usage ceiling, or pause on persistent downstream failure. Before using the paid key, add a finite smoke-test path and stop further paid fetching when processing cannot persist/progress. Report quota immediately after the response, independently of downstream saves. Start live verification with one bounded cycle, not unattended polling.

### 8. P2: the test suite does not cover the deployed pipeline

All **42 existing tests pass**. However, `tests/ABOdds.Tests/Pipeline/PipelineEndToEndTests.cs:13` manually constructs IDs and passes in-memory objects between functions. It does not execute PostgreSQL repositories, hosted workers, or a durable Discord outbox. There are no existing real-database tests. Passing these tests or booting a container does not establish live readiness.

The audit first used the application's retry settings and reproduced finding 1. To inspect the remaining paths, a diagnostic-only factory without database retries was used; production code was not changed. That diagnostic persisted seven quotes, two fair values, one opportunity, and one alert. It then reproduced findings 3–5. This downstream success is not evidence that production works.

## Architecture judgment

Keep .NET, EF Core, PostgreSQL, decimal arithmetic, exact-line matching, complete-market devig, configurable reference weights, and the durable alert outbox. Those choices fit the requested product. The history tables serve the CLV requirement; deleting them to reduce table count would be counterproductive. There is no giant analytics platform here.

The avoidable complexity is stage coordination: four channels, three recovering calculation/reconciliation workers, repeated database hydration, completion markers, and cross-worker locking. For two leagues polled once a minute at fastest, fair-value and EV arithmetic do not need independent background execution.

My preferred simplification is **two long-running workers**: one polling/processing loop with ordinary normalization, fair-value, EV, and alert-rule functions; one Discord outbox loop. Persist the raw normalized batch first and preserve a small, explicit replay boundary for processing it. Commit derived results and queued alerts consistently. A crash must not erase a paid snapshot or create duplicate alerts on replay.

This is a recommendation, not a prerequisite rewrite. Fixing the current design and proving it with integration tests is also viable. No broker, microservices, CQRS framework, prediction model, dashboard, or CLV worker is needed now.

## CLV storage and lower-priority concerns

The core links are present: provider event ID, exact selection/line, target snapshot, provider and local timestamps, reference prices/probabilities/weights, consensus, and alert delivery history. Actual CLV processing is correctly deferred.

There are two historical-data caveats worth addressing before accumulating paid history. Event kickoff is overwritten on each poll (`OddsIngestionRepository.cs:50`), so historical quotes cannot reconstruct the kickoff known at observation time after a reschedule. Calculation/detection/completion timestamps are also assigned the batch observation time, hiding actual processing delay. Preserve observed event metadata and distinguish “as of” from “processed at”; a small calculation-version identifier would make later comparisons auditable. These do not require a CLV engine.

Configuration validation filters out invalid reference entries before checking them, accepts duplicate sports, and does not reject reference/target overlap. Defaults are valid, but future edits could silently change weighting, duplicate paid requests, or compare a target against a consensus containing itself. Validate the normalized configuration actually used, including the ten-book quota boundary.

The provisional references are not evidence of a real edge. The provider explicitly warns about potential delay in its public Pinnacle feed; two available book names do not establish two independent price signals. Keep the requested weights provisional and measure coverage, then CLV. See the [provider bookmaker catalog](https://the-odds-api.com/sports-odds-data/bookmaker-apis.html). No live entitlement or account-specific book coverage was verified.

## Release check

Before a paid key is enabled, require a real-Postgres test run with production service configuration and mocked external HTTP covering:

1. Full poll-to-delivered-alert persistence and replay without duplication.
2. No alert below threshold, without quorum, for stale prices, after kickoff, or at a mismatched line.
3. Reappearance after a delivered alert, followed by failure/expiration and fresh replacement.
4. Slow webhook, webhook-wide 429 cooldown, and restart with pending alerts.
5. Exactly ten requested books, upstream pregame filtering, and no repeated paid fetch while persistence is broken.
6. A finite live smoke test whose request count and credit expectation are printed before execution.

Audit harness: `/private/tmp/abodds-audit.PXyg9Z/Program.cs` and `Audit.csproj`. It is temporary diagnostic code, not part of the app or its passing test suite. It uses a disposable local database and fake HTTP clients; the database container was removed after verification.
