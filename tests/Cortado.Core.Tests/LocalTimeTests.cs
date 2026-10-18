using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class LocalTimeTests
{
    [Test]
    public void NextOccurrence_AlreadyPassed_UsesTomorrow()
    {
        var now = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);
        Assert.That(LocalTime.NextOccurrence(now, TimeZoneInfo.Utc, 13, 30),
            Is.EqualTo(new DateTimeOffset(2026, 10, 19, 13, 30, 0, TimeSpan.Zero)));
    }

    [Test]
    public void NextOccurrence_ExactlyNow_UsesTomorrow()
    {
        var now = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);
        Assert.That(LocalTime.NextOccurrence(now, TimeZoneInfo.Utc, 14, 0), Is.EqualTo(now.AddDays(1)));
    }

    [Test]
    public void NextOccurrence_DstOverlap_ChoosesSecondOccurrenceIfFirstPassed()
    {
        var now = new DateTimeOffset(2026, 11, 1, 5, 45, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.That(LocalTime.NextOccurrence(now, zone, 1, 30), Is.EqualTo(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)));
    }

    [Test]
    public void NextOccurrence_DstGap_RejectsNonexistentTime()
    {
        var now = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => LocalTime.NextOccurrence(now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), 2, 30));
    }

    [Test]
    public void NextOccurrence_DstGapAlreadyPassed_UsesTomorrow()
    {
        var now = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.That(LocalTime.NextOccurrence(now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), 2, 30),
            Is.EqualTo(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero)));
    }

    [TestCase("America/New_York", 16)]
    [TestCase("Europe/London", 11)]
    public void NextOccurrence_CurrentZone_UsesLocalInput(String zoneId, Int32 expectedUtcHour)
    {
        var now = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
        var result = LocalTime.NextOccurrence(now, TimeZoneInfo.FindSystemTimeZoneById(zoneId), 12, 0);
        Assert.That(result.Hour, Is.EqualTo(expectedUtcHour));
    }

    [TestCase(-1, 0, "hour")]
    [TestCase(24, 0, "hour")]
    [TestCase(0, -1, "minute")]
    [TestCase(0, 60, "minute")]
    public void NextOccurrence_InvalidInput_Rejects(Int32 hour, Int32 minute, String parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => LocalTime.NextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc, hour, minute));

        Assert.That(exception.ParamName, Is.EqualTo(parameterName));
    }
}