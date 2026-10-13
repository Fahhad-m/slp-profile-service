namespace RabotSlp.Jobs;

/// <summary>What the stored profile is divided by when it is scaled to a customer.</summary>
public enum NormalisationBasis
{
    /// <summary>
    /// Divide by the sum of the stored values, so the customer's intervals add up to the
    /// annual consumption that was given. This is the default.
    /// </summary>
    StoredSum,

    /// <summary>Divide by the nominal 1,000,000 kWh the profile is documented to add up to.</summary>
    Nominal,
}

/// <summary>Calendar helpers. All times are UTC, which is also what the API uses.</summary>
public static class YearRange
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    private static readonly long IntervalTicks = TimeSpan.FromMinutes(15).Ticks;

    public static void Validate(int year)
    {
        if (year < MinYear || year > MaxYear)
        {
            throw new ArgumentException($"Year must be between {MinYear} and {MaxYear}, got {year}.");
        }
    }

    public static DateTime Start(int year) => new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Exclusive end of the year: the first instant of the next year.</summary>
    public static DateTime End(int year) => Start(year + 1);

    /// <summary>
    /// The year split into calendar months. The API serves at most one calendar month per call,
    /// so a year takes twelve requests.
    /// </summary>
    public static IEnumerable<(DateTime Start, DateTime End)> MonthlyChunks(int year)
    {
        for (var month = 1; month <= 12; month++)
        {
            var chunkStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            yield return (chunkStart, chunkStart.AddMonths(1));
        }
    }

    /// <summary>35,040 for a normal year and 35,136 for a leap year.</summary>
    public static int ExpectedIntervalCount(int year) =>
        (int)((End(year) - Start(year)).Ticks / IntervalTicks);

    public static bool IsQuarterHourAligned(DateTime value) => value.Ticks % IntervalTicks == 0;
}

public static class Identifiers
{
    public static string NormaliseProfileId(string? profileId)
    {
        var value = profileId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)
            || value.Length > 32
            || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
        {
            throw new ArgumentException(
                $"Invalid profile id '{profileId}'. Use letters, digits, '-' or '_' (for example h1 or h2).");
        }

        return value;
    }

    public static string NormaliseCustomerId(string? customerId)
    {
        // Lower case, so that "Fahad" and "fahad" are the same customer on every database.
        var value = customerId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            throw new ArgumentException("Customer id must be between 1 and 64 characters.");
        }

        return value;
    }
}
