using RabotSlp.Cli;
using RabotSlp.Jobs;

namespace RabotSlp.Tests;

public class CliArgumentsTests
{
    [Fact]
    public void No_arguments_show_the_help()
    {
        Assert.Equal(CliCommand.Help, CliArguments.Parse(Array.Empty<string>()).Command);
        Assert.Equal(CliCommand.Help, CliArguments.Parse(new[] { "--help" }).Command);
    }

    [Fact]
    public void Sync_without_options_uses_the_configured_defaults()
    {
        var cli = CliArguments.Parse(new[] { "sync" });

        Assert.Equal(CliCommand.Sync, cli.Command);
        Assert.Null(cli.ProfileId);
        Assert.Null(cli.Year);
    }

    [Fact]
    public void Sync_reads_profile_and_year()
    {
        var cli = CliArguments.Parse(new[] { "sync", "--profile", "h2", "--year", "2026" });

        Assert.Equal("h2", cli.ProfileId);
        Assert.Equal(2026, cli.Year);
    }

    [Fact]
    public void Apply_reads_all_options_in_both_notations()
    {
        var cli = CliArguments.Parse(new[]
        {
            "apply", "--annual-kwh", "3500.5", "--customer=fahad", "--profile=h1", "--year", "2027", "--basis", "nominal",
        });

        Assert.Equal(CliCommand.Apply, cli.Command);
        Assert.Equal(3500.5m, cli.AnnualKwh);
        Assert.Equal("fahad", cli.CustomerId);
        Assert.Equal("h1", cli.ProfileId);
        Assert.Equal(2027, cli.Year);
        Assert.Equal(NormalisationBasis.Nominal, cli.Basis);
    }

    [Theory]
    [InlineData("backup")]
    public void Unknown_commands_are_rejected(string command)
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { command }));
    }

    [Fact]
    public void Unknown_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "sync", "--verbose" }));
    }

    [Fact]
    public void Options_of_the_apply_command_are_rejected_for_sync()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "sync", "--annual-kwh", "3500" }));
    }

    [Theory]
    [InlineData("--year", "twenty")]
    [InlineData("--year", "-5")]
    public void Invalid_years_are_rejected(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "sync", option, value }));
    }

    [Fact]
    public void Invalid_numbers_and_missing_values_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "apply", "--annual-kwh", "3,500" }));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "apply", "--annual-kwh" }));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(new[] { "apply", "--basis", "weird" }));
    }
}
