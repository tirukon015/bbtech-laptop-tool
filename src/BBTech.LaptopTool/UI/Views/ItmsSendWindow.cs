using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// status, submission history, and action buttons:
    ///   - Initial: [ Send to ITMS ] [ Close ]
    ///   - On Failure: [ Try Again ] [ Resend as New ] [ Close ]
    ///   - On Success: [ Resend as New ] [ Close ]
    /// Strict distinction between "Try Again" (idempotent retry with same request ID) and "Resend as New" (new request ID and history entry).
    /// Responsive across laptop displays (13"–16", 1366x768, 1080p, DPI scaling).
    /// </summary>
    public sealed class ItmsSendWindow : Window
    {
        private enum SendMode
        {
            SendOrContinue,
            RetrySameRequest,
            ResendAsNew,
            CheckStatus,
        }

        private readonly DetectionResult _detection;
        private readonly DeviceIdentity _identity;
        private readonly ItmsDevicePayload _payload;
        private readonly IItmsConnector _connector = ItmsConnection.Create();

        private readonly TextBlock _status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _message = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly StackPanel _historyList = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Border _statusBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0) };

        private readonly Button _btnPrimary;
        private readonly Button _btnResend;
        private readonly Button _btnClose;
        private bool _busy;
        private string _currentState = TransmissionState.Local;

        public ItmsSendWindow(DetectionResult detection)
        {
            _detection = detection ?? throw new ArgumentNullException(nameof(detection));
            _identity = IdentityFactory.From(detection);
            _payload = ItmsDevicePayload.From(detection);

            Title = "Send to ITMS";
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(600, Math.Max(380, workArea.Width * 0.90));
            MinWidth = 360;
            MaxHeight = Math.Max(380, workArea.Height * 0.92);
            MinHeight = 300;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Background = Res("AppBackground");
            Foreground = Res("TextPrimary");

            // Main 3-row layout: fixed header, scrollable body, fixed action bar footer
            var rootGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Header
            var headerPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            headerPanel.Children.Add(new TextBlock { Text = "Send this laptop to ITMS", FontSize = 19, FontWeight = FontWeights.SemiBold });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Only this laptop is sent. ITMS adds it to its Registration Queue, where it is reviewed and registered.",
                Style = St("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetRow(headerPanel, 0);
            rootGrid.Children.Add(headerPanel);

            // 2. Scrollable Body
            var bodyStack = new StackPanel();

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
            bodyStack.Children.Add(card);

            var statusPanel = new StackPanel();
            statusPanel.Children.Add(_status);
            statusPanel.Children.Add(_message);
            statusPanel.Children.Add(_historyList);
            _statusBox.Child = statusPanel;
            bodyStack.Children.Add(_statusBox);

            var scrollViewer = new ScrollViewer
            {
                Content = bodyStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(scrollViewer, 1);
            rootGrid.Children.Add(scrollViewer);

            // 3. Fixed Footer with Responsive Action Buttons
            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };

            _btnPrimary = new Button
            {
                Content = "Send to ITMS",
                Style = St("PrimaryButton"),
                MinWidth = 120,
                Margin = new Thickness(6, 4, 0, 4),
                IsEnabled = false
            };
            _btnPrimary.Click += async (s, e) => await HandlePrimaryClickAsync();

            _btnResend = new Button
            {
                Content = "Resend as New",
                Style = St("BaseButton"),
                MinWidth = 130,
                Margin = new Thickness(6, 4, 0, 4),
                Visibility = Visibility.Collapsed
            };
            _btnResend.Click += async (s, e) => await ExecuteSendAsync(SendMode.ResendAsNew);

            _btnClose = new Button
            {
                Content = "Close",
                Style = St("BaseButton"),
                MinWidth = 90,
                Margin = new Thickness(6, 4, 0, 4),
                IsCancel = true
            };
            _btnClose.Click += (s, e) => Close();

            buttons.Children.Add(_btnPrimary);
            buttons.Children.Add(_btnResend);
            buttons.Children.Add(_btnClose);

            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);

            Grid.SetRow(footer, 2);
            rootGrid.Children.Add(footer);

            Content = rootGrid;
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
                _btnPrimary.IsEnabled = false;
                _btnResend.IsEnabled = false;
                return;
            }

            SyncRecord record = null;
            List<SyncRecord> history = null;
            try
            {
                var open = await QcStoreService.OpenAsync(_identity, readOnly: true);
                if (open.Ok)
                {
                    var match = open.Repository.FindDevice(_identity);
                    if (match.Status == MatchStatus.Matched)
                    {
                        record = await Task.Run(() => open.Repository.GetSyncRecord(SyncEntity.Device, match.Device.DeviceId));
                        history = await Task.Run(() => open.Repository.GetSyncHistory(SyncEntity.Device, match.Device.DeviceId));
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn("ITMS status lookup failed", ex); }

            ShowRecord(record, null, history);
            if (!_connector.IsEnabled)
            {
                Show("ITMS connection not configured", "The ITMS connection is not set up in this version yet, so nothing can be sent. Open Settings -> ITMS (or set ITMS_URL and ITMS_API_KEY environment variables) to configure the server destination.", "Neutral", history);
                _btnPrimary.IsEnabled = false;
                _btnResend.IsEnabled = false;
                _btnPrimary.ToolTip = "The ITMS connection is not configured yet. Configure ITMS in Settings.";
                ToolTipService.SetShowOnDisabled(_btnPrimary, true);
            }
        }

        private async Task HandlePrimaryClickAsync()
        {
            if (_currentState == TransmissionState.Failed || _currentState == TransmissionState.Sending)
            {
                await ExecuteSendAsync(SendMode.RetrySameRequest);
            }
            else if (_currentState == TransmissionState.Sent)
            {
                await ExecuteSendAsync(SendMode.CheckStatus);
            }
            else
            {
                await ExecuteSendAsync(SendMode.SendOrContinue);
            }
        }

        private async Task ExecuteSendAsync(SendMode mode)
        {
            if (_busy) return;
            _busy = true;
            _btnPrimary.IsEnabled = false;
            _btnResend.IsEnabled = false;

            string actionMsg = mode == SendMode.ResendAsNew
                ? "Submitting as new inspection to ITMS review queue..."
                : mode == SendMode.RetrySameRequest
                    ? "Retrying submission with same request ID..."
                    : mode == SendMode.CheckStatus
                        ? "Checking receipt status with ITMS..."
                        : "Submitting inspection to ITMS review queue...";

            Show(mode == SendMode.ResendAsNew ? "Resending to ITMS…" : "Sending to ITMS…",
                 "Please wait. " + actionMsg, "Neutral");

            try
            {
                var open = await QcStoreService.OpenAsync(_identity);
                if (!open.Ok)
                {
                    // Fallback to direct submission if local QC store is unreadable
                    string reqId = Guid.NewGuid().ToString("D");
                    _detection.SubmissionId = reqId;
                    var directSubmission = new ItmsSubmission(reqId, _payload, _detection);
                    var directResult = await _connector.SendDeviceAsync(directSubmission);
                    if (directResult.Outcome == ItmsSendOutcome.Received)
                    {
                        _currentState = TransmissionState.ItmsReceived;
                        Show("✓ ITMS received this laptop", "ITMS reference: " + directResult.Receipt + ".\nYou can move the USB to the next laptop, or click [ Resend as New ] to submit again.", "Pass");
                        _btnPrimary.Visibility = Visibility.Collapsed;
                        _btnResend.Visibility = Visibility.Visible;
                        _btnResend.Style = St("PrimaryButton");
                        _btnResend.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    }
                    else
                    {
                        _currentState = TransmissionState.Failed;
                        Show("Sending failed", directResult.Message + "\n\n• Click [ Try Again ] to retry.\n• Click [ Resend as New ] to submit with a new request ID.", "Fail");
                        _btnPrimary.Visibility = Visibility.Visible;
                        _btnPrimary.Content = "Try Again";
                        _btnPrimary.Style = St("PrimaryButton");
                        _btnPrimary.IsEnabled = true;
                        _btnResend.Visibility = Visibility.Visible;
                        _btnResend.Style = St("BaseButton");
                        _btnResend.IsEnabled = true;
                    }
                    return;
                }

                if (open.Repository.IsReadOnly)
                {
                    Show("Cannot be sent", open.Message, "Fail");
                    return;
                }

                var service = new ItmsTransmissionService(open.Repository, _connector) { Requirements = MasterDataService.Requirements };
                var laptop = await Task.Run(() => service.ResolveLaptop(_identity, IdentityFactory.SpecsFrom(_detection)));
                if (!laptop.Ok)
                {
                    Show("Cannot be sent", laptop.Message, "Fail");
                    return;
                }

                // When the BBTech server is reachable it is authoritative: upload pending results first, then
                // require the SERVER to report this laptop READY (latest final QC = PASS). Offline, the local
                // QC gate inside ItmsTransmissionService still applies.
                if (mode != SendMode.CheckStatus)
                {
                    var api = UsbSyncService.CreateClient();
                    if (api.IsConfigured && await api.CheckConnectivityAsync())
                    {
                        await UsbSyncService.RunNowAsync(SettingsService.Store.CurrentOperator);
                        var serverStatus = await api.GetDeviceStatusAsync(laptop.Device.DeviceId);
                        if (serverStatus != null && serverStatus.Readiness != "READY")
                        {
                            Show("Not ready for ITMS",
                                serverStatus.Readiness == "NOT_READY"
                                    ? "The BBTech server records QC FAIL as the latest decision for this laptop."
                                    : "The BBTech server has no final QC PASS for this laptop yet. Finish QC (and let the results sync), then try again.",
                                "Fail");
                            return;
                        }
                    }
                }

                ItmsTransmissionOutcome outcome;
                if (mode == SendMode.ResendAsNew)
                {
                    outcome = await service.ResendAsync(laptop.Device, _detection);
                }
                else if (mode == SendMode.RetrySameRequest)
                {
                    outcome = await service.RetryAsync(laptop.Device, _detection);
                }
                else if (mode == SendMode.CheckStatus)
                {
                    outcome = await service.CheckReceiptAsync(laptop.Device);
                }
                else
                {
                    outcome = await service.SendOrContinueAsync(laptop.Device, _detection);
                }

                try
                {
                    new BBTech.Core.Audit.AuditStore(AppPaths.AuditLogPath).Log(SettingsService.Store.CurrentOperator, BBTech.Core.Audit.AuditActions.ItmsSubmission,
                        "Device:" + laptop.Device.DeviceId, null, outcome.Record?.SyncStatus ?? "NOT SENT", mode + ": " + outcome.Message);
                }
                catch (Exception auditEx) { AppLog.Warn("ITMS audit failed: " + auditEx.Message); }
                AppLog.Info("ITMS: " + (outcome.Record?.SyncStatus ?? "no record") + " (attempt " + (outcome.Record?.AttemptCount ?? 0) + ", mode: " + mode + ")");
                var history = open.Repository.GetSyncHistory(SyncEntity.Device, laptop.Device.DeviceId);
                ShowRecord(outcome.Record, outcome.Message, history);
            }
            catch (Exception ex)
            {
                AppLog.Error("ITMS send failed", ex);
                Show("Sending did not complete", "Something went wrong: " + ex.Message + "\nThe laptop's status was saved; try again.", "Fail");
                _btnPrimary.IsEnabled = _connector.IsEnabled;
                _btnResend.IsEnabled = _connector.IsEnabled;
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShowRecord(SyncRecord r, string message, List<SyncRecord> history = null)
        {
            string state = r?.SyncStatus ?? TransmissionState.Local;
            _currentState = state;

            switch (state)
            {
                case TransmissionState.ItmsReceived:
                    string refText = !string.IsNullOrEmpty(r?.RemoteId) ? r.RemoteId : "Request registered";
                    string historySummary = "";
                    if (history != null && history.Count > 1)
                    {
                        historySummary = string.Format(" (Submission #{0} of {1})", history.Count, history.Count);
                    }
                    Show("✓ ITMS received this laptop",
                        "ITMS reference: " + refText + historySummary + ".\nYou can move the USB to the next laptop, or click [ Resend as New ] to submit again for development/testing.",
                        "Pass", history);
                    _btnPrimary.Visibility = Visibility.Collapsed;
                    _btnResend.Visibility = Visibility.Visible;
                    _btnResend.Content = "Resend as New";
                    _btnResend.Style = St("PrimaryButton");
                    _btnResend.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    return;

                case TransmissionState.Sent:
                    Show("Sent — waiting for ITMS to confirm",
                         (message ?? "The laptop is queued in ITMS.") + "\n\n• Click [ Check Status ] to see if ITMS finished reviewing.\n• Click [ Resend as New ] to start a new submission.",
                         "Warn", history);
                    _btnPrimary.Visibility = Visibility.Visible;
                    _btnPrimary.Content = "Check Status";
                    _btnPrimary.Style = St("PrimaryButton");
                    _btnPrimary.IsEnabled = _connector.IsEnabled;
                    _btnResend.Visibility = Visibility.Visible;
                    _btnResend.Content = "Resend as New";
                    _btnResend.Style = St("BaseButton");
                    _btnResend.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    return;

                case TransmissionState.Failed:
                    string failMsg = !string.IsNullOrWhiteSpace(message) ? message : (!string.IsNullOrWhiteSpace(r?.LastError) ? r.LastError : "The laptop could not be sent to ITMS.");
                    int subNum = history?.Count ?? 1;
                    string failAdvice = string.Format(
                        "\n\n• Click [ Try Again ] to retry this submission (Submission #{0}, Request ID {1}). ITMS will not add duplicate entries.\n• Click [ Resend as New ] to mint a new request ID and submit as a new entry.",
                        subNum,
                        !string.IsNullOrEmpty(r?.RequestId) ? r.RequestId.Substring(0, Math.Min(8, r.RequestId.Length)) + "…" : "active");
                    Show("Sending failed", failMsg + failAdvice, "Fail", history);
                    _btnPrimary.Visibility = Visibility.Visible;
                    _btnPrimary.Content = "Try Again";
                    _btnPrimary.Style = St("PrimaryButton");
                    _btnPrimary.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    _btnResend.Visibility = Visibility.Visible;
                    _btnResend.Content = "Resend as New";
                    _btnResend.Style = St("BaseButton");
                    _btnResend.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    return;

                case TransmissionState.Sending:
                    Show("Previous sending was interrupted",
                        "The tool stopped or the network dropped before ITMS answered.\n\n• Click [ Try Again ] to resume the existing submission.\n• Click [ Resend as New ] to mint a fresh request ID.",
                        "Warn", history);
                    _btnPrimary.Visibility = Visibility.Visible;
                    _btnPrimary.Content = "Try Again";
                    _btnPrimary.Style = St("PrimaryButton");
                    _btnPrimary.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    _btnResend.Visibility = Visibility.Visible;
                    _btnResend.Content = "Resend as New";
                    _btnResend.Style = St("BaseButton");
                    _btnResend.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    return;

                default:
                    Show("Ready to send", "Click Send to ITMS to submit this laptop to the review queue.", "Neutral", history);
                    _btnPrimary.Visibility = Visibility.Visible;
                    _btnPrimary.Content = "Send to ITMS";
                    _btnPrimary.Style = St("PrimaryButton");
                    _btnPrimary.IsEnabled = _connector.IsEnabled && _payload.IsValid;
                    _btnResend.Visibility = Visibility.Collapsed;
                    return;
            }
        }

        private void Show(string status, string message, string tone, List<SyncRecord> history = null)
        {
            _status.Text = status;
            _message.Text = message ?? "";
            _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            _statusBox.Background = Res(tone + "Soft");
            _status.Foreground = tone == "Neutral" ? Res("TextPrimary") : Res(tone + "Brush");

            // Render submission history if multiple submissions exist
            if (history != null && history.Count > 1)
            {
                _historyList.Children.Clear();
                _historyList.Children.Add(new TextBlock
                {
                    Text = "Submission History:",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Foreground = Res("TextPrimary"),
                    Margin = new Thickness(0, 8, 0, 4)
                });

                // Display recent submissions chronologically (newest first)
                int num = history.Count;
                foreach (var h in history.Take(5))
                {
                    string refDisplay;
                    if (h.SyncStatus == TransmissionState.ItmsReceived && !string.IsNullOrEmpty(h.RemoteId))
                    {
                        refDisplay = h.RemoteId;
                    }
                    else if (h.SyncStatus == TransmissionState.Failed)
                    {
                        string errTag = "FAILED";
                        if (!string.IsNullOrEmpty(h.LastError))
                        {
                            var httpMatch = Regex.Match(h.LastError, @"HTTP\s+\d+");
                            var codeMatch = Regex.Match(h.LastError, @"\b[A-Z_]{5,}\b");
                            if (httpMatch.Success && codeMatch.Success)
                                errTag = string.Format("FAILED ({0} {1})", httpMatch.Value, codeMatch.Value);
                            else if (httpMatch.Success)
                                errTag = string.Format("FAILED ({0})", httpMatch.Value);
                            else if (codeMatch.Success)
                                errTag = string.Format("FAILED ({0})", codeMatch.Value);
                        }
                        refDisplay = errTag;
                    }
                    else
                    {
                        refDisplay = h.SyncStatus;
                    }

                    string timeStr = h.UpdatedAt.HasValue ? " · " + DisplayFormat.DateTime(h.UpdatedAt.Value) : "";
                    bool isLatest = (num == history.Count);

                    var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 2) };
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = string.Format("• Submission #{0} → {1}{2}{3}", num, refDisplay, timeStr, isLatest ? " (Current)" : ""),
                        FontSize = 12,
                        Foreground = isLatest ? Res("TextPrimary") : Res("TextMuted"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    _historyList.Children.Add(itemPanel);
                    num--;
                }
                _historyList.Visibility = Visibility.Visible;
            }
            else
            {
                _historyList.Visibility = Visibility.Collapsed;
            }
        }

        private static void AddRow(StackPanel parent, string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, Style = St("Muted"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var v = new TextBlock
            {
                Text = value ?? "Not detected", FontSize = 14, TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold, Foreground = value == null ? Res("NeutralBrush") : Res("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            parent.Children.Add(g);
        }

        private static Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        private static Style St(string k) => (Style)Application.Current.FindResource(k);
    }
}
