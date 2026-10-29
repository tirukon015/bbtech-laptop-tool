using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BBTech.Core.Identity;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// DETECT → Send to ITMS dialog: shows detected laptop details, the laptop's ITMS transmission
    /// status, and one action button (Send to ITMS, Try again, or Received).
    /// Submissions are idempotent using the stable Inspection ID.
    /// </summary>
    public sealed class ItmsSendWindow : Window
    {
        private readonly DetectionResult _detection;
        private readonly DeviceIdentity _identity;
        private readonly ItmsDevicePayload _payload;
        private readonly IItmsConnector _connector = ItmsConnection.Create();

        private readonly TextBlock _status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _message = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Border _statusBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0) };
        private readonly Button _send;
        private bool _busy;

        public ItmsSendWindow(DetectionResult detection)
        {
            _detection = detection ?? throw new ArgumentNullException(nameof(detection));
            _identity = IdentityFactory.From(detection);
            _payload = ItmsDevicePayload.From(detection);

            Title = "Send to ITMS";
            Width = Math.Min(560, SystemParameters.WorkArea.Width * 0.95);
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height * 0.95;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Background = Res("AppBackground");
            Foreground = Res("TextPrimary");

            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(new TextBlock { Text = "Send this laptop to ITMS", FontSize = 20, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock
            {
                Text = "Only this laptop is sent. ITMS adds it to its Registration Queue, where it is reviewed and registered.",
                Style = St("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
            });

            var card = new Border { Style = St("Card") };
            var rows = new StackPanel();
            AddRow(rows, "Serial number", _payload.SerialNumber);
            AddRow(rows, "Brand", _payload.Brand);
            AddRow(rows, "Model", _payload.Model);
            AddRow(rows, "Model number", _payload.ModelNumber);
            AddRow(rows, "CPU", _payload.Cpu);
            AddRow(rows, "Generation", _payload.Generation);
            AddRow(rows, "RAM", _payload.Ram);
            AddRow(rows, "Storage", _payload.Storage);
            AddRow(rows, "Battery health", _payload.BatteryHealth);
            AddRow(rows, "Identity", IdentitySummary());
            card.Child = rows;
            sp.Children.Add(card);

            var statusPanel = new StackPanel();
            statusPanel.Children.Add(_status);
            statusPanel.Children.Add(_message);
            _statusBox.Child = statusPanel;
            sp.Children.Add(_statusBox);

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            _send = new Button { Content = "Send to ITMS", Style = St("PrimaryButton"), MinWidth = 140, IsEnabled = false };
            _send.Click += async (s, e) => await SendAsync();
            var close = new Button { Content = "Close", Style = St("BaseButton"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            close.Click += (s, e) => Close();
            buttons.Children.Add(_send);
            buttons.Children.Add(close);
            sp.Children.Add(buttons);

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
            Loaded += async (s, e) => await LoadStatusAsync();
        }

        private string IdentitySummary()
        {
            var parts = new List<string>();
            if (_payload.SystemUuid != null) parts.Add("system UUID");
            if (_payload.BoardSerial != null) parts.Add("board serial");
            if (_payload.HardwareFingerprint != null) parts.Add("hardware fingerprint");
            return parts.Count == 0 ? null : "Serial number + " + string.Join(", ", parts);
        }

        private async Task LoadStatusAsync()
        {
            if (!_payload.IsValid)
            {
                Show("Cannot be sent", string.Join(" ", _payload.Problems) + " ITMS needs the serial number to identify the laptop.", "Fail");
                return;
            }

            SyncRecord record = null;
            try
            {
                var open = await QcStoreService.OpenAsync(_identity, readOnly: true);
                if (open.Ok)
                    record = await Task.Run(() =>
                    {
                        var match = open.Repository.FindDevice(_identity);
                        return match.Status == MatchStatus.Matched ? open.Repository.GetSyncRecord(SyncEntity.Device, match.Device.DeviceId) : null;
                    });
            }
            catch (Exception ex) { AppLog.Warn("ITMS status lookup failed", ex); }

            ShowRecord(record, null);
            if (!_connector.IsEnabled)
            {
                Show("ITMS connection not configured", "The ITMS connection is not set up in this version yet, so nothing can be sent. Open Settings -> ITMS (or set ITMS_URL and ITMS_API_KEY environment variables) to configure the server destination.", "Neutral");
                _send.IsEnabled = false;
                _send.ToolTip = "The ITMS connection is not configured yet. Configure ITMS in Settings.";
                ToolTipService.SetShowOnDisabled(_send, true);
            }
        }

        private async Task SendAsync()
        {
            if (_busy) return;
            _busy = true;
            _send.IsEnabled = false;
            Show("Sending to ITMS…", "Please wait. Submitting inspection to ITMS review queue...", "Neutral");

            try
            {
                var open = await QcStoreService.OpenAsync(_identity);
                if (!open.Ok)
                {
                    // Fallback to direct submission if local QC store is unreadable
                    var directSubmission = new ItmsSubmission(_detection.SubmissionId, _payload, _detection);
                    var directResult = await _connector.SendDeviceAsync(directSubmission);
                    if (directResult.Outcome == ItmsSendOutcome.Received)
                    {
                        Show("✓ ITMS received this laptop", "ITMS reference: " + directResult.Receipt + ". You can move the USB to the next laptop.", "Pass");
                        _send.IsEnabled = false;
                        _send.Content = "Received";
                    }
                    else
                    {
                        Show("Sending failed", directResult.Message + " Try again.", "Fail");
                        _send.IsEnabled = true;
                        _send.Content = "Try again";
                    }
                    return;
                }

                if (open.Repository.IsReadOnly)
                {
                    Show("Cannot be sent", open.Message, "Fail");
                    return;
                }

                var service = new ItmsTransmissionService(open.Repository, _connector);
                var laptop = await Task.Run(() => service.ResolveLaptop(_identity, IdentityFactory.SpecsFrom(_detection)));
                if (!laptop.Ok)
                {
                    Show("Cannot be sent", laptop.Message, "Fail");
                    return;
                }

                var outcome = await service.SendOrContinueAsync(laptop.Device, _detection);
                AppLog.Info("ITMS: " + (outcome.Record?.SyncStatus ?? "no record") + " (attempt " + (outcome.Record?.AttemptCount ?? 0) + ")");
                ShowRecord(outcome.Record, outcome.Message);
            }
            catch (Exception ex)
            {
                AppLog.Error("ITMS send failed", ex);
                Show("Sending did not complete", "Something went wrong. The laptop's status was saved; try again.", "Fail");
                _send.IsEnabled = _connector.IsEnabled;
            }
            finally { _busy = false; }
        }

        private void ShowRecord(SyncRecord r, string message)
        {
            string state = r?.SyncStatus ?? TransmissionState.Local;
            switch (state)
            {
                case TransmissionState.ItmsReceived:
                    Show("✓ ITMS received this laptop", "ITMS reference: " + r.RemoteId + ". You can move the USB to the next laptop.", "Pass");
                    _send.IsEnabled = false;
                    _send.Content = "Received";
                    return;
                case TransmissionState.Sent:
                    Show("Sent — waiting for ITMS to confirm", message ?? "The laptop is queued in ITMS.", "Warn");
                    _send.Content = "Check status";
                    break;
                case TransmissionState.Failed:
                    Show("Sending failed", (message ?? r.LastError) + " Try again; the same request is reused, so ITMS will not add the laptop twice.", "Fail");
                    _send.Content = "Try again";
                    break;
                case TransmissionState.Sending:
                    Show("Previous sending was interrupted", "Try again; the same request is reused, so ITMS will not add the laptop twice.", "Warn");
                    _send.Content = "Try again";
                    break;
                default:
                    Show("Ready to send", "Click Send to ITMS to submit this laptop to the review queue.", "Neutral");
                    _send.Content = "Send to ITMS";
                    break;
            }
            _send.IsEnabled = _connector.IsEnabled && _payload.IsValid;
        }

        private void Show(string status, string message, string tone)
        {
            _status.Text = status;
            _message.Text = message ?? "";
            _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            _statusBox.Background = Res(tone + "Soft");
            _status.Foreground = tone == "Neutral" ? Res("TextPrimary") : Res(tone + "Brush");
        }

        private static void AddRow(StackPanel parent, string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, Style = St("Muted") });
            var v = new TextBlock
            {
                Text = value ?? "Not detected", FontSize = 14, TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold, Foreground = value == null ? Res("NeutralBrush") : Res("TextPrimary"),
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            parent.Children.Add(g);
        }

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static Style St(string k) => (Style)Application.Current.FindResource(k);
    }
}
