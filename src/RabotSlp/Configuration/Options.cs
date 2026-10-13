using RabotSlp.Jobs;

namespace RabotSlp.Configuration;

/// <summary>Settings for the SLP HTTP API.</summary>
public sealed class SlpApiOptions
{
    public const string SectionName = "SlpApi";

    public string BaseUrl { get; set; } = "https://poc-rabot-tech-challenge.azurewebsites.net/";

    /// <summary>How often a throttled or transiently failing request is retried.</summary>
    public int MaxRetries { get; set; } = 6;

    /// <summary>Delay used when the server does not send a Retry-After header.</summary>
    public int DefaultRetryDelaySeconds { get; set; } = 1;

    /// <summary>Overall timeout of one API call, including its retries.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Which SQL database to use. "Sqlite" (default) or "SqlServer".</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Provider { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = "Data Source=slp.db";
}

/// <summary>Defaults for the two jobs. Command line options override these.</summary>
public sealed class SlpOptions
{
    public const string SectionName = "Slp";

    public string ProfileId { get; set; } = "h1";

    /// <summary>Calendar year (UTC). When empty, the current UTC year is used.</summary>
    public int? Year { get; set; }

    public string CustomerId { get; set; } = "default";

    public decimal? AnnualKwh { get; set; }

    public NormalisationBasis Basis { get; set; } = NormalisationBasis.StoredSum;
}
