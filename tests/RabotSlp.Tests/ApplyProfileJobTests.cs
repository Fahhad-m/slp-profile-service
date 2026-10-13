using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RabotSlp.Jobs;

namespace RabotSlp.Tests;

public class ApplyProfileJobTests
{
    private const int FakeYearSize = 12 * 2 * 96;
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static async Task Sync(TestDb database, FakeSlpApi? api = null, string profile = "h1")
    {
        using var context = database.Create();
        var job = new ProfileSyncJob(api ?? new FakeSlpApi(), context, new FixedTimeProvider(Now), NullLogger<ProfileSyncJob>.Instance);
        await job.RunAsync(profile, 2026);
    }

    private static async Task<ApplyResult> Apply(
        TestDb database,
        decimal annualKwh,
        string customer = "fahad",
        string profile = "h1",
        NormalisationBasis basis = NormalisationBasis.StoredSum)
    {
        using var context = database.Create();
        var job = new ApplyProfileJob(context, new FixedTimeProvider(Now), NullLogger<ApplyProfileJob>.Instance);
        return await job.RunAsync(customer, profile, 2026, annualKwh, basis);
    }

    [Fact]
    public async Task Intervals_add_up_to_the_annual_consumption()
    {
        using var database = new TestDb();
        await Sync(database);

        var result = await Apply(database, 3500m);

        Assert.Equal(FakeYearSize, result.IntervalCount);
        Assert.True(Math.Abs(result.TotalKwh - 3500m) < 0.01m, $"total was {result.TotalKwh}");

        using var context = database.Create();
        var stored = await context.ForecastIntervals.ToListAsync();
        Assert.Equal(FakeYearSize, stored.Count);
        Assert.True(Math.Abs(stored.Sum(x => x.ConsumptionKwh) - 3500m) < 0.01m);
    }

    [Fact]
    public async Task Each_interval_is_the_profile_value_times_the_scaling_factor()
    {
        using var database = new TestDb();
        await Sync(database);

        await Apply(database, 3500m);

        using var context = database.Create();
        var profile = await context.ProfileValues.ToListAsync();
        var factor = Math.Round(3500m / profile.Sum(x => x.ValueKwh), 12, MidpointRounding.AwayFromZero);

        var timestamp = Utc.At(2026, 2, 1, 6, 45);
        var source = profile.Single(x => x.TimestampUtc == timestamp);
        var result = await context.ForecastIntervals.SingleAsync(x => x.TimestampUtc == timestamp);

        Assert.Equal(Math.Round(source.ValueKwh * factor, 6, MidpointRounding.AwayFromZero), result.ConsumptionKwh);
    }

    [Fact]
    public async Task Nominal_basis_divides_by_one_million()
    {
        using var database = new TestDb();
        await Sync(database);

        var result = await Apply(database, 3500m, basis: NormalisationBasis.Nominal);

        Assert.Equal(0.0035m, result.ScalingFactor);

        using var context = database.Create();
        var timestamp = Utc.At(2026, 1, 1);
        var source = await context.ProfileValues.SingleAsync(x => x.TimestampUtc == timestamp);
        var forecast = await context.ForecastIntervals.SingleAsync(x => x.TimestampUtc == timestamp);
        Assert.Equal(Math.Round(source.ValueKwh * 0.0035m, 6, MidpointRounding.AwayFromZero), forecast.ConsumptionKwh);
    }

    [Fact]
    public async Task Running_again_replaces_the_previous_result()
    {
        using var database = new TestDb();
        await Sync(database);

        await Apply(database, 3500m);
        await Apply(database, 4000m);

        using var context = database.Create();
        var forecast = await context.Forecasts.SingleAsync();
        Assert.Equal(4000m, forecast.AnnualKwh);
        Assert.Equal(Now.UtcDateTime, forecast.CalculatedAtUtc);
        Assert.Equal(FakeYearSize, await context.ForecastIntervals.CountAsync());
        Assert.True(Math.Abs(await SumOf(context) - 4000m) < 0.01m);
    }

    [Fact]
    public async Task Customers_do_not_affect_each_other()
    {
        using var database = new TestDb();
        await Sync(database);

        await Apply(database, 3500m, customer: "anna");
        await Apply(database, 5000m, customer: "ben");
        await Apply(database, 3600m, customer: "anna");

        using var context = database.Create();
        Assert.Equal(2, await context.Forecasts.CountAsync());
        Assert.Equal(2 * FakeYearSize, await context.ForecastIntervals.CountAsync());
        Assert.Equal(5000m, (await context.Forecasts.SingleAsync(x => x.CustomerId == "ben")).AnnualKwh);
        Assert.Equal(3600m, (await context.Forecasts.SingleAsync(x => x.CustomerId == "anna")).AnnualKwh);
    }

    [Fact]
    public async Task The_same_customer_can_have_a_result_per_profile()
    {
        using var database = new TestDb();
        await Sync(database, new FakeSlpApi(), "h1");
        await Sync(database, new FakeSlpApi { ValueFor = t => 2m }, "h2");

        await Apply(database, 3500m, profile: "h1");
        await Apply(database, 3500m, profile: "h2");

        using var context = database.Create();
        Assert.Equal(2, await context.Forecasts.CountAsync());
    }

    [Fact]
    public async Task A_changed_profile_is_picked_up_when_the_job_runs_again()
    {
        using var database = new TestDb();
        await Sync(database);
        await Apply(database, 3500m);

        await Sync(database, new FakeSlpApi { ValueFor = t => FakeSlpApi.DefaultValue(t) * 2m });
        await Apply(database, 3500m);

        // Doubling every value does not change the shape, so the totals still match.
        using var context = database.Create();
        Assert.Equal(1, await context.Forecasts.CountAsync());
        Assert.True(Math.Abs(await SumOf(context) - 3500m) < 0.01m);
    }

    [Fact]
    public async Task Without_a_synced_profile_the_job_explains_what_to_do()
    {
        using var database = new TestDb();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(database, 3500m));

        Assert.Contains("sync", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2_000_000_000)]
    public async Task Unreasonable_annual_consumption_is_rejected(long annualKwh)
    {
        using var database = new TestDb();
        await Sync(database);

        await Assert.ThrowsAsync<ArgumentException>(() => Apply(database, annualKwh));

        using var context = database.Create();
        Assert.Equal(0, await context.Forecasts.CountAsync());
    }

    [Fact]
    public async Task Blank_customer_ids_are_rejected()
    {
        using var database = new TestDb();
        await Sync(database);

        await Assert.ThrowsAsync<ArgumentException>(() => Apply(database, 3500m, customer: "  "));
    }

    private static async Task<decimal> SumOf(RabotSlp.Data.SlpDbContext context) =>
        (await context.ForecastIntervals.ToListAsync()).Sum(x => x.ConsumptionKwh);

    [Fact]
    public async Task Forecast_points_to_customer_and_profile_master_rows()
    {
        using var database = new TestDb();
        await Sync(database);

        await Apply(database, 3500m, "Fahad");
        await Apply(database, 4000m, "FAHAD");

        using var context = database.Create();
        var customer = await context.Customers.SingleAsync();
        Assert.Equal("fahad", customer.CustomerId);
        Assert.Equal(Now.UtcDateTime, customer.CreatedAtUtc);

        var forecast = await context.Forecasts.SingleAsync();
        Assert.Equal(customer.CustomerId, forecast.CustomerId);
        Assert.Equal("h1", forecast.ProfileId);
        Assert.Equal(4000m, forecast.AnnualKwh);

        var intervalCount = await context.ForecastIntervals.CountAsync(x => x.ForecastId == forecast.ForecastId);
        Assert.Equal(FakeYearSize, intervalCount);
        Assert.Equal(FakeYearSize, await context.ForecastIntervals.CountAsync());
    }
}
