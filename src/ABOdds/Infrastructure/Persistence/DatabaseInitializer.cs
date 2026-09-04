using Microsoft.EntityFrameworkCore;

namespace ABOdds.Infrastructure.Persistence;

public sealed partial class DatabaseInitializer(
    IDbContextFactory<BettingDbContext> contextFactory,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogApplyingMigrations(logger);
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);
        LogMigrationsCurrent(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations are current")]
    private static partial void LogMigrationsCurrent(ILogger logger);
}
