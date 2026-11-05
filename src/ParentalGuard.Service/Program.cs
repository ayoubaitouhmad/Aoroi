using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentalGuard.Service;
using ParentalGuard.Service.Data;
using ParentalGuard.Service.Dns;
using ParentalGuard.Service.Ipc;
using ParentalGuard.Service.Workers;

// ParentalGuard Service — the protected "brain".
// Runs as a Windows Service under LocalSystem so a standard (child) user cannot stop it.
// During development you can also run it from a console: dotnet run --project ParentalGuard.Service

ServicePaths.EnsureCreated();

var builder = Host.CreateApplicationBuilder(args);

// Make it a proper Windows Service when installed; harmless when run as console.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ParentalGuard";
});

// --- Logging to a rolling file under ProgramData, plus the event log when a service. ---
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");

// --- Singletons: one DB connection and one tracker for the whole process lifetime. ---
builder.Services.AddSingleton(new UsageStore(ServicePaths.DatabaseFile));
builder.Services.AddSingleton(new ConfigStore(ServicePaths.ConfigFile));
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddSingleton<BlocklistStore>();
builder.Services.AddSingleton<DnsForwarder>();

// --- The hosted workers. IpcWorker serves the Agent; DnsWorker serves DNS lookups. ---
builder.Services.AddHostedService<IpcWorker>();
builder.Services.AddHostedService<DnsWorker>();

var host = builder.Build();

// Touch the config once at startup so a default policy.json is written on first run.
host.Services.GetRequiredService<ConfigStore>().Load();

host.Run();
