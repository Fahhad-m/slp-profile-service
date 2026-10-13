namespace RabotSlp.Data;

/// <summary>
/// Master table of standard load profiles (h1, h2, ...). A row is created by the first successful
/// sync of a profile. Profile values and forecasts refer to it.
/// </summary>
public class Profile
{
    public string ProfileId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastSyncedAtUtc { get; set; }
}

/// <summary>Master table of customers. A row is created the first time a result is calculated for a customer.</summary>
public class Customer
{
    public string CustomerId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One stored 15 minute value of a standard load profile (normalised to 1,000,000 kWh per year).</summary>
public class StoredProfileValue
{
    /// <summary>Foreign key to <see cref="Profile.ProfileId"/>.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Start of the interval, UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    public decimal ValueKwh { get; set; }
}

/// <summary>
/// Header of a calculation: one customer, one profile, one year. There is exactly one row per
/// (customer, profile, year); running the apply job again replaces it.
/// </summary>
public class ConsumptionForecast
{
    public int ForecastId { get; set; }

    /// <summary>Foreign key to <see cref="Customer.CustomerId"/>.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Foreign key to <see cref="Profile.ProfileId"/>: the profile this result was calculated from.</summary>
    public string ProfileId { get; set; } = string.Empty;

    public int Year { get; set; }

    public decimal AnnualKwh { get; set; }

    /// <summary>Factor that was multiplied with every profile value.</summary>
    public decimal ScalingFactor { get; set; }

    public DateTime CalculatedAtUtc { get; set; }

    public List<ForecastInterval> Intervals { get; set; } = new();
}

/// <summary>Detail row of a calculation: the expected consumption of the customer in one 15 minute interval.</summary>
public class ForecastInterval
{
    /// <summary>Foreign key to <see cref="ConsumptionForecast.ForecastId"/>.</summary>
    public int ForecastId { get; set; }

    /// <summary>Start of the interval, UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    public decimal ConsumptionKwh { get; set; }
}
