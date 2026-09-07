using ABOdds;
using ABOdds.Workers;
using ABOdds.NoSweat;

var builder = ApplicationHost.CreateBuilder(args);
using var host = builder.Build();
var poller = host.Services.GetService<OddsPollingWorker>();
var noSweat = host.Services.GetService<NoSweatWorker>();
await host.RunAsync();
return noSweat?.ExitCode ?? poller!.ExitCode;
