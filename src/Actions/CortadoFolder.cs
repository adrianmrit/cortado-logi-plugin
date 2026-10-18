using System.Diagnostics;

using Cortado.Core;

namespace Loupedeck.CortadoPlugin
{
    public sealed class CortadoFolder : PluginDynamicFolder
    {
        private CortadoMenu menu = null!;
        private String? lastStatus;
        private Boolean? lastIsActive;
        private Boolean active;
        private Boolean hadError;
        private readonly TimeProvider clock = TimeProvider.System;
        private Int64 lastPublishedAt;
        private CortadoPlugin Owner => (CortadoPlugin)this.Plugin;

        public CortadoFolder()
        {
            this.DisplayName = "Cortado";
            this.Description = "Keep your Mac awake. Open to start, stop, or change your session.";
            this.GroupName = "Cortado";
            this.SupportedOs = LoupedeckOperatingSystem.Mac;
            this.SupportedDevices = DeviceType.Loupedeck70;
        }

        public override Boolean Load()
        {
            this.menu = new CortadoMenu(this.Owner.Sessions, this.clock, () => TimeZoneInfo.Local);
            this.menu.Open();
            this.Owner.Refreshed -= this.OnRefresh;
            this.Owner.Refreshed += this.OnRefresh;
            this.lastStatus = null;
            this.OnRefresh();
            return true;
        }

        public override Boolean Unload()
        {
            this.Owner.Refreshed -= this.OnRefresh;
            this.active = false;
            return true;
        }

        public override Boolean Activate()
        {
            lock (this.Owner.SyncRoot)
            {
                this.active = true;
                try
                {
                    this.Owner.Sessions.Tick();
                }
                catch (Exception exception)
                {
                    this.Owner.Sessions.ReportFailure(exception);
                }
                this.menu.Open();
                this.lastStatus = null;
            }
            this.OnRefresh();
            this.InvalidateLayout();
            return true;
        }

        public override Boolean Deactivate()
        {
            lock (this.Owner.SyncRoot)
            {
                this.active = false;
                this.menu.Open();
                return true;
            }
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override String GetButtonDisplayName(PluginImageSize imageSize) =>
            this.Owner.CurrentSnapshot.Status;

        public override BitmapImage GetButtonImage(PluginImageSize imageSize) =>
            CupRenderer.Entry(imageSize, this.Owner.CurrentSnapshot);

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType _)
        {
            lock (this.Owner.SyncRoot)
            {
                var items = this.menu.Items();
                if (!this.menu.IsSelector)
                {
                    return items.Select(item => this.CreateCommandName(item.Parameter)).ToArray();
                }

                String Slot(String verb) => this.CreateCommandName(items.Single(item =>
                    item.Parameter[(item.Parameter.IndexOf('|') + 1)..].StartsWith(verb, StringComparison.Ordinal)).Parameter);
                var error = items.FirstOrDefault(item => item.Parameter.EndsWith("|error", StringComparison.Ordinal));
                return new[]
                {
                    Slot("value"), Slot("confirm"),
                    Slot("minuteDown"), error is null ? String.Empty : this.CreateCommandName(error.Parameter), Slot("minuteUp"),
                    Slot("hourDown"), String.Empty, Slot("hourUp")
                };
            }
        }

        public override IEnumerable<String> GetEncoderRotateActionNames(DeviceType _)
        {
            lock (this.Owner.SyncRoot)
            {
                return this.menu.IsSelector
                    ? new[] { this.CreateAdjustmentName(this.menu.AdjustmentParameter("hour")), this.CreateAdjustmentName(this.menu.AdjustmentParameter("minute")) }
                    : Array.Empty<String>();
            }
        }

        public override IEnumerable<String> GetEncoderPressActionNames(DeviceType _) => Array.Empty<String>();

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
        {
            lock (this.Owner.SyncRoot)
            {
                return this.menu.Items().FirstOrDefault(item => item.Parameter == actionParameter)?.Label ?? "Menu changed";
            }
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            lock (this.Owner.SyncRoot)
            {
                var item = this.menu.Items().FirstOrDefault(item => item.Parameter == actionParameter);
                return item is null
                    ? CupRenderer.Entry(imageSize, this.Owner.Sessions.Snapshot)
                    : CupRenderer.Menu(imageSize, item);
            }
        }

        public override String GetAdjustmentDisplayName(String actionParameter, PluginImageSize imageSize) =>
            actionParameter.EndsWith("|hour", StringComparison.Ordinal) ? "Hours" : "Minutes";

        public override String GetAdjustmentValue(String actionParameter)
        {
            lock (this.Owner.SyncRoot)
            {
                return this.menu.DraftLabel;
            }
        }

        public override void RunCommand(String actionParameter)
        {
            var watch = Stopwatch.StartNew();
            var close = false;
            lock (this.Owner.SyncRoot)
            {
                try
                {
                    close = this.menu.Execute(actionParameter);
                    if (close)
                    {
                        this.active = false;
                    }
                }
                catch (Exception exception)
                {
                    this.Owner.Sessions.ReportFailure(exception);
                    this.menu.Synchronize();
                }
            }
            if (close)
            {
                var verified = watch.Elapsed.TotalMilliseconds;
                try
                {
                    this.Close();
                    PluginLog.Verbose($"Session command verified in {verified:F1} ms; folder close returned in {watch.Elapsed.TotalMilliseconds - verified:F1} ms.");
                }
                catch (Exception exception)
                {
                    this.Owner.Sessions.ReportFailure(exception);
                    close = false;
                }
            }
            this.UpdateStatus();
            if (!close)
            {
                this.InvalidateLayout();
            }
        }

        public override void ApplyAdjustment(String actionParameter, Int32 diff)
        {
            lock (this.Owner.SyncRoot)
            {
                try
                {
                    this.menu.ApplyAdjustment(actionParameter, diff);
                }
                catch (Exception exception)
                {
                    this.Owner.Sessions.ReportFailure(exception);
                }
            }
            this.InvalidateLayout();
        }

        private void OnRefresh()
        {
            Boolean layoutChanged;
            Boolean outerChanged;
            Boolean imagesVisible;
            lock (this.Owner.SyncRoot)
            {
                if (this.Owner.ExistingSnapshot is not { } snapshot)
                {
                    return;
                }

                var hasError = snapshot.Error is not null;
                var errorChanged = hasError != this.hadError;
                this.hadError = hasError;
                layoutChanged = this.active && (this.menu.Synchronize() || errorChanged);
                var status = snapshot.Status;
                outerChanged = status != this.lastStatus || snapshot.IsActive != this.lastIsActive ||
                    this.clock.GetElapsedTime(this.lastPublishedAt) >= TimeSpan.FromSeconds(5);
                if (outerChanged)
                {
                    this.lastStatus = status;
                    this.lastIsActive = snapshot.IsActive;
                    this.lastPublishedAt = this.clock.GetTimestamp();
                }
                imagesVisible = this.active;
            }
            if (layoutChanged)
            {
                this.InvalidateLayout();
            }

            if (outerChanged)
            {
                this.Owner.OnActionImageChanged(GenericActionNames.DynamicFolder, this.Name, false);
                if (imagesVisible && !layoutChanged)
                {
                    this.InvalidateImages();
                }
            }
        }

        private void UpdateStatus()
        {
            this.Owner.PublishStatus();
            lock (this.Owner.SyncRoot)
            {
                this.lastStatus = null;
            }

            this.OnRefresh();
        }

        private void InvalidateLayout()
        {
            this.ButtonActionNamesChanged();
            this.EncoderActionNamesChanged();
            this.InvalidateImages();
        }

        private void InvalidateImages()
        {
            this.CommandImageChanged(null!);
            this.AdjustmentValueChanged(null!);
        }
    }
}