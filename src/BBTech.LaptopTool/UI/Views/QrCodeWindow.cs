using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Qr;

namespace BBTech.LaptopTool.UI.Views
{
    /// <summary>
    /// Displays a high-density, crisp QR code containing the full offline inspection record.
    /// Can be scanned by another computer running Central ITMS Tracker via USB barcode scanner or webcam.
    /// </summary>
    public sealed class QrCodeWindow : Window
    {
        private readonly DetectionResult _detection;
        private readonly List<string> _chunks;
        private int _currentChunkIndex = 0;

        private readonly Image _qrImage;
        private readonly TextBlock _partLabel;
        private readonly Button _prevBtn;
        private readonly Button _nextBtn;
        private readonly TextBlock _statusText;

        public QrCodeWindow(DetectionResult detection)
        {
            _detection = detection ?? throw new ArgumentNullException(nameof(detection));

            Title = "Offline Inspection QR Code";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Background = (Brush)Application.Current.FindResource("AppBackground");
            Foreground = (Brush)Application.Current.FindResource("TextPrimary");

            // 1. Build canonical ITMS payload with persistent SubmissionId
            string requestId = !string.IsNullOrEmpty(_detection.SubmissionId)
                ? _detection.SubmissionId
                : Guid.NewGuid().ToString("D");

            string jsonPayload = ItmsPayload.BuildInspectionPayload(
                _detection,
                requestId,
                testedBy: null,
                overallResult: "DETECTED"
            );

            // 2. Compress and chunk
            string compressed = QrPayloadCompressor.Compress(jsonPayload);
            _chunks = QrPayloadCompressor.Chunk(compressed, maxChunkSize: 1100, requestId: requestId);

            var mainPanel = new StackPanel { Margin = new Thickness(24) };

            // Header
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Offline Inspection QR Code",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Scan this QR code from Central ITMS using a USB 2D barcode scanner or browser webcam.",
                FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // Specs Summary Box
            var summaryBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("SurfaceBackground"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16)
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
                Margin = new Thickness(0, 0, 0, 2)
            });
            summaryPanel.Children.Add(new TextBlock
            {
                Text = $"S/N: {serial}  ·  CPU: {_detection.Cpu}  ·  RAM: {_detection.Ram}  ·  Storage: {_detection.Storage}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            summaryBorder.Child = summaryPanel;
            mainPanel.Children.Add(summaryBorder);

            // QR Code Image Container
            var qrBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            _qrImage = new Image
            {
                Width = 260,
                Height = 260,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
            qrBorder.Child = _qrImage;
            mainPanel.Children.Add(qrBorder);

            // Multi-part Navigation (visible only if multiple parts)
            var navPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = _chunks.Count > 1 ? Visibility.Visible : Visibility.Collapsed
            };

            _prevBtn = new Button
            {
                Content = "◀ Previous Part",
                Style = (Style)Application.Current.FindResource("SecondaryButton"),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };
            _prevBtn.Click += (s, e) => NavigateChunk(-1);

            _partLabel = new TextBlock
            {
                Text = $"Part 1 of {_chunks.Count}",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };

            _nextBtn = new Button
            {
                Content = "Next Part ▶",
                Style = (Style)Application.Current.FindResource("SecondaryButton"),
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };
            _nextBtn.Click += (s, e) => NavigateChunk(1);

            navPanel.Children.Add(_prevBtn);
            navPanel.Children.Add(_partLabel);
            navPanel.Children.Add(_nextBtn);
            mainPanel.Children.Add(navPanel);

            // Status & Size Telemetry
            var (rawBytes, compBytes, ratio) = QrPayloadCompressor.Measure(jsonPayload);
            _statusText = new TextBlock
            {
                Text = $"Payload: {compBytes} bytes compressed ({rawBytes} bytes uncompressed, {(ratio * 100):0}% of raw size)",
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            mainPanel.Children.Add(_statusText);

            // Action Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var copyBtn = new Button
            {
                Content = "Copy Payload",
                Style = (Style)Application.Current.FindResource("SecondaryButton"),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 100
            };
            copyBtn.Click += (s, e) =>
            {
                Clipboard.SetText(_chunks[_currentChunkIndex]);
                MessageBox.Show("QR payload copied to clipboard.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            var saveBtn = new Button
            {
                Content = "Save Image…",
                Style = (Style)Application.Current.FindResource("SecondaryButton"),
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

            // Render initial QR
            RenderCurrentChunk();
        }

        private void NavigateChunk(int delta)
        {
            int next = _currentChunkIndex + delta;
            if (next >= 0 && next < _chunks.Count)
            {
                _currentChunkIndex = next;
                RenderCurrentChunk();
            }
        }

        private void RenderCurrentChunk()
        {
            string payload = _chunks[_currentChunkIndex];
            var qr = QrCodeEncoder.EncodeText(payload, EccLevel.L);
            byte[] bmpBytes = qr.ToBmp(modulePixelSize: 5, quietZoneModules: 4);

            using (var ms = new MemoryStream(bmpBytes))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                _qrImage.Source = bitmap;
            }

            if (_chunks.Count > 1)
            {
                _partLabel.Text = $"Part {_currentChunkIndex + 1} of {_chunks.Count}";
                _prevBtn.IsEnabled = _currentChunkIndex > 0;
                _nextBtn.IsEnabled = _currentChunkIndex < _chunks.Count - 1;
            }
        }

        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Inspection QR Code",
                Filter = "Bitmap Image (*.bmp)|*.bmp",
                FileName = $"Inspection-QR-{_detection.System?.SerialNumber ?? "Laptop"}.bmp"
            };

            if (dialog.ShowDialog() == true)
            {
                string payload = _chunks[_currentChunkIndex];
                var qr = QrCodeEncoder.EncodeText(payload, EccLevel.L);
                byte[] bmpBytes = qr.ToBmp(modulePixelSize: 8, quietZoneModules: 4);
                File.WriteAllBytes(dialog.FileName, bmpBytes);
                MessageBox.Show("QR code image saved successfully.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
