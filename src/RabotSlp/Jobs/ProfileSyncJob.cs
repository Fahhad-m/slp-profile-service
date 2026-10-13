using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RabotSlp.Api;
using RabotSlp.Data;

namespace RabotSlp.Jobs;

public sealed record SyncResult(
    string ProfileId,
    int Year,
    int Fetched,
    int Inserted,
    int Updated,
    int Unchanged,
    int Deleted,
    IReadOnlyList<DateTime> MissingIntervals);

/// <summary>
/// Job 1: mirrors one standard load profile for one calendar year from the API into the database.
/// The job can run any number of times. It never creates duplicates and it brings the stored data
/// in line with the API (values that changed are updated, intervals the API no longer returns are removed).
/// </summary>
public sealed class ProfileSyncJob
{
    private readonly ISlpApiClient _api;
    private readonly SlpDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<ProfileSyncJob> _logger;

    public ProfileSyncJob(ISlpApiClient api, SlpDbContext db, TimeProvider time, ILogger<ProfileSyncJob> logger)
    {
        _api = api;
        _db = db;
        _time = time;
        _logger = logger;
    }

    public async Task<SyncResult> RunAsync(string profileId, int year, CancellationToken cancellationToken = default)
    {
        profileId = Identifiers.NormaliseProfileId(profileId);
        YearRange.Validate(year);

        // Step 1: fetch the whole year before the database is touched. If any request fails,
        // the stored profile stays exactly as it was.
        var fetched = new List<ProfileInterval>(YearRange.ExpectedIntervalCount(year));
        foreach (var (chunkStart, chunkEnd) in YearRange.MonthlyChunks(year))
        {
            var chunk = await _api.GetProfileAsync(profileId, chunkStart, chunkEnd, cancellationToken)
                .ConfigureAwait(false);
            Validate(chunk, profileId, chunkStart, chunkEnd);
            fetched.AddRange(chunk);
        }

        if (fetched.Count == 0)
        {
            // A source that suddenly returns nothing is more likely a fault than a real change.
            throw new InvalidOperationException(
                $"The API returned no values for profile '{profileId}' in {year}. The stored profile was left untouched.");
        }

        var fetchedTimestamps = fetched.Select(x => x.TimestampUtc).ToHashSet();
        var missing = FindMissing(year, fetchedTimestamps);
        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Profile {ProfileId} {Year}: the API returned no value for {Count} of {Expected} expected intervals (first missing: {First:O}).",
                profileId, year, missing.Count, YearRange.ExpectedIntervalCount(year), missing[0]);
        }

        // Step 2: upsert in one transaction.
        var yearStart = YearRange.Start(year);
        var yearEnd = YearRange.End(year);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await _db.ProfileValues
            .Where(x => x.ProfileId == profileId && x.TimestampUtc >= yearStart && x.TimestampUtc < yearEnd)
            .ToDictionaryAsync(x => x.TimestampUtc, cancellationToken)
            .ConfigureAwait(false);

        // The profile master row exists once the API has delivered data for this id.
        var now = _time.GetUtcNow().UtcDateTime;
        var profile = await _db.Profiles
            .SingleOrDefaultAsync(x => x.ProfileId == profileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            profile = new Profile { ProfileId = profileId, CreatedAtUtc = now };
            _db.Profiles.Add(profile);
        }

        profile.LastSyncedAtUtc = now;

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var toAdd = new List<StoredProfileValue>();

        foreach (var item in fetched)
        {
            if (existing.TryGetValue(item.TimestampUtc, out var row))
            {
                if (row.ValueKwh == item.ValueKwh)
                {
                    unchanged++;
                }
                else
                {
                    row.ValueKwh = item.ValueKwh;
                    updated++;
                }
            }
            else
            {
                toAdd.Add(new StoredProfileValue
                {
                    ProfileId = profileId,
                    TimestampUtc = item.TimestampUtc,
                    ValueKwh = item.ValueKwh,
                });
                inserted++;
            }
        }

        var stale = existing.Values.Where(x => !fetchedTimestamps.Contains(x.TimestampUtc)).ToList();

        _db.ProfileValues.AddRange(toAdd);
        _db.ProfileValues.RemoveRange(stale);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Synced profile {ProfileId} {Year}: {Inserted} inserted, {Updated} updated, {Unchanged} unchanged, {Deleted} removed.",
            profileId, year, inserted, updated, unchanged, stale.Count);

        return new SyncResult(profileId, year, fetched.Count, inserted, updated, unchanged, stale.Count, missing);
    }

    // The data comes from an external system, so it is checked before it is stored.
    private static void Validate(IReadOnlyList<ProfileInterval> chunk, string profileId, DateTime chunkStart, DateTime chunkEnd)
    {
        var seen = new HashSet<DateTime>();
        foreach (var item in chunk)
        {
            if (!YearRange.IsQuarterHourAligned(item.TimestampUtc))
            {
                throw new InvalidDataException(
                    $"Profile '{profileId}': timestamp {item.TimestampUtc:O} is not on a 15 minute boundary.");
            }

            if (item.TimestampUtc < chunkStart || item.TimestampUtc >= chunkEnd)
            {
                throw new InvalidDataException(
                    $"Profile '{profileId}': timestamp {item.TimestampUtc:O} is outside the requested range {chunkStart:O} to {chunkEnd:O}.");
            }

            if (item.ValueKwh < 0)
            {
                throw new InvalidDataException(
                    $"Profile '{profileId}': negative value {item.ValueKwh} at {item.TimestampUtc:O}.");
            }

            if (!seen.Add(item.TimestampUtc))
            {
                throw new InvalidDataException(
                    $"Profile '{profileId}': duplicate timestamp {item.TimestampUtc:O} in the API response.");
            }
        }
    }

    private static List<DateTime> FindMissing(int year, HashSet<DateTime> present)
    {
        var missing = new List<DateTime>();
        var end = YearRange.End(year);
        for (var t = YearRange.Start(year); t < end; t = t.AddMinutes(15))
        {
            if (!present.Contains(t))
            {
                missing.Add(t);
            }
        }

        return missing;
    }
}
