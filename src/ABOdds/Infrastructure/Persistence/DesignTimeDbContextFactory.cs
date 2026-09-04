using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ABOdds.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BettingDbContext>
{
    public BettingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=abodds;Username=abodds;Password=abodds";
        var options = new DbContextOptionsBuilder<BettingDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new BettingDbContext(options);
    }
}
