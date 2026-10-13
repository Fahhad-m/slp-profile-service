using System.Globalization;
using RabotSlp.Jobs;

namespace RabotSlp.Cli;

public enum CliCommand
{
    Help,
    Sync,
    Apply,
}

/// <summary>Command line of the service. Values that are not given fall back to appsettings.json.</summary>
public sealed record CliArguments(
    CliCommand Command,
    string? ProfileId = null,
    int? Year = null,
    decimal? AnnualKwh = null,
    string? CustomerId = null,
    NormalisationBasis? Basis = null)
{
    public static readonly string Usage = string.Join(
        Environment.NewLine,
        "Usage:",
        "  RabotSlp sync  [--profile h1|h2] [--year 2026]",
        "  RabotSlp apply --annual-kwh <kWh> [--customer <name>] [--profile h1|h2] [--year 2026] [--basis stored|nominal]",
        string.Empty,
        "sync   downloads the profile for the year and stores it in the database (safe to run repeatedly).",
        "apply  scales the stored profile to the given annual consumption and stores one value per 15 minutes.",
        string.Empty,
        "Defaults come from appsettings.json (section Slp). The year defaults to the current UTC year.");

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new CliArguments(CliCommand.Help);
        }

        var verb = args[0].Trim().ToLowerInvariant();
        if (verb is "-h" or "--help" or "help")
        {
            return new CliArguments(CliCommand.Help);
        }

        var command = verb switch
        {
            "sync" => CliCommand.Sync,
            "apply" => CliCommand.Apply,
            _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
        };

        string? profile = null;
        int? year = null;
        decimal? annual = null;
        string? customer = null;
        NormalisationBasis? basis = null;

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            string name = token;
            string? inlineValue = null;

            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                name = token[..equals];
                inlineValue = token[(equals + 1)..];
            }

            name = name.ToLowerInvariant();

            switch (name)
            {
                case "--profile":
                    profile = ReadValue(args, ref i, name, inlineValue);
                    break;
                case "--year":
                    var yearText = ReadValue(args, ref i, name, inlineValue);
                    if (!int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
                    {
                        throw new ArgumentException($"'{yearText}' is not a valid year.");
                    }

                    year = parsedYear;
                    break;
                case "--annual-kwh":
                    RequireCommand(command, CliCommand.Apply, name);
                    var kwhText = ReadValue(args, ref i, name, inlineValue);
                    // No thousands separators on purpose: "3,5" must not silently become 35.
                    if (!decimal.TryParse(
                            kwhText,
                            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture,
                            out var parsedKwh))
                    {
                        throw new ArgumentException($"'{kwhText}' is not a valid number of kWh. Use a dot as the decimal separator, for example 3500.5.");
                    }

                    annual = parsedKwh;
                    break;
                case "--customer":
                    RequireCommand(command, CliCommand.Apply, name);
                    customer = ReadValue(args, ref i, name, inlineValue);
                    break;
                case "--basis":
                    RequireCommand(command, CliCommand.Apply, name);
                    var basisText = ReadValue(args, ref i, name, inlineValue).Trim().ToLowerInvariant();
                    basis = basisText switch
                    {
                        "stored" or "storedsum" => NormalisationBasis.StoredSum,
                        "nominal" => NormalisationBasis.Nominal,
                        _ => throw new ArgumentException($"Unknown basis '{basisText}'. Use 'stored' or 'nominal'."),
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{token}'.");
            }
        }

        return new CliArguments(command, profile, year, annual, customer, basis);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string name, string? inlineValue)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Option {name} needs a value.");
        }

        index++;
        return args[index];
    }

    private static void RequireCommand(CliCommand actual, CliCommand expected, string option)
    {
        if (actual != expected)
        {
            throw new ArgumentException($"Option {option} is only valid for the '{expected.ToString().ToLowerInvariant()}' command.");
        }
    }
}
