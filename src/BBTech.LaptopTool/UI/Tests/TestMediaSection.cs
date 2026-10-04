using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BBTech.Core.Testing;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test.Audio;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>
    /// "BBTech Test Media" section of the Speaker / Headphone tests.
    ///
    /// Plays the fixed BBTech left/right test clip INSIDE the test window with WPF's MediaElement
    /// (Windows Media Foundation; decoding runs on its own thread, so the UI stays responsive).
    /// It never opens an external player and offers no file browsing. Audio goes to the default
    /// Windows output device. Playback stops when the test window closes, or when another test
    /// sound starts (AudioArbiter). Finishing the clip does NOT mark the test as passed.
    /// </summary>
    public sealed class TestMediaSection : UserControl
    {
        private const string Owner = "media";

        private readonly MediaElement _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = 0.85,
            ScrubbingEnabled = false,
        };
        private readonly Border _videoBox;
        private readonly TextBlock _status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private readonly Button _start, _play, _pause, _stop;
        private readonly Slider _volume = new Slider { Minimum = 0, Maximum = 1, Value = 0.85, Width = 120, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        private readonly ProgressBar _progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 4), Foreground = (Brush)Application.Current.FindResource("Brand") };
        private readonly TextBlock _time = new TextBlock { FontSize = 12 };
        private readonly StackPanel _controls;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _loaded, _playing, _unavailable;
        private TimeSpan _furthest;

        /// <summary>For the test notes, e.g. "BBTech test media played (0:58 of 1:00)".</summary>
        public string Notes =>
            _furthest > TimeSpan.FromSeconds(1)
                ? "BBTech test media played (" + Fmt(_furthest) + (_media.NaturalDuration.HasTimeSpan ? " of " + Fmt(_media.NaturalDuration.TimeSpan) : "") + ")"
                : "";

        public bool IsPlaying => _playing;

        public TestMediaSection()
        {
            _start = Btn("▶  Play Test Media", (s, e) => StartAsync(), "PrimaryButton");
            _start.SetValue(AutomationProperties.NameProperty, "Play Test Media");
            _play = Btn("▶ Play", (s, e) => Resume(), "BaseButton");
            _pause = Btn("❚❚ Pause", (s, e) => Pause(), "BaseButton");
            _stop = Btn("■ Stop", (s, e) => Stop(), "BaseButton");
            _play.SetValue(AutomationProperties.NameProperty, "Play media");
            _pause.SetValue(AutomationProperties.NameProperty, "Pause media");
            _stop.SetValue(AutomationProperties.NameProperty, "Stop media");
            _volume.ValueChanged += (s, e) => _media.Volume = _volume.Value;

            _videoBox = new Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(8),
                Height = 200,
                MaxWidth = 356, // 16:9
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = true,
                Child = _media,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 8) };
            volumeRow.Children.Add(new TextBlock { Text = "Volume", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            volumeRow.Children.Add(_volume);

            var buttons = new WrapPanel();
            buttons.Children.Add(_play);
            buttons.Children.Add(_pause);
            buttons.Children.Add(_stop);
            buttons.Children.Add(volumeRow);

            _controls = new StackPanel { Visibility = Visibility.Collapsed };
            _controls.Children.Add(buttons);
            _controls.Children.Add(_progress);
            _controls.Children.Add(_time);

            var root = new StackPanel();
            root.Children.Add(_status);
            root.Children.Add(_start);
            root.Children.Add(_videoBox);
            root.Children.Add(_controls);
            Content = root;

            _media.MediaOpened += (s, e) =>
            {
                _loaded = true;
                AppLog.Info("Test media opened (" + (_media.NaturalDuration.HasTimeSpan ? Fmt(_media.NaturalDuration.TimeSpan) : "?") + ")");
                UpdateProgress();
            };
            _media.MediaFailed += (s, e) =>
            {
                AppLog.Warn("Test media playback failed: " + (e.ErrorException?.Message ?? "unknown error"));
                ShowUnavailable();
            };
            _media.MediaEnded += (s, e) =>
            {
                _media.Stop();
                SetPlaying(false);
                _status.Text = "Test media finished. Confirm what you heard, then choose PASS or FAIL.";
                AppLog.Info("Test media finished");
                UpdateProgress();
            };
            _timer.Tick += (s, e) => UpdateProgress();

            if (!ToolBundle.HasMedia(ToolBundle.SpeakerVideo)) ShowUnavailable();
            else _status.Text = "Left / right speaker test clip (about 1 minute). Plays inside this window.";
        }

        private static Button Btn(string text, RoutedEventHandler click, string style)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(0, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += click;
            return b;
        }

        private async void StartAsync()
        {
            if (_unavailable) return;
            if (_loaded) { Resume(); return; }

            _start.IsEnabled = false;
            _status.Text = "Loading test media...";
            // Extracting the embedded clip (~19 MB) happens off the UI thread.
            string path = await Task.Run(() => ToolBundle.GetMediaFile(ToolBundle.SpeakerVideo));
            var state = await Task.Run(() => TestMediaCheck.Check(path));
            if (state != TestMediaState.Available)
            {
                AppLog.Warn("Test media check failed: " + state);
                ShowUnavailable();
                return;
            }

            try
            {
                _media.Source = new Uri(path);
                _start.Visibility = Visibility.Collapsed;
                _videoBox.Visibility = Visibility.Visible;
                _controls.Visibility = Visibility.Visible;
                Resume();
            }
            catch (Exception ex)
            {
                AppLog.Error("Test media could not start", ex);
                ShowUnavailable();
            }
        }

        private void Resume()
        {
            if (_unavailable || _media.Source == null) return;
            AudioArbiter.Acquire(Owner, Pause); // stops test tones / other audio first
            _media.Play();
            SetPlaying(true);
            _status.Text = "Playing BBTech test media...";
            AppLog.Info("Test media playing");
        }

        /// <summary>Pause (also used when another test sound takes over the audio).</summary>
        public void Pause()
        {
            if (!_playing) return;
            _media.Pause();
            SetPlaying(false);
            _status.Text = "Paused at " + Fmt(_media.Position) + ".";
            AppLog.Info("Test media paused at " + Fmt(_media.Position));
        }

        public void Stop()
        {
            if (_media.Source == null) return;
            bool was = _playing;
            Track();
            _media.Stop();
            SetPlaying(false);
            AudioArbiter.Release(Owner);
            _status.Text = "Stopped.";
            UpdateProgress();
            if (was) AppLog.Info("Test media stopped by user");
        }

        /// <summary>Called when the test window closes: stop audio and release the file.</summary>
        public void Shutdown()
        {
            bool was = _playing;
            Track();
            _timer.Stop();
            try
            {
                _media.Stop();
                _media.Close();
                _media.Source = null;
            }
            catch (Exception ex) { AppLog.Warn("Test media shutdown error", ex); }
            _playing = false;
            AudioArbiter.Release(Owner);
            if (was || _furthest > TimeSpan.Zero) AppLog.Info("Test media stopped (test window closed" + (was ? " while playing" : "") + ", furthest " + Fmt(_furthest) + ")");
        }

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            _play.IsEnabled = !playing;
            _pause.IsEnabled = playing;
            if (playing) _timer.Start(); else _timer.Stop();
        }

        private void Track()
        {
            try { if (_media.Position > _furthest) _furthest = _media.Position; } catch { }
        }

        private void UpdateProgress()
        {
            Track();
            var dur = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
            _progress.Value = dur.TotalSeconds > 0 ? Math.Min(1, _media.Position.TotalSeconds / dur.TotalSeconds) : 0;
            _time.Text = Fmt(_media.Position) + " / " + (dur > TimeSpan.Zero ? Fmt(dur) : "--:--");
        }

        private void ShowUnavailable()
        {
            _unavailable = true;
            _timer.Stop();
            try { _media.Close(); _media.Source = null; } catch { }
            _playing = false;
            AudioArbiter.Release(Owner);
            _status.Text = TestMediaCheck.UnavailableMessage + " The left / right tones above still work.";
            _status.Foreground = (Brush)Application.Current.FindResource("TextMuted");
            _start.Visibility = Visibility.Visible;
            _start.IsEnabled = false;
            _videoBox.Visibility = Visibility.Collapsed;
            _controls.Visibility = Visibility.Collapsed;
        }

        private static string Fmt(TimeSpan t) => ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
    }
}
