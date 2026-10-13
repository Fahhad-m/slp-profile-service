using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RabotSlp.Api;
using RabotSlp.Jobs;

namespace RabotSlp.Tests;

public class ProfileSyncJobTests
{
    // Two days per month at 96 intervals per day, twelve months.
    private const int FakeYearSize = 12 * 2 * 96;
    private static readonly DateTimeOffset DefaultNow = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static async Task<SyncResult> Sync(TestDb database, FakeSlpApi api, string profile = "h1", int year = 2026, DateTimeOffset? now = null)
    {
        using var context = database.Create();
        var job = new ProfileSyncJob(api, context, new FixedTimeProvider(now ?? DefaultNow), NullLogger<ProfileSyncJob>.Instance);
        return await job.RunAsync(profile, year);
    }

    private static async Task<int> CountRows(TestDb database, string profile)
    {
        using var context = database.Create();
        return await context.ProfileValues.CountAsync(x => x.ProfileId == profile);
    }

    [Fact]
    public async Task First_run_stores_every_interval_of_the_year()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        var result = await Sync(database, api);

        Assert.Equal(FakeYearSize, result.Fetched);
        Assert.Equal(FakeYearSize, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Requests_cover_the_whole_year_in_calendar_months()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        await Sync(database, api);

        Assert.Equal(12, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.Equal("h1", r.ProfileId));
        Assert.Equal(Utc.At(2026, 1, 1), api.Requests[0].From);
        Assert.Equal(Utc.At(2027, 1, 1), api.Requests[11].To);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(Utc.At(2026, i + 1, 1), api.Requests[i].From);
            Assert.Equal(api.Requests[i].From.AddMonths(1), api.Requests[i].To);
        }
    }

    [Fact]
    public async Task Running_again_creates_no_duplicates_and_changes_nothing()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        await Sync(database, api);
        var second = await Sync(database, api);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Deleted);
        Assert.Equal(FakeYearSize, second.Unchanged);
        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Running_again_updates_values_that_changed()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();
        var changed = Utc.At(2026, 1, 1, 0, 15);

        await Sync(database, api);

        api.ValueFor = t => FakeSlpApi.DefaultValue(t) + (t == changed ? 1m : 0m);
        var second = await Sync(database, api);

        Assert.Equal(1, second.Updated);
        Assert.Equal(FakeYearSize - 1, second.Unchanged);
        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));

        using var context = database.Create();
        var row = await context.ProfileValues.SingleAsync(x => x.ProfileId == "h1" && x.TimestampUtc == changed);
        Assert.Equal(FakeSlpApi.DefaultValue(changed) + 1m, row.ValueKwh);
    }

    [Fact]
    public async Task Running_again_adds_intervals_that_appeared_in_the_api()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();
        var gap = Utc.At(2026, 1, 1, 0, 30);
        api.Gaps.Add(gap);

        var first = await Sync(database, api);
        api.Gaps.Clear();
        var second = await Sync(database, api);

        Assert.Equal(FakeYearSize - 1, first.Inserted);
        Assert.Equal(1, second.Inserted);
        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Running_again_removes_intervals_the_api_no_longer_returns()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();
        var gap = Utc.At(2026, 1, 1, 0, 30);

        await Sync(database, api);
        api.Gaps.Add(gap);
        var second = await Sync(database, api);

        Assert.Equal(1, second.Deleted);
        Assert.Equal(FakeYearSize - 1, await CountRows(database, "h1"));

        using var context = database.Create();
        Assert.False(await context.ProfileValues.AnyAsync(x => x.TimestampUtc == gap));
    }

    [Fact]
    public async Task A_failing_request_leaves_the_stored_profile_untouched()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();
        await Sync(database, api);

        var failing = new FakeSlpApi
        {
            ValueFor = t => FakeSlpApi.DefaultValue(t) + 5m,
            FailFromCall = 4,
        };
        await Assert.ThrowsAsync<SlpApiException>(() => Sync(database, failing));

        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
        using var context = database.Create();
        var firstInterval = Utc.At(2026, 1, 1);
        var first = await context.ProfileValues.SingleAsync(x => x.TimestampUtc == firstInterval);
        Assert.Equal(FakeSlpApi.DefaultValue(firstInterval), first.ValueKwh);
    }

    [Fact]
    public async Task An_empty_answer_is_refused_and_keeps_existing_data()
    {
        using var database = new TestDb();
        await Sync(database, new FakeSlpApi());

        var empty = new FakeSlpApi { Override = (_, _, _) => new List<ProfileInterval>() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sync(database, empty));

        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Profiles_are_stored_independently()
    {
        using var database = new TestDb();
        await Sync(database, new FakeSlpApi(), "h1");
        await Sync(database, new FakeSlpApi { ValueFor = t => 1m }, "h2");

        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
        Assert.Equal(FakeYearSize, await CountRows(database, "h2"));

        using var context = database.Create();
        var firstInterval = Utc.At(2026, 1, 1);
        var h1 = await context.ProfileValues.SingleAsync(x => x.ProfileId == "h1" && x.TimestampUtc == firstInterval);
        var h2 = await context.ProfileValues.SingleAsync(x => x.ProfileId == "h2" && x.TimestampUtc == firstInterval);
        Assert.NotEqual(h1.ValueKwh, h2.ValueKwh);
    }

    [Fact]
    public async Task Missing_intervals_are_reported()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi { DaysPerChunk = 31 };
        var gap = Utc.At(2026, 3, 10, 8, 30);
        api.Gaps.Add(gap);

        var result = await Sync(database, api);

        Assert.Equal(YearRange.ExpectedIntervalCount(2026) - 1, result.Fetched);
        Assert.Single(result.MissingIntervals);
        Assert.Equal(gap, result.MissingIntervals[0]);
        Assert.Equal(YearRange.ExpectedIntervalCount(2026) - 1, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Duplicate_timestamps_in_the_response_are_rejected()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi
        {
            Override = (_, start, _) => new List<ProfileInterval>
            {
                new(start, 1m),
                new(start, 2m),
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Sync(database, api));
        Assert.Equal(0, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Timestamps_outside_the_requested_range_are_rejected()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi
        {
            Override = (_, start, _) => new List<ProfileInterval> { new(start.AddDays(-1), 1m) },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Sync(database, api));
    }

    [Fact]
    public async Task Timestamps_off_the_quarter_hour_are_rejected()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi
        {
            Override = (_, start, _) => new List<ProfileInterval> { new(start.AddMinutes(7), 1m) },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Sync(database, api));
    }

    [Fact]
    public async Task Profile_ids_are_normalised()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        var result = await Sync(database, api, " H1 ");

        Assert.Equal("h1", result.ProfileId);
        Assert.Equal("h1", api.Requests[0].ProfileId);
        Assert.Equal(FakeYearSize, await CountRows(database, "h1"));
    }

    [Fact]
    public async Task Invalid_profile_ids_and_years_are_rejected()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        await Assert.ThrowsAsync<ArgumentException>(() => Sync(database, api, "h1/../x"));
        await Assert.ThrowsAsync<ArgumentException>(() => Sync(database, api, "h1", 1850));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Profile_master_row_is_created_once_and_last_synced_is_updated()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();
        var first = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

        await Sync(database, api, now: first);
        await Sync(database, api, now: second);

        using var context = database.Create();
        var profile = await context.Profiles.SingleAsync();
        Assert.Equal("h1", profile.ProfileId);
        Assert.Equal(first.UtcDateTime, profile.CreatedAtUtc);
        Assert.Equal(second.UtcDateTime, profile.LastSyncedAtUtc);
    }

    [Fact]
    public async Task Every_synced_profile_has_its_own_master_row()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi();

        await Sync(database, api, "h1");
        await Sync(database, api, "h2");

        using var context = database.Create();
        var ids = await context.Profiles.Select(x => x.ProfileId).OrderBy(x => x).ToListAsync();
        Assert.Equal(2, ids.Count);
        Assert.Equal("h1", ids[0]);
        Assert.Equal("h2", ids[1]);
    }

    [Fact]
    public async Task Failed_first_sync_registers_no_profile()
    {
        using var database = new TestDb();
        var api = new FakeSlpApi { FailFromCall = 5 };

        await Assert.ThrowsAnyAsync<Exception>(() => Sync(database, api));

        using var context = database.Create();
        Assert.Equal(0, await context.Profiles.CountAsync());
        Assert.Equal(0, await context.ProfileValues.CountAsync());
    }
}
