using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Qr;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// Displays a single, high-contrast QR code containing the complete offline inspection record.
    /// Supports standard UTF-8 JSON format (universal scanner compatibility) and compressed BBT1 format.
    /// Can be scanned by another computer running Central ITMS Tracker via USB barcode scanner or camera.
    /// </summary>
    public sealed class QrCodeWindow : Window
    {
        private readonly DetectionResult _detection;
        private readonly string _compactUtf8Payload;
        private readonly Image _qrImage;
        private readonly TextBlock _statusText;
        private readonly TextBlock _formatHintText;

        public QrCodeWindow(DetectionResult detection)
        {
            _detection = detection ?? throw new ArgumentNullException(nameof(detection));
            AppLog.Info($"[QR-Diag] Stage 1: QrCodeWindow initialized for device '{_detection.Brand} {_detection.Model}' (S/N: {_detection.System?.SerialNumber ?? "Unknown"})");

            Title = "Offline Inspection QR Code";
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Background = (Brush)Application.Current.FindResource("AppBackground");
            Foreground = (Brush)Application.Current.FindResource("TextPrimary");

            var mainPanel = new StackPanel { Margin = new Thickness(24) };

            // 1. Header
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Offline Inspection QR Code",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Single-scan transfer to Central ITMS Tracker via 2D USB barcode scanner or camera.",
                FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // 2. Device Specs Summary Card
            var summaryBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("CardBackground"),
                BorderBrush = (Brush)Application.Current.FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var summaryPanel = new StackPanel();

            string brand = !string.IsNullOrEmpty(_detection.Brand) ? _detection.Brand : "Unknown Brand";
            string model = !string.IsNullOrEmpty(_detection.Model) ? _detection.Model : "Unknown Model";
            string serial = !string.IsNullOrEmpty(_detection.System?.SerialNumber) ? _detection.System.SerialNumber : "Unknown S/N";

            summaryPanel.Children.Add(new TextBlock
            {
                Text = $"{brand} {model}",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            });

            string ssdHealth = "";
            var primeDisk = _detection.StorageDevices?.Count > 0 ? _detection.StorageDevices[0] : null;
            if (primeDisk != null && !string.IsNullOrEmpty(primeDisk.HealthAssessment))
            {
                ssdHealth = $"  ·  SSD: {primeDisk.HealthAssessment}";
                if (primeDisk.HealthPercent.HasValue) ssdHealth += $" ({primeDisk.HealthPercent}%)";
            }

            summaryPanel.Children.Add(new TextBlock
            {
                Text = $"S/N: {serial}  ·  CPU: {_detection.Cpu}  ·  RAM: {_detection.Ram}  ·  Storage: {_detection.Storage}{ssdHealth}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            summaryBorder.Child = summaryPanel;
            mainPanel.Children.Add(summaryBorder);

            // 3. Guaranteed Mandatory UTF-8 JSON Format Indicator
            var formatBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(24, 16, 185, 129)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 16, 185, 129)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var formatStack = new StackPanel { Orientation = Orientation.Horizontal };
            formatStack.Children.Add(new TextBlock
            {
                Text = "✓ UTF-8 JSON Format",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129))
            });
            formatStack.Children.Add(new TextBlock
            {
                Text = " (Directly scannable by physical 2D barcode scanners & ITMS)",
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                Margin = new Thickness(4, 1, 0, 0)
            });
            formatBorder.Child = formatStack;
            mainPanel.Children.Add(formatBorder);

            _formatHintText = new TextBlock
            {
                Text = "Payload starts with standard '{' JSON. Fully compatible with ITMS review queue.",
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            mainPanel.Children.Add(_formatHintText);

            // 4. QR Code Image Container
            var qrBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)Application.Current.FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(22),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _qrImage = new Image
            {
                Width = 440,
                Height = 440,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_qrImage, EdgeMode.Aliased);
            qrBorder.Child = _qrImage;
            mainPanel.Children.Add(qrBorder);

            // 5. Status & Telemetry
            _statusText = new TextBlock
            {
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            mainPanel.Children.Add(_statusText);

            // 6. Action Buttons (Copy, Save, Close)
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var copyBtn = new Button
            {
                Content = "Copy Payload",
                Style = (Style)Application.Current.FindResource("BaseButton"),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 100
            };
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_compactUtf8Payload))
                    {
                        Clipboard.SetText(_compactUtf8Payload);
                        MessageBox.Show("Offline inspection QR payload (UTF-8 JSON) copied to clipboard.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Failed to copy QR payload to clipboard", ex);
                    MessageBox.Show("Could not copy to clipboard: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            var saveBtn = new Button
            {
                Content = "Save Image…",
                Style = (Style)Application.Current.FindResource("BaseButton"),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 100
            };
            saveBtn.Click += SaveImage_Click;

            var closeBtn = new Button
            {
                Content = "Close",
                Style = (Style)Application.Current.FindResource("PrimaryButton"),
                MinWidth = 80
            };
            closeBtn.Click += (s, e) => Close();

            btnPanel.Children.Add(copyBtn);
            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(closeBtn);
            mainPanel.Children.Add(btnPanel);

            Content = mainPanel;

            // 7. Prepare payload and render mandatory UTF-8 JSON QR code
            try
            {
                string requestId = !string.IsNullOrEmpty(_detection.SubmissionId)
                    ? _detection.SubmissionId
                    : Guid.NewGuid().ToString("D");

                // Guaranteed Standard UTF-8 JSON (<800 bytes)
                _compactUtf8Payload = ItmsPayload.BuildCompactInspectionPayload(
                    _detection,
                    requestId,
                    testedBy: null,
                    overallResult: "DETECTED"
                );

                AppLog.Info($"[QR-Diag] Stage 2: Payload prepared. UTF-8 JSON={_compactUtf8Payload.Length} bytes.");

                RenderCurrentQrCode();
            }
            catch (Exception ex)
            {
                AppLog.Error("[QR-Diag] Failed to generate offline QR code", ex);
                _statusText.Text = "Failed to generate QR code: " + ex.Message;
                _statusText.Foreground = (Brush)Application.Current.FindResource("FailBrush");
            }

            Loaded += (s, e) =>
            {
                AppLog.Info($"[QR-Diag] Stage 7: QrCodeWindow displayed successfully. WindowSize={ActualWidth:0}x{ActualHeight:0}, ImageSize={_qrImage.ActualWidth:0}x{_qrImage.ActualHeight:0}, ImageVisible={_qrImage.IsVisible}, ImageHasSource={(_qrImage.Source != null)}.");
            };
        }

        private QrCodeEncoder RenderCurrentQrCode()
        {
            if (string.IsNullOrEmpty(_compactUtf8Payload))
            {
                AppLog.Warn("[QR-Diag] RenderCurrentQrCode aborted: payload is empty");
                return null;
            }

            QrCodeEncoder qr;
            try
            {
                // Mandatory UTF-8 JSON with screen-optimized EccLevel.L (7%)
                // Minimizes module density and maximizes module pixel size for instant 1-frame reading
                qr = QrCodeEncoder.EncodeText(_compactUtf8Payload, EccLevel.L);
            }
            catch (Exception ex)
            {
                AppLog.Error("[QR-Diag] Failed to encode UTF-8 QR code", ex);
                _statusText.Text = "Failed to generate QR code: " + ex.Message;
                _statusText.Foreground = (Brush)Application.Current.FindResource("FailBrush");
                return null;
            }

            int blackCount = 0;
            for (int r = 0; r < qr.Size; r++)
                for (int c = 0; c < qr.Size; c++)
                    if (qr.Modules[r, c]) blackCount++;

            bool matrixSuccess = qr.Modules != null && qr.Size > 0 && blackCount > 0;
            AppLog.Info($"[QR-Diag] Stage 3 & 4: Format=UTF-8 JSON, PayloadBytes={qr.DataBytes}, EncodingMode={qr.EncodingMode}, SelectedVersion={qr.Version}, SelectedEcc={qr.ErrorCorrection}, MaxCapacity={qr.MaxCapacityBytes} bytes, MatrixDimensions={qr.Size}x{qr.Size}, MatrixGenerationSuccess={matrixSuccess} (ActiveModules={blackCount}/{qr.Size * qr.Size}).");

            // Crisp integer module size for instant scanning
            int modulePixelSize = qr.Version > 25 ? 4 : (qr.Version > 14 ? 5 : 6);
            byte[] bmpBytes = qr.ToBmp(modulePixelSize: modulePixelSize, quietZoneModules: 5);

            using (var ms = new MemoryStream(bmpBytes))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                _qrImage.Source = bitmap;
                bool bitmapSuccess = bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;
                AppLog.Info($"[QR-Diag] Stage 5 & 6: BitmapGenerationSuccess={bitmapSuccess}, Dimensions={bitmap.PixelWidth}x{bitmap.PixelHeight}, Dpi={bitmap.DpiX:0}x{bitmap.DpiY:0}, ImageControlAssigned=True.");
            }

            _statusText.Text = $"UTF-8 JSON · Version {qr.Version} ({qr.Size}×{qr.Size}) · {qr.DataBytes} bytes (ECC {qr.ErrorCorrection}) · Ready to scan";
            _statusText.Foreground = (Brush)Application.Current.FindResource("TextMuted");

            return qr;
        }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_compactUtf8Payload))
                {
                    MessageBox.Show("No QR code payload available to save.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Title = "Save Inspection QR Code (UTF-8 JSON)",
                    Filter = "Bitmap Image (*.bmp)|*.bmp",
                    FileName = $"Inspection-QR-{_detection.System?.SerialNumber ?? "Laptop"}-UTF8.bmp"
                };

                if (dialog.ShowDialog() == true)
                {
                    var qr = QrCodeEncoder.EncodeText(_compactUtf8Payload, EccLevel.L);
                    byte[] bmpBytes = qr.ToBmp(modulePixelSize: 6, quietZoneModules: 5);
                    File.WriteAllBytes(dialog.FileName, bmpBytes);
                    MessageBox.Show("QR code image saved successfully (UTF-8 JSON).", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to save QR code image", ex);
                MessageBox.Show("Failed to save image: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
