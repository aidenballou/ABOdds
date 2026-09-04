using System.Threading.Channels;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Time;

namespace ABOdds.Workers;

public abstract class RecoveringBatchWorker(
    ChannelReader<Guid> workReader,
    CalculationRepository repository,
    IClock clock,
    ILogger logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Guid, Exception?> LogStageFailure =
        LoggerMessage.Define<string, Guid>(
            LogLevel.Error,
            new EventId(1001, nameof(LogStageFailure)),
            "Pipeline stage {Stage} failed for batch {BatchId}");

    private static readonly Action<ILogger, string, Exception?> LogRecoveryFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1002, nameof(LogRecoveryFailure)),
            "Pipeline stage {Stage} could not query or record recovery state");

    protected abstract string StageName { get; }

    protected abstract Task<Guid?> GetNextPendingBatchAsync(CancellationToken cancellationToken);

    protected abstract Task ProcessBatchAsync(Guid batchId, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? batchId = null;
            try
            {
                batchId = workReader.TryRead(out var signaledBatchId)
                    ? signaledBatchId
                    : await GetNextPendingBatchAsync(stoppingToken);

                if (!batchId.HasValue)
                {
                    await WaitForSignalAsync(stoppingToken);
                    continue;
                }

                await ProcessBatchAsync(batchId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (batchId.HasValue)
                {
                    LogStageFailure(logger, StageName, batchId.Value, exception);
                    try
                    {
                        await repository.RecordFailureAsync(
                            batchId.Value,
                            StageName,
                            exception,
                            clock.UtcNow,
                            stoppingToken);
                    }
                    catch (Exception recoveryException) when (recoveryException is not OperationCanceledException)
                    {
                        LogRecoveryFailure(logger, StageName, recoveryException);
                    }
                }
                else
                {
                    LogRecoveryFailure(logger, StageName, exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task WaitForSignalAsync(CancellationToken stoppingToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        waitCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await workReader.WaitToReadAsync(waitCancellation.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The timeout causes a database rescan so a lost in-memory signal cannot stall a batch.
        }
    }
}
