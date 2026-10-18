using Cortado.Core;

namespace Loupedeck.CortadoPlugin
{
    public sealed class CortadoPlugin : Plugin
    {
        private SessionController? sessions;
        internal SessionController Sessions
        {
            get
            {
                if (Volatile.Read(ref this.sessions) is { } current)
                {
                    return current;
                }

                lock (this.SyncRoot)
                {
                    var coordinator = this.sessions;
                    if (coordinator is null)
                    {
                        coordinator = new SessionController(
                            new JsonStateStore(Path.Combine(this.GetPluginDataDirectory(), "state.json")),
                            new CaffeinateProcessFactory(), new MacContinuousClock(),
                            exception =>
                            {
                                PluginLog.Error(exception, "Cortado operation failed");
                            }, new MacCaffeinateMonitor());
                        try
                        {
                            coordinator.Initialize();
                        }
                        catch (Exception exception)
                        {
                            coordinator.ReportFailure(exception);
                        }
                        Volatile.Write(ref this.sessions, coordinator);
                    }
                    return coordinator;
                }
            }
        }
        internal Object SyncRoot { get; } = new();
        internal event Action? Refreshed;
        internal SessionSnapshot? ExistingSnapshot => Volatile.Read(ref this.sessions)?.Snapshot;
        internal SessionSnapshot CurrentSnapshot
        {
            get
            {
                var coordinator = this.Sessions;
                var snapshot = coordinator.Snapshot;
                if (coordinator.NeedsRefresh && Interlocked.CompareExchange(ref this.refreshQueued, 1, 0) == 0)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        { this.Refresh(); }
                        finally { Interlocked.Exchange(ref this.refreshQueued, 0); }
                    });
                }

                return snapshot;
            }
        }
        private Timer? timer;
        private String? timeZoneLink;
        private Byte[]? timeZoneData;
        private Int32 refreshing;
        private Int32 refreshQueued;
        private Boolean hasPublishedStatus;
        private String? lastPublishedError;

        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        public CortadoPlugin() => PluginLog.Init(this.Log);

        public override void Load()
        {
            _ = this.Sessions;
            lock (this.SyncRoot)
            {
                this.timer ??= new Timer(_ => this.Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            this.PublishStatus();
        }

        internal void PublishStatus()
        {
            var coordinator = Volatile.Read(ref this.sessions);
            if (coordinator is null)
            {
                return;
            }

            var error = coordinator.Snapshot.Error;
            lock (this.SyncRoot)
            {
                if (!ReferenceEquals(coordinator, this.sessions))
                {
                    return;
                }

                if (this.hasPublishedStatus && this.lastPublishedError == error)
                {
                    return;
                }

                this.hasPublishedStatus = true;
                this.lastPublishedError = error;
            }
            this.OnPluginStatusChanged(error is null ? Loupedeck.PluginStatus.Normal : Loupedeck.PluginStatus.Error, error ?? "Ready");
        }

        private void Refresh()
        {
            if (Interlocked.CompareExchange(ref this.refreshing, 1, 0) != 0)
            {
                return;
            }

            SessionController? coordinator = null;
            try
            {
                lock (this.SyncRoot)
                {
                    if (this.timer is null)
                    {
                        return;
                    }

                    coordinator = this.sessions;
                }
                if (coordinator is null)
                {
                    return;
                }

                var currentTimeZoneFile = new FileInfo("/etc/localtime");
                var currentTimeZoneLink = currentTimeZoneFile.LinkTarget;
                var currentTimeZoneData = File.ReadAllBytes(currentTimeZoneFile.FullName);
                if (currentTimeZoneLink != this.timeZoneLink
                    || this.timeZoneData is null
                    || !currentTimeZoneData.AsSpan().SequenceEqual(this.timeZoneData))
                {
                    this.timeZoneLink = currentTimeZoneLink;
                    this.timeZoneData = currentTimeZoneData;
                    TimeZoneInfo.ClearCachedData();
                }
                coordinator.Tick();
                Action? refreshed;
                lock (this.SyncRoot)
                {
                    if (this.timer is null || !ReferenceEquals(coordinator, this.sessions))
                    {
                        return;
                    }

                    var interval = coordinator.Snapshot.IsExpiring ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
                    this.timer.Change(interval, interval);
                    refreshed = this.Refreshed;
                }
                this.PublishStatus();
                refreshed?.Invoke();
            }
            catch (Exception exception)
            {
                coordinator?.ReportFailure(exception);
            }
            finally { Interlocked.Exchange(ref this.refreshing, 0); }
        }

        public override void Unload()
        {
            lock (this.SyncRoot)
            {
                this.timer?.Dispose();
                this.timer = null;
                try
                {
                    this.sessions?.Dispose();
                }
                catch (Exception exception)
                {
                    PluginLog.Error(exception, "Could not preserve Cortado's session during unload");
                    this.OnPluginStatusChanged(Loupedeck.PluginStatus.Error, exception.Message);
                }
                finally
                {
                    Volatile.Write(ref this.sessions, null);
                    this.hasPublishedStatus = false;
                }
            }
        }
    }
}