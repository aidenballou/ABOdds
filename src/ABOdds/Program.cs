using ABOdds;
using ABOdds.Workers;

var builder = ApplicationHost.CreateBuilder(args);
using var host = builder.Build();
var poller = host.Services.GetRequiredService<OddsPollingWorker>();
await host.RunAsync();
return poller.ExitCode;
