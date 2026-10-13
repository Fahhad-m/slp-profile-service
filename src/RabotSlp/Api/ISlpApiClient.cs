namespace RabotSlp.Api;

/// <summary>One 15 minute interval of a profile. The timestamp is the interval start in UTC.</summary>
public sealed record ProfileInterval(DateTime TimestampUtc, decimal ValueKwh);

public interface ISlpApiClient
{
    /// <summary>
    /// Loads the profile for [fromUtc, toUtc). The API accepts at most one calendar month per call.
    /// </summary>
    Task<IReadOnlyList<ProfileInterval>> GetProfileAsync(
        string profileId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
}
