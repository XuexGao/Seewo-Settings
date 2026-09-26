using SeewoAssistant.Core.Services.Scheduling;
using Xunit;

namespace SeewoAssistant.Tests;

/// <summary>
/// Tests for the cron parser. The day-of-month / day-of-week interaction and the
/// Sunday-as-7 handling are the parts most likely to be got wrong, so they get the
/// most coverage.
/// </summary>
public sealed class CronExpressionTests
{
    [Theory]
    [InlineData("0 8 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 1 *")]
    [InlineData("30 9 * * 1-5")]
    [InlineData("0 0,12 * * *")]
    [InlineData("@daily")]
    [InlineData("@hourly")]
    [InlineData("@weekly")]
    [InlineData("@monthly")]
    [InlineData("@yearly")]
    [InlineData("0 0 * * 7")]
    [InlineData("0 0 * * 5-7")]
    [InlineData("0 0-23/2 * * *")]
    public void Parse_AcceptsValidExpressions(string expression)
    {
        var cron = CronExpression.Parse(expression);
        Assert.Equal(expression, cron.Text);
    }

    [Theory]
    [InlineData("", "空")]
    [InlineData("* * * *", "5 个字段")]
    [InlineData("* * * * * *", "5 个字段")]
    [InlineData("60 * * * *", "超出范围")]
    [InlineData("* 24 * * *", "超出范围")]
    [InlineData("* * 0 * *", "超出范围")]
    [InlineData("* * 32 * *", "超出范围")]
    [InlineData("* * * 13 *", "超出范围")]
    [InlineData("* * * * 8", "无效")]
    [InlineData("*/0 * * * *", "步长")]
    [InlineData("5-1 * * * *", "起止颠倒")]
    [InlineData("abc * * * *", "无效")]
    public void Parse_RejectsInvalidExpressions(string expression, string expectedFragment)
    {
        var ex = Assert.Throws<FormatException>(() => CronExpression.Parse(expression));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Parse_RejectsNull()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse(null));
    }

    [Fact]
    public void GetNextOccurrence_SimpleDaily()
    {
        var cron = CronExpression.Parse("0 8 * * *");
        var after = new DateTimeOffset(2024, 3, 10, 6, 30, 0, TimeSpan.FromHours(8));

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 3, 10, 8, 0, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_RollsToNextDayWhenTimeHasPassed()
    {
        var cron = CronExpression.Parse("0 8 * * *");
        var after = new DateTimeOffset(2024, 3, 10, 9, 0, 0, TimeSpan.FromHours(8));

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 3, 11, 8, 0, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_IsStrictlyAfterTheGivenTime()
    {
        // An expression matching the current minute must return the *next* match,
        // never the same instant, or the scheduler would fire repeatedly.
        var cron = CronExpression.Parse("* * * * *");
        var after = new DateTimeOffset(2024, 3, 10, 8, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 3, 10, 8, 1, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_WeekdayRangeSkipsTheWeekend()
    {
        var cron = CronExpression.Parse("0 9 * * 1-5");

        // 2024-03-08 is a Friday; the next weekday 09:00 is Monday the 11th.
        var friday = new DateTimeOffset(2024, 3, 8, 10, 0, 0, TimeSpan.Zero);
        var next = cron.GetNextOccurrence(friday);

        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
        Assert.Equal(new DateTime(2024, 3, 11, 9, 0, 0), next.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_SundayCanBeWrittenAsSeven()
    {
        var asSeven = CronExpression.Parse("0 0 * * 7");
        var asZero = CronExpression.Parse("0 0 * * 0");

        var after = new DateTimeOffset(2024, 3, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(asZero.GetNextOccurrence(after), asSeven.GetNextOccurrence(after));
        Assert.Equal(DayOfWeek.Sunday, asSeven.GetNextOccurrence(after)!.Value.DayOfWeek);
    }

    [Fact]
    public void GetNextOccurrence_FridayThroughSundayRangeIsAccepted()
    {
        // 5-7 must mean Fri/Sat/Sun, not be rejected as a reversed range.
        var cron = CronExpression.Parse("0 0 * * 5-7");

        Assert.True(cron.Matches(new DateTime(2024, 3, 8)));  // Friday
        Assert.True(cron.Matches(new DateTime(2024, 3, 9)));  // Saturday
        Assert.True(cron.Matches(new DateTime(2024, 3, 10))); // Sunday
        Assert.False(cron.Matches(new DateTime(2024, 3, 11))); // Monday
    }

    [Fact]
    public void Matches_BothDayFieldsRestricted_MatchesEither()
    {
        // Vixie cron: "0 0 1 * 1" means the 1st of the month OR any Monday.
        var cron = CronExpression.Parse("0 0 1 * 1");

        Assert.True(cron.Matches(new DateTime(2024, 4, 1)));   // the 1st, a Monday
        Assert.True(cron.Matches(new DateTime(2024, 5, 1)));   // the 1st, a Wednesday
        Assert.True(cron.Matches(new DateTime(2024, 4, 8)));   // a Monday, not the 1st
        Assert.False(cron.Matches(new DateTime(2024, 4, 9)));  // neither
    }

    [Fact]
    public void Matches_OnlyDayOfMonthRestricted_IgnoresDayOfWeek()
    {
        var cron = CronExpression.Parse("0 0 15 * *");

        Assert.True(cron.Matches(new DateTime(2024, 4, 15)));
        Assert.False(cron.Matches(new DateTime(2024, 4, 16)));
    }

    [Fact]
    public void GetNextOccurrence_HandlesLeapDay()
    {
        // 29 February only exists in a leap year, so the search must cross years.
        var cron = CronExpression.Parse("0 0 29 2 *");
        var after = new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 2, 29, 0, 0, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_ReturnsNullForImpossibleDate()
    {
        // 30 February never occurs; the search must give up rather than loop.
        var cron = CronExpression.Parse("0 0 30 2 *");
        var after = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(cron.GetNextOccurrence(after));
    }

    [Fact]
    public void GetNextOccurrence_StepOverMinutes()
    {
        var cron = CronExpression.Parse("*/15 * * * *");
        var after = new DateTimeOffset(2024, 3, 10, 8, 7, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 3, 10, 8, 15, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_MonthlyOnTheFirst()
    {
        var cron = CronExpression.Parse("0 0 1 * *");
        var after = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0), next!.Value.DateTime);
    }

    [Fact]
    public void GetNextOccurrence_Yearly()
    {
        var cron = CronExpression.Parse("0 0 1 1 *");
        var after = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0), next!.Value.DateTime);
    }

    [Fact]
    public void TryParse_ReportsErrorWithoutThrowing()
    {
        Assert.False(CronExpression.TryParse("nonsense", out var cron, out var error));
        Assert.Null(cron);
        Assert.NotNull(error);
        Assert.NotEmpty(error!);
    }

    [Fact]
    public void TryParse_SucceedsForValidInput()
    {
        Assert.True(CronExpression.TryParse("0 8 * * 1-5", out var cron, out var error));
        Assert.NotNull(cron);
        Assert.Null(error);
    }

    [Fact]
    public void Describe_ProducesReadableChinese()
    {
        Assert.Contains("8:00", CronExpression.Parse("0 8 * * *").Describe());
        Assert.Contains("每分钟", CronExpression.Parse("* * * * *").Describe());
        Assert.Contains("周一至周五", CronExpression.Parse("0 9 * * 1-5").Describe());
        Assert.Contains("每小时整点", CronExpression.Parse("@hourly").Describe());
    }

    [Fact]
    public void Describe_HandlesMonthAndDayConstraints()
    {
        var description = CronExpression.Parse("0 0 1 1 *").Describe();
        Assert.Contains("1 月", description);
        Assert.Contains("每月 1 日", description);
    }
}
