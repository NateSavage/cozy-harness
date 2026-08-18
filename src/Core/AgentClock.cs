using CozyHarness.Config;

namespace CozyHarness.Core;

/// <summary>
/// The agent has no circadian rhythm, but its world does — the operator's commits, messages and news all follow his clock.
/// A day with a shape is more legible to both of them than a uniform grind.
/// </summary>
public sealed class AgentClock {
    private readonly TimeZoneInfo _timeZone;
    private readonly ScheduleConfig _cfg;

    public AgentClock(ScheduleConfig cfg, string timeZoneId) {
        _cfg = cfg;
        _timeZone = SafeZone(timeZoneId);
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset OperatorNow => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);

    public bool IsQuietHours() {
        int hour = OperatorNow.Hour;
        return _cfg.QuietHourStart > _cfg.QuietHourEnd
            ? hour >= _cfg.QuietHourStart || hour < _cfg.QuietHourEnd
            : hour >= _cfg.QuietHourStart && hour < _cfg.QuietHourEnd;
    }

    public bool IsIntakeHour() =>
        OperatorNow.Minute < 5 &&
        (OperatorNow.Hour == _cfg.IntakeMorningHour || OperatorNow.Hour == _cfg.IntakeEveningHour);

    public bool IsDailyReflectHour() =>
        OperatorNow.Hour == _cfg.DailyReflectHour && OperatorNow.Minute < 5;

    public bool IsWeeklyReflectHour() =>
        OperatorNow.DayOfWeek == _cfg.WeeklyReflectDay &&
        OperatorNow.Hour == _cfg.WeeklyReflectHour &&
        OperatorNow.Minute < 5;

    private static TimeZoneInfo SafeZone(string id) {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
