using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RabotSlp.Api;
using RabotSlp.Data;

namespace RabotSlp.Tests;

/// <summary>
/// A real SQL database (SQLite in memory) shared by several DbContext instances, so that every
/// job run in a test gets a fresh context just like a separate process run would.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = Create();
        context.Database.EnsureCreated();
    }

    public SlpDbContext Create() =>
        new(new DbContextOptionsBuilder<SlpDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Stand in for the SLP API. By default it only returns the first two days of every month,
/// which keeps the tests fast. Set <see cref="DaysPerChunk"/> to 31 for a complete year.
/// </summary>
public sealed class FakeSlpApi : ISlpApiClient
{
    public int DaysPerChunk { get; set; } = 2;

    public Func<DateTime, decimal> ValueFor { get; set; } = DefaultValue;

    /// <summary>Intervals the fake API leaves out, like the real h2 profile does once in March.</summary>
    public HashSet<DateTime> Gaps { get; } = new();

    /// <summary>When set, the n-th request (1 based) and all later ones fail.</summary>
    public int? FailFromCall { get; set; }

    /// <summary>When set, replaces the generated answer completely.</summary>
    public Func<string, DateTime, DateTime, IReadOnlyList<ProfileInterval>>? Override { get; set; }

    public List<(string ProfileId, DateTime From, DateTime To)> Requests { get; } = new();

    public static decimal DefaultValue(DateTime timestamp) =>
        10m + (timestamp.Hour * 4 + timestamp.Minute / 15) / 100m + timestamp.Day / 1000m;

    public Task<IReadOnlyList<ProfileInterval>> GetProfileAsync(
        string profileId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        Requests.Add((profileId, fromUtc, toUtc));

        if (FailFromCall is { } failFrom && Requests.Count >= failFrom)
        {
            throw new SlpApiException("simulated outage", HttpStatusCode.ServiceUnavailable);
        }

        if (Override is not null)
        {
            return Task.FromResult(Override(profileId, fromUtc, toUtc));
        }

        var end = fromUtc.AddDays(DaysPerChunk);
        if (end > toUtc)
        {
            end = toUtc;
        }

        var result = new List<ProfileInterval>();
        for (var t = fromUtc; t < end; t = t.AddMinutes(15))
        {
            if (!Gaps.Contains(t))
            {
                result.Add(new ProfileInterval(t, ValueFor(t)));
            }
        }

        return Task.FromResult<IReadOnlyList<ProfileInterval>>(result);
    }
}

public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

public static class Utc
{
    public static DateTime At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
