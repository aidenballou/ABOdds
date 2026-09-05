# Code and test audit

Reviewed all authored production code, all test files and fixtures, persistence mappings and migrations, configuration, build files, and the Docker test script on September 5, 2026. The checkout was clean before the audit. The baseline passed 118 tests with no skips.

The architecture fits this V1: one application, a polling and calculation worker, a Discord delivery worker, and PostgreSQL history and outbox storage. The cleanup removes unused machinery without changing the configured books, calculations, alert policy, or database schema.

## Changes

- Removed the second alert tracking model. Production discarded the decision service's returned state and maintained database state separately. The service now returns only an alert reason. Bet identity belongs to the opportunity, without a separate factory.
- Made the stateless odds normalizer a static function. Its interface had one implementation and no test substitutes.
- Consolidated identical reference and target book option types. Removed unused display-name settings; actual quote and alert titles already come from the provider.
- Kept API configuration validation at the host's options boundary. Removed duplicate client-side checks and silent configuration filtering. Invalid-market coverage now exercises bound options and actual startup before any HTTP request.
- Removed unused DTO fields, batch metadata projections, line-key properties, and stage-save return flags.
- Removed pre-save deletion queries for partial fair-value and EV rows. Each stage writes its rows and completion marker in one `SaveChangesAsync` transaction. Replay preserves committed rows and timestamps; rollback tests cover failed writes.
- Replaced lists of matching fair values with a single lookup. The database already enforces one value per batch, market, selection, and exact line.
- Removed an impossible nonpositive implied-probability branch after complete outcomes with odds greater than one have been selected.
- Removed pass-through delay methods and the disabled Discord worker's infinite wait.

## Test decisions

Every remaining test has a behavioral purpose. The review checked its inputs, assertions, failure condition, overlap with other tests, and use of the real application path. Helpers that construct database stages remain useful for testing partial processing and recovery.

| Test file | Purpose and audit result |
| --- | --- |
| `AlertDecisionServiceTests` | Replaced tests of unused state construction and impossible cross-bet input with decision cases for new, reappeared, unchanged, decreased, below-boundary, exact-boundary, and configured-improvement behavior. |
| `AlertRepositoryTests` | Retained failed/expired reappearance regressions. Added persisted last-seen versus last-alerted baseline checks and independent event/line deduplication. |
| `ApplicationHostTests` | Retained real registration, startup, polling, outbox recovery, and HTTP-adapter coverage. Strengthened delivered-message and alert-reason assertions; added invalid configuration stopping startup before requests. |
| `DiscordWorkerTests` | Retained HTTP cancellation and lock-release tests. Replaced 100 ms restart sleeps that could not reach a second two-second poll with persisted cooldown boundary checks through a fresh repository. Added permanent failure, server-error retry, and queued-alert expiry coverage. |
| `HistoryTests` | Retained reschedule and observation-time provenance checks. Added nonempty result assertions and actual calculation/detection timestamp checks. |
| `PollingWorkerTests` | Retained paid-request limits, ingestion/processing failures, replay-before-fetch, and missing-quota behavior. These failure paths are distinct from the host happy path. |
| `RepositoryTests` | Retained transaction rollback, replay, source-role persistence, and decimal-boundary checks. Replaced return-flag assertions with stored row/timestamp assertions and removed a LowVig absence check whose input never contained LowVig. |
| `FairValueCalculatorTests` | Retained no-vig math, reference roles, freshness, pregame, and complete-market checks. Prevented the source-freshness test from passing on empty output. Added invalid spread and total pair cases. |
| `EvScannerTests` | Retained target selection, thresholds, freshness, validation, and three-way rejection. Extended exact-line coverage to totals and opposite spread signs, with a positive matching-line control. |
| `DiscordAlertFormatterTests` | Retained customer-visible formatting and push/confidence disclosures. Removed duplicate substring assertions and a separate fake team-total test; the real Over-total case covers unsigned formatting. Added no-push-disclosure assertions for half-point markets. |
| `DiscordWebhookClientTests` | Retained request payload, mention suppression, confirmation query, status handling, retry-header parsing, and exhausted-bucket parsing. These verify HTTP translation used by the worker. |
| `OddsNormalizerTests` | Retained source timestamp precedence, selection normalization, kickoff filtering, and unsupported/invalid quote filtering. Updated calls to the static function. |
| `TheOddsApiClientTests` | Retained actual request construction, quota capture/logging, and stalled-body cancellation. Moved unsupported-market validation to host configuration tests and removed invalid overlapping books from the normal request fixture. |
| `AdaptivePollingScheduleTests` | Retained separate sport schedules, six-hour boundary behavior, wakeup on entry into that window, and empty-event cadence. |
| `ConfigurationTests` | Retained unsafe configuration and default-book checks. Added the validation cases removed from the HTTP client. |
| `PipelineEndToEndTests` | Deleted. It manually rebuilt production mappings and called functions in order, bypassing the actual pipeline, database, workers, and host. Real-host tests now assert its useful output contract. |

Shared calculation and alert fixtures now use primary/validation roles instead of obsolete weighted-consensus defaults. No tests call the real Odds API or Discord.

## Verification

- Final Release build and full suite: 133 passed, zero failed, zero skipped, including disposable PostgreSQL tests. There are 84 test methods across 15 test classes; theories account for the additional cases.
- Command: `bash scripts/test.sh -c Release --collect:'XPlat Code Coverage' --results-directory /private/tmp/abodds-audit-final`.
- Four temporary mutation probes were rejected by the intended tests: changing the re-alert boundary from inclusive to exclusive, comparing against last-seen EV instead of last-alerted EV, discarding spread-line signs, and bypassing the persisted cooldown. Every mutation was restored before the final Release run. These probes are targeted evidence, not an exhaustive mutation analysis.
- `git diff --check` passed.
- `dotnet format ABOdds.slnx --no-restore --verify-no-changes` reported pre-existing initializer formatting in `CalculationRepository`, `FairValueCalculator`, `EvScannerTests`, and `RepositoryTests`. The flagged expressions were verified against `HEAD` and left unchanged.

## Retained deliberately

The history tables, observation metadata, stage completion markers, legacy source weights/roles, and EF migrations support replay and interpretation of stored data. The single shared semaphore coordinates reconciliation with delivery-state updates. Provider and webhook interfaces allow controlled external I/O in worker tests. These are not unused abstractions.

Historical migrations and older stage indexes were left intact. Removing schema history or adding a migration solely to trim old indexes would add churn without simplifying the current execution path. The test dependency set remains useful, including the coverage collector used in this audit.
