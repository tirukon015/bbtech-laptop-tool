using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BBTech.LaptopTool.Logging;
using BBTech.LaptopTool.Services;
using BBTech.LaptopTool.Test.Audio;

namespace BBTech.LaptopTool.UI.Tests
{
    /// <summary>
    /// Speaker and headphone tests. Generated left/right tones and a sweep, plus the fixed
    /// "BBTech Test Media" clip played inside the window (<see cref="TestMediaSection"/>).
    /// For headphones, the output device list is watched to detect plug-in.
    /// </summary>
    public sealed class AudioOutputTestPanel : TestPanelBase
    {
        private readonly bool _headphones;
        private readonly TextBlock _device = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _jack = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly TestMediaSection _media = new TestMediaSection();
        private string _initialDefaultId;
        private bool _plugDetected;
        private string _plugDevice = "";

        public override string Instructions => _headphones
            ? "Plug headphones into the audio jack (or connect USB/Bluetooth headphones). Play the left and right tones and confirm each ear."
            : "Unplug any headphones. Play the left and right tones and the sweep, and confirm both speakers sound clear without rattling or distortion.";

        public AudioOutputTestPanel(bool headphones)
        {
            _headphones = headphones;
            var controls = Row(
                B("◀  Play LEFT", (s, e) => Play(Channel.Left), "PrimaryButton"),
                B("Play RIGHT  ▶", (s, e) => Play(Channel.Right), "PrimaryButton"),
                B("Play BOTH", (s, e) => Play(Channel.Both)),
                B("Frequency sweep", (s, e) => Run(AudioPlayback.PlaySweep)),
                B("Stop", (s, e) => AudioPlayback.Stop()));

            var stack = new StackPanel();
            stack.Children.Add(Card("Output device", _device, _headphones ? (UIElement)_jack : new Border()));
            stack.Children.Add(Card("Sound tests", T("Left uses a lower tone, right a higher tone.", 13, false, "TextMuted"), new Border { Height = 8 }, controls));
            stack.Children.Add(Card("BBTech Test Media", _media));
            stack.Children.Add(_headphones
                ? Card("Confirm what you heard", Check("Sound in LEFT ear only for left tone"), Check("Sound in RIGHT ear only for right tone"), Check("Clear sound, no crackling"), Check("Speakers muted while headphones are connected"))
                : Card("Confirm what you heard", Check("LEFT speaker works"), Check("RIGHT speaker works"), Check("No distortion or rattling during sweep"), Check("Volume is adequate at maximum")));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var def = AudioEndpoints.List(false).FirstOrDefault(d => d.IsDefault);
            _initialDefaultId = def?.Id ?? "";
            Refresh();
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
        }

        private void Refresh()
        {
            var list = AudioEndpoints.List(false);
            var def = list.FirstOrDefault(d => d.IsDefault);
            if (def == null)
            {
                _device.Text = "No audio output device found. Check that audio is enabled in Device Manager / BIOS.";
                _device.Foreground = Res("FailBrush");
                Host?.SetSuggestion("No audio output device detected.", SuggestionKind.Bad);
                return;
            }
            _device.Foreground = Res("TextPrimary");
            _device.Text = def.Name + "  (" + def.FormFactorName + ")";

            if (!_headphones) return;
            var phones = list.FirstOrDefault(d => d.IsHeadphoneLike);
            if (!_plugDetected && (phones != null || (def.Id != _initialDefaultId && _initialDefaultId.Length > 0)))
            {
                _plugDetected = true;
                _plugDevice = phones?.Name ?? def.Name;
                AppLog.Info("Headphone output detected (" + (phones != null ? phones.FormFactorName : "default device changed") + ")");
            }
            _jack.Text = _plugDetected
                ? "✓ Headphone output detected: " + _plugDevice
                : "Waiting for headphones... (some laptops share one output for speakers and jack; then just listen in the headphones)";
            _jack.Foreground = _plugDetected ? Res("PassBrush") : Res("TextMuted");
            Host?.SetSuggestion(_plugDetected ? "Headphones detected. Play the tones and confirm each ear." : "Plug in headphones.", _plugDetected ? SuggestionKind.Good : SuggestionKind.Info);
        }

        private void Play(Channel ch) => Run(() => AudioPlayback.PlayTone(ch));

        private void Run(Action a)
        {
            try { a(); }
            catch (InvalidOperationException ex) { Host?.SetSuggestion(ex.Message, SuggestionKind.Bad); }
        }

        /// <summary>Window closed (PASS/FAIL/SKIP/close): nothing keeps playing in the background.</summary>
        public override void Detach()
        {
            _timer.Stop();
            _media.Shutdown();
            AudioPlayback.Stop();
        }

        // The media finishing never decides the result; it is only recorded as evidence.
        public override string Notes => Join(_headphones ? (_plugDetected ? "Headphone output detected" : "Headphone output not auto-detected") : "",
                                             _media.Notes, ChecklistNotes());
    }

    /// <summary>Microphone: live input level, plus a 5-second recording played back to the operator.</summary>
    public sealed class MicrophoneTestPanel : TestPanelBase
    {
        private readonly MicrophoneRecorder _rec = new MicrophoneRecorder();
        private readonly ProgressBar _meter = new ProgressBar { Height = 26, Maximum = 100, Foreground = Res("PassBrush") };
        private readonly TextBlock _device = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly Button _recordBtn, _playBtn;
        private byte[] _lastRecording;
        private double _peak;
        private bool _soundDetected;

        public override string Instructions => "Speak or clap near the laptop. The level bar must move. Then record 5 seconds and play it back.";

        public MicrophoneTestPanel()
        {
            _recordBtn = B("● Record 5 seconds", (s, e) => Record(), "PrimaryButton");
            _playBtn = B("▶ Play recording", (s, e) => { if (_lastRecording != null) AudioPlayback.PlayWav(_lastRecording); });
            _playBtn.IsEnabled = false;
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        Card("Recording device", _device),
                        Card("Input level", _meter, new Border { Height = 8 }, _status),
                        Card("Record and play back", Row(_recordBtn, _playBtn)),
                        Card("Confirm", Check("Level bar reacts to voice"), Check("Recording is clear and audible")),
                    }
                }
            };
        }

        public override void Attach(TestHostWindow host)
        {
            base.Attach(host);
            var def = AudioEndpoints.List(true).FirstOrDefault(d => d.IsDefault);
            _device.Text = def != null ? def.Name : "No recording device reported by Windows.";

            _rec.Level += level => Ui(() => OnLevel(level));
            _rec.RecordingFinished += wav => Ui(() =>
            {
                _lastRecording = wav;
                _recordBtn.IsEnabled = true;
                _recordBtn.Content = "● Record 5 seconds";
                _playBtn.IsEnabled = true;
                AudioPlayback.PlayWav(wav);
            });

            string err = _rec.Start();
            if (err != null)
            {
                _status.Text = err;
                _status.Foreground = Res("FailBrush");
                _recordBtn.IsEnabled = false;
                host.SetSuggestion(err, SuggestionKind.Bad);
            }
            else
            {
                _status.Text = "Listening... speak now.";
                host.SetSuggestion("Speak to check the level.", SuggestionKind.Info);
            }
        }

        private void OnLevel(double level)
        {
            double pct = Math.Min(100, level * 140);
            _meter.Value = pct;
            _peak = Math.Max(_peak, level);
            if (!_soundDetected && level > 0.08)
            {
                _soundDetected = true;
                _status.Text = "✓ Sound detected by the microphone.";
                _status.Foreground = Res("PassBrush");
                Host?.SetSuggestion("Microphone picks up sound. Record and play back to confirm quality.", SuggestionKind.Good);
            }
            else if (!_soundDetected && _peak == 0)
            {
                _status.Text = "No signal at all. If it stays flat, check Settings > Privacy > Microphone (allow desktop apps) and that the mic is not muted.";
            }
        }

        private void Record()
        {
            _recordBtn.IsEnabled = false;
            _recordBtn.Content = "Recording...";
            _rec.RecordSeconds(5);
        }

        public override void Detach()
        {
            _rec.Dispose();
            AudioPlayback.Stop();
        }

        public override string Notes => Join(_soundDetected ? "Sound detected (peak " + (int)(_peak * 100) + "%)" : "No sound detected (peak " + (int)(_peak * 100) + "%)",
                                             _lastRecording != null ? "Recording played back" : "", ChecklistNotes());
    }
}

