using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabotSlp.Api;
using RabotSlp.Cli;
using RabotSlp.Configuration;
using RabotSlp.Data;
using RabotSlp.Jobs;

namespace RabotSlp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CliArguments cli;
        try
        {
            cli = CliArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliArguments.Usage);
            return 2;
        }

        if (cli.Command == CliCommand.Help)
        {
            Console.WriteLine(CliArguments.Usage);
            return 0;
        }

        // The content root is the folder of the executable, so appsettings.json is found
        // no matter which directory the service is started from.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.Configure<SlpApiOptions>(builder.Configuration.GetSection(SlpApiOptions.SectionName));
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
        builder.Services.Configure<SlpOptions>(builder.Configuration.GetSection(SlpOptions.SectionName));

        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddDbContext<SlpDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            DatabaseConfiguration.Configure(options, database);
        });

        builder.Services
            .AddHttpClient<ISlpApiClient, SlpApiClient>((serviceProvider, http) =>
            {
                var api = serviceProvider.GetRequiredService<IOptions<SlpApiOptions>>().Value;
                http.BaseAddress = new Uri(api.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(api.TimeoutSeconds);
            })
            .AddHttpMessageHandler(serviceProvider =>
            {
                var api = serviceProvider.GetRequiredService<IOptions<SlpApiOptions>>().Value;
                return new RetryHandler(
                    api.MaxRetries,
                    TimeSpan.FromSeconds(api.DefaultRetryDelaySeconds),
                    serviceProvider.GetRequiredService<ILogger<RetryHandler>>());
            });

        builder.Services.AddScoped<ProfileSyncJob>();
        builder.Services.AddScoped<ApplyProfileJob>();

        using var host = builder.Build();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;

            var defaults = services.GetRequiredService<IOptions<SlpOptions>>().Value;
            var clock = services.GetRequiredService<TimeProvider>();

            var profileId = cli.ProfileId ?? defaults.ProfileId;
            var year = cli.Year ?? defaults.Year ?? clock.GetUtcNow().Year;

            // Creates the tables on first use. See the README for the migration note.
            await services.GetRequiredService<SlpDbContext>()
                .Database.EnsureCreatedAsync(cancellation.Token);

            if (cli.Command == CliCommand.Sync)
            {
                var result = await services.GetRequiredService<ProfileSyncJob>()
                    .RunAsync(profileId, year, cancellation.Token);

                Console.WriteLine(
                    $"Profile {result.ProfileId} {result.Year}: {result.Fetched} intervals fetched, " +
                    $"{result.Inserted} inserted, {result.Updated} updated, {result.Unchanged} unchanged, {result.Deleted} removed.");

                if (result.MissingIntervals.Count > 0)
                {
                    Console.WriteLine(
                        $"Warning: the API returned no value for {result.MissingIntervals.Count} interval(s), " +
                        $"for example {result.MissingIntervals[0]:yyyy-MM-dd'T'HH:mm:ss'Z'}.");
                }
            }
            else
            {
                var annualKwh = cli.AnnualKwh ?? defaults.AnnualKwh
                    ?? throw new ArgumentException("The annual consumption is required. Pass --annual-kwh <kWh>.");

                var result = await services.GetRequiredService<ApplyProfileJob>().RunAsync(
                    cli.CustomerId ?? defaults.CustomerId,
                    profileId,
                    year,
                    annualKwh,
                    cli.Basis ?? defaults.Basis,
                    cancellation.Token);

                Console.WriteLine(
                    $"Customer {result.CustomerId}, profile {result.ProfileId} {result.Year}: {result.IntervalCount} intervals saved, " +
                    $"{result.TotalKwh} kWh in total for {result.AnnualKwh} kWh per year (factor {result.ScalingFactor}).");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is SlpApiException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
