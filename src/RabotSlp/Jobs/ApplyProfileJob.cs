using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RabotSlp.Data;

namespace RabotSlp.Jobs;

public sealed record ApplyResult(
    string CustomerId,
    string ProfileId,
    int Year,
    decimal AnnualKwh,
    decimal ScalingFactor,
    int IntervalCount,
    decimal TotalKwh);

/// <summary>
/// Job 2: turns a stored standard load profile into the expected consumption of one customer.
/// For every 15 minute interval: consumption = profile value * annual consumption / basis.
/// The result is saved per (customer, profile, year) and replaced when the job runs again.
/// </summary>
public sealed class ApplyProfileJob
{
    /// <summary>The annual consumption the profile is documented to be normalised to.</summary>
    public const decimal NominalAnnualKwh = 1_000_000m;

    /// <summary>Upper bound that catches typing mistakes such as an extra factor of 1,000.</summary>
    public const decimal MaxAnnualKwh = 1_000_000_000m;

    public const int ConsumptionDecimals = 6;
    public const int FactorDecimals = 12;

    private readonly SlpDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<ApplyProfileJob> _logger;

    public ApplyProfileJob(SlpDbContext db, TimeProvider time, ILogger<ApplyProfileJob> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    public async Task<ApplyResult> RunAsync(
        string customerId,
        string profileId,
        int year,
        decimal annualKwh,
        NormalisationBasis basis = NormalisationBasis.StoredSum,
        CancellationToken cancellationToken = default)
    {
        customerId = Identifiers.NormaliseCustomerId(customerId);
        profileId = Identifiers.NormaliseProfileId(profileId);
        YearRange.Validate(year);

        if (annualKwh <= 0 || annualKwh > MaxAnnualKwh)
        {
            throw new ArgumentException(
                $"Annual consumption must be greater than 0 and at most {MaxAnnualKwh:N0} kWh, got {annualKwh}.");
        }

        var yearStart = YearRange.Start(year);
        var yearEnd = YearRange.End(year);

        var profile = await _db.ProfileValues
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.TimestampUtc >= yearStart && x.TimestampUtc < yearEnd)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (profile.Count == 0)
        {
            throw new InvalidOperationException(
                $"No stored profile '{profileId}' for {year}. Run the sync job first.");
        }

        profile.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        var basisKwh = basis == NormalisationBasis.StoredSum
            ? profile.Sum(x => x.ValueKwh)
            : NominalAnnualKwh;

        if (basisKwh <= 0)
        {
            throw new InvalidOperationException($"The stored profile '{profileId}' for {year} adds up to zero and cannot be scaled.");
        }

        var factor = Math.Round(annualKwh / basisKwh, FactorDecimals, MidpointRounding.AwayFromZero);

        var forecast = new ConsumptionForecast
        {
            CustomerId = customerId,
            ProfileId = profileId,
            Year = year,
            AnnualKwh = annualKwh,
            ScalingFactor = factor,
            CalculatedAtUtc = _time.GetUtcNow().UtcDateTime,
        };

        foreach (var value in profile)
        {
            forecast.Intervals.Add(new ForecastInterval
            {
                TimestampUtc = value.TimestampUtc,
                ConsumptionKwh = Math.Round(value.ValueKwh * factor, ConsumptionDecimals, MidpointRounding.AwayFromZero),
            });
        }

        // Replace a previous result for the same customer, profile and year in one transaction.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var previousIds = await _db.Forecasts
            .AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.ProfileId == profileId && x.Year == year)
            .Select(x => x.ForecastId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var previousId in previousIds)
        {
            await _db.ForecastIntervals.Where(x => x.ForecastId == previousId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await _db.Forecasts.Where(x => x.ForecastId == previousId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // The customer master row is created the first time a result is calculated for the customer.
        var customerExists = await _db.Customers
            .AnyAsync(x => x.CustomerId == customerId, cancellationToken)
            .ConfigureAwait(false);
        if (!customerExists)
        {
            _db.Customers.Add(new Customer { CustomerId = customerId, CreatedAtUtc = forecast.CalculatedAtUtc });
        }

        _db.Forecasts.Add(forecast);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var total = forecast.Intervals.Sum(x => x.ConsumptionKwh);

        _logger.LogInformation(
            "Applied profile {ProfileId} {Year} to customer {CustomerId}: {Count} intervals, {Total} kWh (factor {Factor}).",
            profileId, year, customerId, forecast.Intervals.Count, total, factor);

        return new ApplyResult(customerId, profileId, year, annualKwh, factor, forecast.Intervals.Count, total);
    }
}
