using System.Globalization;

namespace Cortado.Core;

public sealed record MenuItem(String Parameter, String Label);
public enum MenuView { Root, Presets, Minutes, Hours, Duration, Time, ChangeEndTime, Settings, Info }

public sealed class CortadoMenu(SessionController sessions, TimeProvider clock, Func<TimeZoneInfo> timeZone)
{
    private Int64 generation;
    private Int64 sessionRevision;
    private Int32 page;
    private Int64 durationMinutes = 30;
    private Int32 hour;
    private Int32 minute;
    private Int64 draftRevision;
    private MenuView infoParent;
    private String info = "";

    public MenuView View { get; private set; }
    public String? Notice { get; private set; }
    public Boolean IsSelector => View is MenuView.Duration or MenuView.Time or MenuView.ChangeEndTime;

    public void Open()
    {
        View = MenuView.Root;
        sessionRevision = sessions.Snapshot.Revision;
        page = 0;
        Notice = null;
        generation++;
    }

    public Boolean Synchronize()
    {
        if (sessions.Snapshot.Revision == sessionRevision)
        {
            return false;
        }

        var editing = IsSelector;
        Open();
        if (editing)
        {
            Notice = "Session changed. Reopen selector.";
        }

        return true;
    }

    public IReadOnlyList<MenuItem> Items()
    {
        var snapshot = sessions.Snapshot;
        var items = new List<(String Verb, String Label)>();
        switch (View)
        {
            case MenuView.Root:
                if (snapshot.CanControl && !snapshot.IsActive)
                {
                    items.AddRange([("indefinite", "Indefinitely"), ("duration", "Duration"), ("time", "Time"), ("presets", "Presets")]);
                }
                else if (snapshot.CanControl)
                {
                    items.Add(("stop", snapshot.ExternalSessions.Count + (snapshot.Active is null ? 0 : 1) > 1 ? "Stop all" : "Stop"));
                    if (snapshot.CanEdit)
                    {
                        items.AddRange([("extend5", "Add\n5 minutes"), ("extend60", "Add\n1 hour"), ("change", "Change\nend time")]);
                    }
                }
                items.Add(("settings", "Settings"));
                if (snapshot.Error is not null || Notice is not null)
                {
                    items.Add(("error", "View error\nor info"));
                }

                break;
            case MenuView.Presets:
                items.AddRange([("minutes", "Minutes"), ("hours", "Hours")]);
                break;
            case MenuView.Minutes:
            case MenuView.Hours:
                var values = View == MenuView.Minutes
                    ? Enumerable.Range(1, 12).Select(value => value * 5).ToArray()
                    : Enumerable.Range(2, 11).Select(value => value * 60).ToArray();
                foreach (var value in values.Skip(page * 5).Take(5))
                {
                    items.Add(($"preset{value}", value < 60 ? $"{value} min" : $"{value / 60} hour{(value == 60 ? "" : "s")}"));
                }

                if (page > 0)
                {
                    items.Add(("previous", "Previous"));
                }

                if ((page + 1) * 5 < values.Length)
                {
                    items.Add(("next", "Next"));
                }

                break;
            case MenuView.Settings:
                items.Add((snapshot.PreferredMode == KeepAwakeMode.MacOnly ? "modeMacOnly" : "modeMacAndDisplay",
                    snapshot.PreferredMode == KeepAwakeMode.MacOnly ? "Mac only" : "Mac +\ndisplay"));
                items.Add((snapshot.ShowModeBadge ? "modeBadgeShown" : "modeBadgeHidden",
                    snapshot.ShowModeBadge ? "Mode subicon\nshown" : "Mode subicon\nhidden"));
                items.Add(("timeFormat", snapshot.TimeFormat == TimeDisplayFormat.TwentyFourHour ? "24-hour" : "12-hour\nAM/PM"));
                if (snapshot.IsActive && snapshot.Active?.Mode != snapshot.PreferredMode)
                {
                    items.Add(("notice", "Applies next\nsession"));
                }

                break;
            case MenuView.Info:
                const Int32 chunkSize = 36;
                items.Add(("done", "Done"));
                items.Add(("notice", info.Substring(page * chunkSize, Math.Min(chunkSize, info.Length - page * chunkSize))));
                if (page > 0)
                {
                    items.Add(("previous", "Previous"));
                }

                if ((page + 1) * chunkSize < info.Length)
                {
                    items.Add(("next", "Next"));
                }

                break;
            default:
                items.AddRange([
                    ("value", DraftLabel + "\n" + (View == MenuView.Duration ? "Duration" : "End time")),
                    ("hourUp", "Hour +"), ("hourDown", "Hour -"),
                    ("minuteUp", "Minute +"), ("minuteDown", "Minute -"),
                    ($"confirm{draftRevision}", View == MenuView.ChangeEndTime ? "Save" : "Start"),
                    ("reset", "Reset")
                ]);
                if (snapshot.Error is not null)
                {
                    items.Add(("error", "View error"));
                }

                break;
        }
        return items.Select(item => new MenuItem(Parameter(item.Verb), item.Label)).ToArray();
    }

    public String DraftLabel => View == MenuView.Duration
        ? $"{durationMinutes / 60}h {durationMinutes % 60:00}m"
        : ClockTimeFormatter.Format(hour, minute, sessions.Snapshot.TimeFormat);

    public String AdjustmentParameter(String unit) => Parameter(unit);

    public Boolean Execute(String parameter)
    {
        var verb = Parse(parameter);
        if (verb is "notice" or "value")
        {
            return false;
        }

        if (verb == "done")
        {
            Navigate(infoParent);
            return false;
        }
        switch (verb)
        {
            case "indefinite":
                sessions.StartIndefinite(sessionRevision);
                return Finish();
            case "stop":
                sessions.Stop(sessionRevision);
                return Finish();
            case "extend5":
                sessions.Extend(TimeSpan.FromMinutes(5), sessionRevision);
                return Finish();
            case "extend60":
                sessions.Extend(TimeSpan.FromHours(1), sessionRevision);
                return Finish();
            case "duration":
                durationMinutes = 30;
                Navigate(MenuView.Duration);
                break;
            case "time":
            case "change":
                var initial = verb == "change" && sessions.Snapshot.Active?.Deadline is { } deadline
                    ? deadline : clock.GetUtcNow().AddHours(1);
                var local = TimeZoneInfo.ConvertTime(initial, timeZone());
                hour = local.Hour;
                minute = local.Minute;
                Navigate(verb == "change" ? MenuView.ChangeEndTime : MenuView.Time);
                break;
            case "presets":
                Navigate(MenuView.Presets);
                break;
            case "minutes":
                Navigate(MenuView.Minutes);
                break;
            case "hours":
                Navigate(MenuView.Hours);
                break;
            case "settings":
                Navigate(MenuView.Settings);
                break;
            case "error":
                infoParent = View;
                info = Notice ?? sessions.Snapshot.Error ?? "No current error.";
                Navigate(MenuView.Info);
                break;
            case "modeMacOnly":
                sessions.SetPreference(KeepAwakeMode.MacAndDisplay);
                break;
            case "modeMacAndDisplay":
                sessions.SetPreference(KeepAwakeMode.MacOnly);
                break;
            case "timeFormat":
                sessions.SetTimeFormat(sessions.Snapshot.TimeFormat == TimeDisplayFormat.TwentyFourHour
                    ? TimeDisplayFormat.TwelveHour : TimeDisplayFormat.TwentyFourHour);
                break;
            case "modeBadgeShown":
                sessions.SetShowModeBadge(false);
                break;
            case "modeBadgeHidden":
                sessions.SetShowModeBadge(true);
                break;
            case "previous":
                page--;
                generation++;
                break;
            case "next":
                page++;
                generation++;
                break;
            case "hourUp":
                Adjust("hour", 1);
                break;
            case "hourDown":
                Adjust("hour", -1);
                break;
            case "minuteUp":
                Adjust("minute", 1);
                break;
            case "minuteDown":
                Adjust("minute", -1);
                break;
            case "reset":
                if (View == MenuView.Duration)
                {
                    durationMinutes = 0;
                }
                else
                {
                    var currentLocal = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone());
                    hour = currentLocal.Hour;
                    minute = currentLocal.Minute;
                }

                draftRevision++;
                break;
            case var confirm when confirm == $"confirm{draftRevision}":
                if (!IsSelector)
                {
                    throw new InvalidOperationException("Open a selector first.");
                }

                if (View == MenuView.Duration)
                {
                    sessions.StartDuration(TimeSpan.FromMinutes(durationMinutes), sessionRevision);
                }
                else
                {
                    var end = LocalTime.NextOccurrence(clock.GetUtcNow(), timeZone(), hour, minute);
                    if (View == MenuView.ChangeEndTime)
                    {
                        sessions.ChangeEndTime(end, sessionRevision);
                    }
                    else
                    {
                        sessions.StartUntil(end, sessionRevision);
                    }
                }
                return Finish();
            default:
                if (!verb.StartsWith("preset", StringComparison.Ordinal) ||
                    !Int32.TryParse(verb.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                {
                    throw new ArgumentException("Unknown Cortado menu command.");
                }

                sessions.StartDuration(TimeSpan.FromMinutes(minutes), sessionRevision);
                return Finish();
        }
        return false;
    }

    public void ApplyAdjustment(String parameter, Int32 diff) => Adjust(Parse(parameter, adjustment: true), diff);

    private void Adjust(String unit, Int32 diff)
    {
        if (!IsSelector || unit is not ("hour" or "minute"))
        {
            throw new InvalidOperationException("Open a duration or time selector before adjusting.");
        }

        if (View == MenuView.Duration)
        {
            var updated = checked(durationMinutes + (Int64)diff * (unit == "hour" ? 60 : 1));
            if (updated < 0 || updated > Int32.MaxValue / 60)
            {
                throw new ArgumentOutOfRangeException(nameof(diff), "Duration is outside caffeinate's supported range.");
            }

            durationMinutes = updated;
        }
        else if (unit == "hour")
        {
            hour = (Int32)(((Int64)hour + diff) % 24 + 24) % 24;
        }
        else
        {
            minute = (Int32)(((Int64)minute + diff) % 60 + 60) % 60;
        }

        draftRevision++;
    }

    private String Parse(String parameter, Boolean adjustment = false)
    {
        var separator = parameter.IndexOf('|');
        if (separator < 0 || !Int64.TryParse(parameter.AsSpan(0, separator), out var version) || version != generation)
        {
            throw new SessionConflictException();
        }

        var verb = parameter[(separator + 1)..];
        return !adjustment && !Items().Any(item => item.Parameter == parameter)
            ? throw new ArgumentException("This menu option is no longer available.")
            : verb;
    }

    private String Parameter(String verb) => $"{generation}|{verb}";

    private void Navigate(MenuView view)
    {
        View = view;
        page = 0;
        Notice = null;
        generation++;
    }

    private Boolean Finish()
    {
        Open();
        return true;
    }
}