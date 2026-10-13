using RabotSlp.Jobs;

namespace RabotSlp.Tests;

public class YearRangeTests
{
    [Theory]
    [InlineData(2026)]
    [InlineData(2028)]
    public void Monthly_chunks_cover_the_year_without_gaps_or_overlap(int year)
    {
        var chunks = YearRange.MonthlyChunks(year).ToList();

        Assert.Equal(12, chunks.Count);
        Assert.Equal(YearRange.Start(year), chunks[0].Start);
        Assert.Equal(YearRange.End(year), chunks[11].End);

        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(chunks[i].End, chunks[i + 1].Start);
        }

        Assert.All(chunks, c => Assert.True(c.End <= c.Start.AddMonths(1)));
        Assert.All(chunks, c => Assert.Equal(DateTimeKind.Utc, c.Start.Kind));
    }

    [Fact]
    public void A_normal_year_has_35040_intervals_and_a_leap_year_35136()
    {
        Assert.Equal(35040, YearRange.ExpectedIntervalCount(2026));
        Assert.Equal(35136, YearRange.ExpectedIntervalCount(2028));
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2101)]
    public void Years_outside_the_supported_range_are_rejected(int year)
    {
        Assert.Throws<ArgumentException>(() => YearRange.Validate(year));
    }

    [Fact]
    public void Quarter_hour_alignment_is_detected()
    {
        Assert.True(YearRange.IsQuarterHourAligned(Utc.At(2026, 3, 29, 1, 45)));
        Assert.False(YearRange.IsQuarterHourAligned(Utc.At(2026, 3, 29, 1, 40)));
        Assert.False(YearRange.IsQuarterHourAligned(Utc.At(2026, 3, 29, 1, 45).AddSeconds(1)));
    }
}
