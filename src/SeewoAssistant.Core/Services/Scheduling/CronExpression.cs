namespace SeewoAssistant.Core.Services.Scheduling;

/// <summary>
/// A parsed five-field cron expression: minute, hour, day-of-month, month,
/// day-of-week.
/// </summary>
/// <remarks>
/// <para>Supported syntax, matching the classic Vixie cron subset:</para>
/// <list type="bullet">
/// <item><description><c>*</c> — any value</description></item>
/// <item><description><c>5</c> — a single value</description></item>
/// <item><description><c>1,3,5</c> — a list</description></item>
/// <item><description><c>1-5</c> — an inclusive range</description></item>
/// <item><description><c>*/15</c> or <c>0-30/10</c> — a step, optionally over a range</description></item>
/// <item><description>Macros: <c>@hourly</c>, <c>@daily</c>, <c>@midnight</c>,
/// <c>@weekly</c>, <c>@monthly</c>, <c>@yearly</c>, <c>@annually</c></description></item>
/// </list>
/// <para>
/// Day-of-week accepts both 0 and 7 for Sunday, as cron traditionally does.
/// </para>
/// <para>
/// <b>Day-of-month / day-of-week interaction</b> follows Vixie cron rather than
/// naive intersection: when <em>both</em> fields are restricted, a date matches if
/// <em>either</em> field matches. When only one is restricted, only that one
/// applies. Getting this wrong makes <c>0 9 1 * 1</c> ("the 1st, and every Monday")
/// silently mean "the 1st only if it is a Monday".
/// </para>
/// </remarks>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];   // index 1..31
    private readonly bool[] _months = new bool[13];        // index 1..12
    private readonly bool[] _daysOfWeek = new bool[7];     // index 0..6, 0 = Sunday

    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    /// <summary>The original text, preserved for display and round-tripping.</summary>
    public string Text { get; }

    private CronExpression(
        string text,
        bool[] minutes,
        bool[] hours,
        bool[] daysOfMonth,
        bool[] months,
        bool[] daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    /// <summary>
    /// Parses an expression. Throws <see cref="FormatException"/> with a
    /// human-readable Chinese message when it is invalid.
    /// </summary>
    public static CronExpression Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FormatException("定时表达式不能为空。");
        }

        var text = expression.Trim();

        // Macros expand to their five-field equivalent.
        var expanded = text.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => text,
        };

        var fields = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            throw new FormatException(
                $"定时表达式需要 5 个字段（分 时 日 月 周），当前有 {fields.Length} 个。例如：0 8 * * 1-5");
        }

        var minutes = ParseField(fields[0], 0, 59, "分钟", out _);
        var hours = ParseField(fields[1], 0, 23, "小时", out _);
        var daysOfMonth = ParseField(fields[2], 1, 31, "日期", out var domRestricted);
        var months = ParseField(fields[3], 1, 12, "月份", out _);
        var daysOfWeek = ParseDayOfWeekField(fields[4], out var dowRestricted);

        return new CronExpression(
            text, minutes, hours, daysOfMonth, months, daysOfWeek, domRestricted, dowRestricted);
    }

    /// <summary>Parses without throwing.</summary>
    public static bool TryParse(string? expression, out CronExpression? result, out string? error)
    {
        try
        {
            result = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool[] ParseField(
        string field,
        int min,
        int max,
        string fieldName,
        out bool restricted)
    {
        var size = max + 1;
        var values = new bool[size];
        restricted = false;

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                throw new FormatException($"{fieldName}字段包含空的列表项。");
            }

            if (part == "*")
            {
                for (var i = min; i <= max; i++)
                {
                    values[i] = true;
                }

                continue;
            }

            // A '*' or range may carry a step: */5 or 1-30/5
            var step = 1;
            var rangePart = part;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                rangePart = part[..slash];
                var stepText = part[(slash + 1)..];

                if (!int.TryParse(stepText, out step) || step <= 0)
                {
                    throw new FormatException($"{fieldName}字段的步长「{stepText}」无效，必须是正整数。");
                }

                restricted = true;
            }

            int rangeStart;
            int rangeEnd;

            if (rangePart == "*")
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else
            {
                var dash = rangePart.IndexOf('-', rangePart.Length > 0 && rangePart[0] == '-' ? 1 : 0);

                if (dash > 0)
                {
                    var startText = rangePart[..dash];
                    var endText = rangePart[(dash + 1)..];

                    if (!int.TryParse(startText, out rangeStart))
                    {
                        throw new FormatException($"{fieldName}字段的起始值「{startText}」无效。");
                    }

                    if (!int.TryParse(endText, out rangeEnd))
                    {
                        throw new FormatException($"{fieldName}字段的结束值「{endText}」无效。");
                    }

                    if (rangeStart > rangeEnd)
                    {
                        throw new FormatException(
                            $"{fieldName}字段的范围「{rangePart}」起止颠倒。cron 不支持跨零点回绕范围。");
                    }

                    restricted = true;
                }
                else
                {
                    if (!int.TryParse(rangePart, out rangeStart))
                    {
                        throw new FormatException($"{fieldName}字段的值「{rangePart}」无效。");
                    }

                    rangeEnd = rangeStart;
                    restricted = true;
                }
            }

            // Validate against the declared bounds before indexing.
            if (rangeStart < min || rangeStart > max || rangeEnd < min || rangeEnd > max)
            {
                throw new FormatException(
                    $"{fieldName}字段的值超出范围，允许 {min}-{max}。");
            }

            for (var i = rangeStart; i <= rangeEnd; i += step)
            {
                values[i] = true;
            }
        }

        if (!values.Any(v => v))
        {
            throw new FormatException($"{fieldName}字段没有匹配任何值。");
        }

        return values;
    }

    /// <summary>
    /// Parses the day-of-week field. Handles the traditional quirk that both 0 and
    /// 7 mean Sunday, including inside ranges such as <c>5-7</c> (Friday, Saturday,
    /// Sunday) which must not be rejected as an out-of-range or reversed range.
    /// </summary>
    private static bool[] ParseDayOfWeekField(string field, out bool restricted)
    {
        var values = new bool[7];
        restricted = false;

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                throw new FormatException("星期字段包含空的列表项。");
            }

            if (part == "*")
            {
                for (var i = 0; i < 7; i++)
                {
                    values[i] = true;
                }

                continue;
            }

            var step = 1;
            var rangePart = part;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                rangePart = part[..slash];
                var stepText = part[(slash + 1)..];

                if (!int.TryParse(stepText, out step) || step <= 0)
                {
                    throw new FormatException($"星期字段的步长「{stepText}」无效，必须是正整数。");
                }

                restricted = true;
            }

            int start;
            int end;

            if (rangePart == "*")
            {
                start = 0;
                end = 6;
            }
            else
            {
                var dash = rangePart.IndexOf('-', rangePart.Length > 0 && rangePart[0] == '-' ? 1 : 0);

                if (dash > 0)
                {
                    var startText = rangePart[..dash];
                    var endText = rangePart[(dash + 1)..];

                    if (!TryParseDayOfWeek(startText, out start))
                    {
                        throw new FormatException($"星期字段的起始值「{startText}」无效，允许 0-7（0 和 7 都表示周日）。");
                    }

                    if (!TryParseDayOfWeek(endText, out end))
                    {
                        throw new FormatException($"星期字段的结束值「{endText}」无效，允许 0-7（0 和 7 都表示周日）。");
                    }

                    // "5-7" means Friday through Sunday, so the end maps to 0. Walk
                    // the range in raw day numbers and fold 7 onto 0 rather than
                    // comparing the folded values, which would look reversed.
                    if (end == 0 && !endText.Trim().Equals("0", StringComparison.Ordinal))
                    {
                        for (var i = start; i <= 7; i += step)
                        {
                            values[i == 7 ? 0 : i] = true;
                        }

                        restricted = true;
                        continue;
                    }

                    if (start > end)
                    {
                        throw new FormatException(
                            $"星期字段的范围「{rangePart}」起止颠倒。cron 不支持跨周回绕范围。");
                    }

                    restricted = true;
                }
                else
                {
                    if (!TryParseDayOfWeek(rangePart, out start))
                    {
                        throw new FormatException($"星期字段的值「{rangePart}」无效，允许 0-7（0 和 7 都表示周日）。");
                    }

                    end = start;
                    restricted = true;
                }
            }

            for (var i = start; i <= end; i += step)
            {
                values[i] = true;
            }
        }

        if (!values.Any(v => v))
        {
            throw new FormatException("星期字段没有匹配任何值。");
        }

        return values;
    }

    private static bool TryParseDayOfWeek(string text, out int value)
    {
        if (!int.TryParse(text, out value))
        {
            return false;
        }

        if (value == 7)
        {
            value = 0;
        }

        return value is >= 0 and <= 6;
    }

    /// <summary>True when the given minute matches the expression.</summary>
    public bool Matches(DateTime time)
    {
        if (!_minutes[time.Minute] || !_hours[time.Hour])
        {
            return false;
        }

        return MatchesDate(time);
    }

    /// <summary>True when the date component (month, day-of-month, day-of-week) matches.</summary>
    private bool MatchesDate(DateTime time)
    {
        if (!_months[time.Month])
        {
            return false;
        }

        var domMatch = _daysOfMonth[time.Day];
        var dowMatch = _daysOfWeek[(int)time.DayOfWeek];

        // Vixie cron semantics: when both day fields are restricted, either may
        // match. When only one is restricted, that one decides.
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
        {
            return domMatch || dowMatch;
        }

        if (_dayOfMonthRestricted)
        {
            return domMatch;
        }

        if (_dayOfWeekRestricted)
        {
            return dowMatch;
        }

        return true;
    }

    /// <summary>
    /// Finds the next matching time strictly after <paramref name="after"/>.
    /// </summary>
    /// <returns>The next occurrence, or null when none exists within four years
    /// (which happens for impossible dates such as 30 February).</returns>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        // Start at the next whole minute: cron has one-minute resolution.
        var candidate = new DateTime(
            after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);

        // Four years covers every leap-year combination, so an expression that has
        // not matched by then never will. 30 February is the canonical example.
        var limit = candidate.AddYears(4);

        while (candidate < limit)
        {
            if (!_months[candidate.Month])
            {
                // Skip the whole month rather than walking it minute by minute.
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0)
                    .AddMonths(1);
                continue;
            }

            if (!MatchesDate(candidate))
            {
                // Skip the whole day. The month check above guarantees AddDays lands
                // in a valid month.
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                // Skip the whole hour.
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            // The local offset is taken from the candidate so a schedule that spans a
            // daylight-saving transition still reports a usable instant.
            return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
        }

        return null;
    }

    /// <summary>Explains the expression in Chinese, for display in the UI.</summary>
    public string Describe()
    {
        // Macros describe themselves.
        var lower = Text.ToLowerInvariant();
        switch (lower)
        {
            case "@hourly": return "每小时整点";
            case "@daily":
            case "@midnight": return "每天 00:00";
            case "@weekly": return "每周日 00:00";
            case "@monthly": return "每月 1 日 00:00";
            case "@yearly":
            case "@annually": return "每年 1 月 1 日 00:00";
        }

        var fields = Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return Text;
        }

        var parts = new List<string>();

        parts.Add(DescribeTimeField(fields[1], fields[0]));

        if (fields[2] != "*")
        {
            parts.Add($"每月 {fields[2]} 日");
        }

        if (fields[3] != "*")
        {
            parts.Add($"{fields[3]} 月");
        }

        if (fields[4] != "*")
        {
            parts.Add(DescribeDayOfWeek(fields[4]));
        }

        return string.Join("，", parts);
    }

    private static string DescribeTimeField(string hourField, string minuteField)
    {
        if (hourField == "*" && minuteField == "*")
        {
            return "每分钟";
        }

        if (hourField == "*")
        {
            return minuteField.StartsWith("*/", StringComparison.Ordinal)
                ? $"每小时的第 {minuteField[2..]} 分钟间隔"
                : $"每小时的第 {minuteField} 分钟";
        }

        if (minuteField == "*")
        {
            return $"{hourField} 点的每分钟";
        }

        return $"{hourField}:{minuteField.PadLeft(2, '0')}";
    }

    private static string DescribeDayOfWeek(string field)
    {
        string[] names = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

        if (int.TryParse(field, out var single))
        {
            if (single == 7)
            {
                single = 0;
            }

            return single is >= 0 and <= 6 ? names[single] : $"星期 {field}";
        }

        if (field.Contains('-', StringComparison.Ordinal))
        {
            var bounds = field.Split('-');
            if (bounds.Length == 2 &&
                int.TryParse(bounds[0], out var from) &&
                int.TryParse(bounds[1], out var to) &&
                from is >= 0 and <= 6 &&
                to is >= 0 and <= 6)
            {
                return $"{names[from]}至{names[to]}";
            }
        }

        if (field.Contains(',', StringComparison.Ordinal))
        {
            var days = field.Split(',')
                .Select(d => int.TryParse(d, out var v) ? (v == 7 ? 0 : v) : -1)
                .Where(v => v is >= 0 and <= 6)
                .Select(v => names[v]);

            return string.Join("、", days);
        }

        return $"星期 {field}";
    }
    public override string ToString() => Text;
}
