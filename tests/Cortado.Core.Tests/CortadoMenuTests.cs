using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class CortadoMenuTests
{
    private TestClock clock = null!;
    private FakeFactory factory = null!;
    private SessionController sessions = null!;
    private CortadoMenu menu = null!;

    [SetUp]
    public void SetUp()
    {
        clock = new();
        factory = new();
        sessions = new(new MemoryStore(), factory, clock, _ => { });
        sessions.Initialize();
        menu = new(sessions, clock, () => TimeZoneInfo.Utc);
        menu.Open();
    }

    [TearDown]
    public void TearDown() => sessions.Dispose();

    private Boolean Press(String verb) => menu.Execute(menu.Items().Single(item => verb == "confirm"
        ? item.Parameter.Split('|')[1].StartsWith("confirm", StringComparison.Ordinal)
        : item.Parameter.EndsWith("|" + verb, StringComparison.Ordinal)).Parameter);

    [Test]
    public void Open_WhileActive_DoesNotToggle()
    {
        Press("indefinite");
        menu.Open();
        Assert.That(sessions.Snapshot.Active, Is.Not.Null);
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Stop").And.Not.Contain("Duration"));
    }

    [Test]
    public void InactiveRoot_ShowsOnlyStartOptionsAndSettings() => Assert.That(menu.Items().Select(item => item.Label), Is.EqualTo(new[] { "Indefinitely", "Duration", "Time", "Presets", "Settings" }));

    [Test]
    public void Duration_LeavingDraft_DoesNotStartProcess()
    {
        Press("duration");
        Press("hourUp");
        menu.Open();
        Assert.That(factory.Started, Is.Empty);
        Assert.That(menu.View, Is.EqualTo(MenuView.Root));
    }

    [Test]
    public void Duration_Start_UsesDraftAndCloses()
    {
        Press("duration");
        Press("hourUp");
        Assert.That(Press("confirm"), Is.True);
        Assert.That(sessions.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(90)));
    }

    [Test]
    public void Duration_Reset_ReturnsToZeroAndRequiresPositiveValue()
    {
        Press("duration");
        Press("hourUp");
        Press("reset");

        Assert.That(menu.DraftLabel, Is.EqualTo("0h 00m"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Press("confirm"));
        Press("minuteUp");
        Assert.That(menu.DraftLabel, Is.EqualTo("0h 01m"));
    }

    [Test]
    public void Time_Commit_UsesNextClockOccurrence()
    {
        Press("time");
        Press("hourDown");
        Assert.That(Press("confirm"), Is.True);
        Assert.That(sessions.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [TestCase("time", false)]
    [TestCase("change", true)]
    public void Time_Reset_UsesCurrentLocalClockTime(String verb, Boolean active)
    {
        if (active)
        {
            sessions.StartDuration(TimeSpan.FromMinutes(30), sessions.Snapshot.Revision);
            menu.Open();
        }

        var zone = TimeZoneInfo.CreateCustomTimeZone("Test +02:00", TimeSpan.FromHours(2), "Test +02:00", "Test +02:00");
        menu = new(sessions, clock, () => zone);
        menu.Open();
        Press(verb);
        Press("hourUp");
        Press("reset");

        Assert.That(menu.DraftLabel, Is.EqualTo("14:00"));
    }

    [Test]
    public void ActiveTimedRoot_ShowsRelevantControls()
    {
        Press("duration");
        Press("confirm");
        Assert.That(menu.Items().Select(item => item.Label),
            Does.Contain("Add\n5 minutes").And.Contain("Add\n1 hour").And.Contain("Change\nend time"));
    }

    [Test]
    public void Presets_Pagination_CoversExactAgreedDurationsWithinEightSlots()
    {
        var values = new List<Int32>();
        foreach (var group in new[] { "minutes", "hours" })
        {
            menu.Open();
            Press("presets");
            Press(group);
            while (true)
            {
                Assert.That(menu.Items().Count, Is.LessThanOrEqualTo(8));
                values.AddRange(menu.Items().Select(item => item.Parameter.Split('|')[1])
                    .Where(verb => verb.StartsWith("preset", StringComparison.Ordinal)).Select(verb => Int32.Parse(verb[6..])));
                if (!menu.Items().Any(item => item.Parameter.EndsWith("|next", StringComparison.Ordinal)))
                {
                    break;
                }

                Press("next");
            }
        }
        Assert.That(values, Is.EqualTo(Enumerable.Range(1, 12).Select(i => i * 5).Concat(Enumerable.Range(2, 11).Select(i => i * 60))));
    }

    [Test]
    public void Execute_StalePageCommand_Rejects()
    {
        var old = menu.Items().Single(item => item.Label == "Indefinitely").Parameter;
        Press("duration");
        Assert.Throws<SessionConflictException>(() => menu.Execute(old));
        Assert.That(factory.Started, Is.Empty);
    }

    [Test]
    public void Confirm_AfterAnotherDeviceStarts_RejectsAndSynchronizes()
    {
        Press("duration");
        var confirm = menu.Items().Single(item => item.Parameter.Split('|')[1].StartsWith("confirm", StringComparison.Ordinal)).Parameter;
        sessions.StartIndefinite(0);
        Assert.Throws<SessionConflictException>(() => menu.Execute(confirm));
        Assert.That(menu.Synchronize(), Is.True);
        Assert.That(menu.View, Is.EqualTo(MenuView.Root));
        Assert.That(sessions.Snapshot.Active?.Kind, Is.EqualTo(SessionKind.Indefinite));
    }

    [Test]
    public void ApplyAdjustment_NegativeAndLargeTicks_HandlesBoundsAndWraps()
    {
        Press("time");
        menu.ApplyAdjustment(menu.AdjustmentParameter("hour"), 25);
        menu.ApplyAdjustment(menu.AdjustmentParameter("minute"), -1);
        Assert.That(menu.DraftLabel, Is.EqualTo("14:59"));
    }

    [Test]
    public void Settings_Selection_PersistsForNextSession()
    {
        Press("settings");
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Mac +\ndisplay"));
        Press("modeMacAndDisplay");
        Assert.That(sessions.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacOnly));
        menu.Open();
        Press("indefinite");
        Assert.That(sessions.Snapshot.Active?.Mode, Is.EqualTo(KeepAwakeMode.MacOnly));
        menu.Open();
        Press("settings");
        Press("modeMacOnly");
        Assert.That(sessions.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
    }

    [Test]
    public void Settings_Notice_ShowsOnlyWhenModeChangeIsDeferred()
    {
        Press("settings");
        Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Applies next\nsession"));

        menu.Open();
        Press("indefinite");
        Press("settings");
        Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Applies next\nsession"));

        Press("modeMacAndDisplay");
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Applies next\nsession"));

        Press("modeMacOnly");
        Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Applies next\nsession"));
    }

    [Test]
    public void Settings_TimeFormatSelection_PersistsAndFormatsSelectorDraft()
    {
        Press("settings");
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("24-hour"));

        Press("timeFormat");
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("12-hour\nAM/PM"));
        Assert.That(sessions.Snapshot.TimeFormat, Is.EqualTo(TimeDisplayFormat.TwelveHour));
        Press("timeFormat");
        Assert.That(sessions.Snapshot.TimeFormat, Is.EqualTo(TimeDisplayFormat.TwentyFourHour));
        Press("timeFormat");
        Assert.That(sessions.Snapshot.TimeFormat, Is.EqualTo(TimeDisplayFormat.TwelveHour));

        menu.Open();
        Press("time");
        Assert.That(menu.DraftLabel, Is.EqualTo("1:00 PM"));
        Assert.That(sessions.Snapshot.TimeFormat, Is.EqualTo(TimeDisplayFormat.TwelveHour));
    }

    [Test]
    public void Settings_ModeSubiconCanBeHiddenAndShown()
    {
        Press("settings");
        var labels = menu.Items().Select(item => item.Label).ToArray();
        var modeLabel = sessions.Snapshot.PreferredMode == KeepAwakeMode.MacOnly ? "Mac only" : "Mac +\ndisplay";
        Assert.That(Array.IndexOf(labels, "Mode subicon\nshown"), Is.EqualTo(Array.IndexOf(labels, modeLabel) + 1));

        Press("modeBadgeShown");
        Assert.That(sessions.Snapshot.ShowModeBadge, Is.False);
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Mode subicon\nhidden"));

        Press("modeBadgeHidden");
        Assert.That(sessions.Snapshot.ShowModeBadge, Is.True);
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Mode subicon\nshown"));
    }

    [TestCase(0, "12:05 AM")]
    [TestCase(12, "12:05 PM")]
    [TestCase(13, "1:05 PM")]
    public void ClockTimeFormatter_UsesTwelveHourClock(Int32 hour, String expected) =>
        Assert.That(ClockTimeFormatter.Format(hour, 5, TimeDisplayFormat.TwelveHour), Is.EqualTo(expected));

    [Test]
    public void ChangeEndTime_LeavingDraft_PreservesExistingDeadline()
    {
        Press("duration");
        Press("confirm");
        var old = sessions.Snapshot.Active;
        Press("change");
        Press("hourUp");
        menu.Open();
        Assert.That(sessions.Snapshot.Active, Is.EqualTo(old));
    }

    [Test]
    public void Confirm_AfterSharedDraftChanges_RejectsStaleConfirmation()
    {
        Press("duration");
        var oldConfirmation = menu.Items().Single(item => item.Label == "Start").Parameter;
        Press("minuteUp");
        Assert.Throws<ArgumentException>(() => menu.Execute(oldConfirmation));
        Assert.That(factory.Started, Is.Empty);
    }

    [TestCase("duration")]
    [TestCase("time")]
    [TestCase("change")]
    public void Confirm_AfterReset_RejectsStaleConfirmationAndCommitsCurrentDraft(String verb)
    {
        if (verb == "change")
        {
            sessions.StartDuration(TimeSpan.FromMinutes(30), sessions.Snapshot.Revision);
            menu.Open();
        }

        Press(verb);
        Press("hourUp");
        var oldConfirmation = menu.Items().Single(item => item.Label is "Start" or "Save").Parameter;
        var originalSession = sessions.Snapshot.Active;
        var startedCount = factory.Started.Count;
        Press("reset");

        Assert.Throws<ArgumentException>(() => menu.Execute(oldConfirmation));
        Assert.That(sessions.Snapshot.Active, Is.EqualTo(originalSession));
        Assert.That(factory.Started, Has.Count.EqualTo(startedCount));

        if (verb == "duration")
        {
            Press("minuteUp");
        }

        Assert.That(Press("confirm"), Is.True);
        Assert.That(sessions.Snapshot.Remaining, Is.EqualTo(verb == "duration"
            ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(24)));
    }

    [Test]
    public void Error_InsideSelector_CanReadAllDetailsAndReturnToDraft()
    {
        Press("duration");
        Press("minuteUp");
        const String message = "Choose a positive duration within caffeinate's supported range. Please correct the selected hours and minutes.";
        sessions.ReportFailure(new InvalidOperationException(message));
        var draft = menu.DraftLabel;
        Press("error");
        var details = "";
        while (true)
        {
            details += menu.Items().Single(item => item.Parameter.EndsWith("|notice", StringComparison.Ordinal)).Label;
            if (!menu.Items().Any(item => item.Parameter.EndsWith("|next", StringComparison.Ordinal)))
            {
                break;
            }

            Press("next");
        }
        Assert.That(details, Is.EqualTo(message));
        Press("done");
        Assert.Multiple(() =>
        {
            Assert.That(menu.View, Is.EqualTo(MenuView.Duration));
            Assert.That(menu.DraftLabel, Is.EqualTo(draft));
            Assert.That(menu.Items(), Has.Count.LessThanOrEqualTo(8));
        });
    }

    [Test]
    public void Synchronize_AtExpiry_ReturnsToInactiveRoot()
    {
        Press("duration");
        Press("confirm");
        Press("change");
        clock.Advance(TimeSpan.FromHours(1));
        factory.Started.Single().Process.HasExited = true;
        sessions.Tick();
        Assert.That(menu.Synchronize(), Is.True);
        Assert.That(menu.View, Is.EqualTo(MenuView.Root));
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Indefinitely").And.Not.Contain("Stop"));
    }

    [TestCase("duration")]
    [TestCase("time")]
    [TestCase("settings")]
    [TestCase("presets")]
    [TestCase("error")]
    public void Views_DoNotDuplicateNativeBack(String verb)
    {
        if (verb == "error")
        {
            sessions.ReportFailure(new InvalidOperationException("A test error."));
        }

        Press(verb);
        Assert.That(menu.Items().Any(item => item.Parameter.EndsWith("|back", StringComparison.Ordinal)), Is.False);
        Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Back"));
        Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Cancel"));
    }

    [TestCase("duration")]
    [TestCase("time")]
    public void NativeBack_ReopeningDiscardsUnconfirmedDraftWithoutStarting(String verb)
    {
        Press(verb);
        Press("hourUp");
        menu.Open();
        Assert.That(menu.View, Is.EqualTo(MenuView.Root));
        Assert.That(factory.Started, Is.Empty);
        Press(verb);
        Assert.That(menu.DraftLabel, Is.EqualTo(verb == "duration" ? "0h 30m" : "13:00"));
    }
}