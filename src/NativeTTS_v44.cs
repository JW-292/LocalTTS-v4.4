using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace LocalTtsVoice
{
    internal sealed class DeviceItem
    {
        public int Number;
        public string Name;
        public DeviceItem(int number, string name) { Number = number; Name = name; }
        public override string ToString() { return Name; }
    }

    internal sealed class GlassTableLayoutPanel : TableLayoutPanel
    {
        public Color OverlayColor = Color.FromArgb(222, 22, 29, 45);

        public GlassTableLayoutPanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            using (SolidBrush overlay = new SolidBrush(OverlayColor))
                e.Graphics.FillRectangle(overlay, ClientRectangle);
        }
    }

    // VB-CABLE is fed by one long-lived 48 kHz stream. Previously the microphone
    // relay and every TTS sentence opened separate legacy WaveOut clients on the
    // same device. Some drivers mixed those clients unreliably, which caused
    // missing syllables and short drop-outs in Discord.
    internal sealed class DiscordAudioBus : IDisposable
    {
        public const int SampleRate = 48000;
        private readonly object sync = new object();
        private readonly MixingSampleProvider mixer;
        private readonly MMDeviceEnumerator deviceEnumerator;
        private readonly MMDevice endpoint;
        private readonly IWavePlayer output;
        private readonly Dictionary<ISampleProvider, AudioFileReader> fileReaders =
            new Dictionary<ISampleProvider, AudioFileReader>();
        private readonly Dictionary<ISampleProvider, Action> fileCallbacks =
            new Dictionary<ISampleProvider, Action>();
        private ISampleProvider microphoneInput;
        private bool disposing;
        public readonly int DeviceNumber;

        public bool IsRunning
        {
            get { return !disposing && output.PlaybackState == PlaybackState.Playing; }
        }

        public DiscordAudioBus(int deviceNumber)
        {
            DeviceNumber = deviceNumber;
            mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
            mixer.ReadFully = true;
            mixer.MixerInputEnded += MixerInputEnded;
            deviceEnumerator = new MMDeviceEnumerator();
            endpoint = FindRenderEndpoint(deviceEnumerator, deviceNumber);
            // The installed VB-CABLE endpoint is natively 48 kHz, stereo,
            // IEEE-float. WASAPI shared mode receives that exact format and
            // avoids the former extra PCM16 quantization plus legacy waveOut
            // conversion that remote listeners described as muffled.
            output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 120);
            output.Init(new SampleToWaveProvider(mixer));
            output.PlaybackStopped += OutputStopped;
            output.Play();
        }

        private static MMDevice FindRenderEndpoint(MMDeviceEnumerator enumerator,
                                                    int waveOutDeviceNumber)
        {
            if (waveOutDeviceNumber < 0)
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string legacyName = WaveOut.GetCapabilities(waveOutDeviceNumber).ProductName;
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            foreach (MMDevice device in devices)
                if (device.FriendlyName.StartsWith(legacyName,
                        StringComparison.OrdinalIgnoreCase) ||
                    legacyName.StartsWith(device.FriendlyName,
                        StringComparison.OrdinalIgnoreCase))
                    return device;
            throw new InvalidOperationException(
                "선택한 Discord 출력의 WASAPI 장치를 찾지 못했습니다: " + legacyName);
        }

        private void OutputStopped(object sender, StoppedEventArgs args)
        {
            // If Windows or VB-CABLE drops the long-lived endpoint, complete
            // pending sessions instead of leaving them stuck forever. The next
            // utterance recreates the bus in EnsureDiscordBus.
            if (!disposing) StopFiles();
        }

        private static ISampleProvider ToBusFormat(ISampleProvider source)
        {
            ISampleProvider converted = source;
            if (converted.WaveFormat.Channels == 1)
                converted = new MonoToStereoSampleProvider(converted);
            else if (converted.WaveFormat.Channels != 2)
                throw new InvalidOperationException("Discord 출력은 모노 또는 스테레오 음성만 지원합니다.");
            if (converted.WaveFormat.SampleRate != SampleRate)
                converted = new WdlResamplingSampleProvider(converted, SampleRate);
            return converted;
        }

        public ISampleProvider AddMicrophone(BufferedWaveProvider microphone)
        {
            ISampleProvider provider = ToBusFormat(microphone.ToSampleProvider());
            lock (sync)
            {
                if (microphoneInput != null) mixer.RemoveMixerInput(microphoneInput);
                microphoneInput = provider;
                mixer.AddMixerInput(provider);
            }
            return provider;
        }

        public void RemoveMicrophone()
        {
            lock (sync)
            {
                if (microphoneInput == null) return;
                mixer.RemoveMixerInput(microphoneInput);
                microphoneInput = null;
            }
        }

        public void PlayFile(string file, float volume, Action completed)
        {
            AudioFileReader reader = new AudioFileReader(file);
            // Never overdrive the Discord transport. Values over unity clip
            // consonant transients and are perceived remotely as muffled.
            reader.Volume = Math.Max(0.0f, Math.Min(1.0f, volume));
            ISampleProvider provider = ToBusFormat(reader);
            lock (sync)
            {
                fileReaders[provider] = reader;
                fileCallbacks[provider] = completed;
                try { mixer.AddMixerInput(provider); }
                catch
                {
                    fileReaders.Remove(provider);
                    fileCallbacks.Remove(provider);
                    reader.Dispose();
                    throw;
                }
            }
        }

        private void MixerInputEnded(object sender, SampleProviderEventArgs args)
        {
            AudioFileReader reader = null;
            Action callback = null;
            lock (sync)
            {
                if (fileReaders.TryGetValue(args.SampleProvider, out reader))
                {
                    fileReaders.Remove(args.SampleProvider);
                    fileCallbacks.TryGetValue(args.SampleProvider, out callback);
                    fileCallbacks.Remove(args.SampleProvider);
                }
            }
            if (reader != null) reader.Dispose();
            if (callback != null) callback();
        }

        public void StopFiles()
        {
            List<KeyValuePair<ISampleProvider, AudioFileReader>> readers;
            List<Action> callbacks;
            lock (sync)
            {
                readers = new List<KeyValuePair<ISampleProvider, AudioFileReader>>(fileReaders);
                callbacks = new List<Action>(fileCallbacks.Values);
                foreach (KeyValuePair<ISampleProvider, AudioFileReader> item in readers)
                    mixer.RemoveMixerInput(item.Key);
                fileReaders.Clear();
                fileCallbacks.Clear();
            }
            foreach (KeyValuePair<ISampleProvider, AudioFileReader> item in readers)
                item.Value.Dispose();
            foreach (Action callback in callbacks)
                if (callback != null) callback();
        }

        public void Dispose()
        {
            if (disposing) return;
            disposing = true;
            StopFiles();
            RemoveMicrophone();
            mixer.MixerInputEnded -= MixerInputEnded;
            output.PlaybackStopped -= OutputStopped;
            try { output.Stop(); } catch { }
            output.Dispose();
            if (endpoint != null) endpoint.Dispose();
            if (deviceEnumerator != null) deviceEnumerator.Dispose();
        }
    }

    internal sealed class PlaybackSession : IDisposable
    {
        private readonly List<WaveOutEvent> outputs = new List<WaveOutEvent>();
        private readonly List<AudioFileReader> readers = new List<AudioFileReader>();
        private int remaining;
        private readonly Action<PlaybackSession> finished;
        private bool disposed;

        public PlaybackSession(string file, int? monitorDevice, float volume,
                               Action<PlaybackSession> finishedCallback)
        {
            finished = finishedCallback;
            // One completion belongs to the Discord bus. The optional monitor
            // has its own reader so local listening cannot starve Discord.
            remaining = 1 + (monitorDevice.HasValue ? 1 : 0);
            if (monitorDevice.HasValue)
            {
                AudioFileReader reader = new AudioFileReader(file);
                reader.Volume = volume;
                WaveOutEvent output = new WaveOutEvent();
                output.DeviceNumber = monitorDevice.Value;
                output.DesiredLatency = 160;
                output.NumberOfBuffers = 4;
                output.Init(reader);
                output.PlaybackStopped += OnStopped;
                readers.Add(reader);
                outputs.Add(output);
            }
        }

        public PlaybackSession(string file, IEnumerable<int> devices, float volume,
                               Action<PlaybackSession> finishedCallback)
        {
            finished = finishedCallback;
            remaining = 0;
            foreach (int device in devices)
            {
                AudioFileReader reader = new AudioFileReader(file);
                reader.Volume = volume;
                WaveOutEvent output = new WaveOutEvent {
                    DeviceNumber = device,
                    DesiredLatency = 100,
                    NumberOfBuffers = 3
                };
                output.Init(reader);
                output.PlaybackStopped += OnStopped;
                readers.Add(reader);
                outputs.Add(output);
                remaining++;
            }
        }

        public void Play()
        {
            foreach (WaveOutEvent output in outputs) output.Play();
        }

        private void OnStopped(object sender, StoppedEventArgs args)
        {
            PartFinished();
        }

        public void DiscordFinished()
        {
            PartFinished();
        }

        private void PartFinished()
        {
            if (disposed) return;
            if (Interlocked.Decrement(ref remaining) <= 0 && finished != null)
                finished(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (WaveOutEvent output in outputs)
            {
                output.PlaybackStopped -= OnStopped;
                try { output.Stop(); } catch { }
                output.Dispose();
            }
            foreach (AudioFileReader reader in readers) reader.Dispose();
            outputs.Clear();
            readers.Clear();
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ComboBox micBox = NewCombo();
        private readonly ComboBox discordBox = NewCombo();
        private readonly ComboBox monitorBox = NewCombo();
        private readonly ComboBox voiceBox = NewCombo();
        private readonly TextBox chatBox = new TextBox();
        private readonly TextBox pipChatBox = new TextBox();
        private readonly Panel pipPanel = new Panel();
        private readonly PictureBox pipAvatar = new PictureBox();
        private readonly Button relayButton = NewButton("마이크 송출 시작", true);
        private readonly Button speakButton = NewButton("채팅 읽기", true);
        private readonly Button stopTtsButton = NewButton("TTS 중지", false);
        private readonly Button refreshButton = NewButton("장치 새로고침", false);
        private readonly Button pipButton = NewButton("PIP 모드", false);
        private readonly Button pipHeaderButton = NewButton("PIP 모드", false);
        private readonly TrackBar rateBar = NewSlider(-5, 5, 0);
        private readonly TrackBar volumeBar = NewSlider(0, 200, 100);
        private readonly CheckBox clarityCheck = new CheckBox();
        private readonly Label statusLabel = new Label();
        private readonly Label stateDot = new Label();
        private readonly System.Windows.Forms.Timer audioHealthTimer =
            new System.Windows.Forms.Timer();
        private readonly List<PlaybackSession> playbacks = new List<PlaybackSession>();
        private const string MashiroVoice = "독립 합성음성 v4.4 (4인 조화 · Wave Balanced)";
        private const string MashiroRoot = @"A:\LocalTTS-AI\GPT-SoVITS-v2-240821";
        private static readonly string MashiroVoiceRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "models", "v44");
        private const string MashiroV2BaseGpt =
            @"A:\LocalTTS-AI\voices\mashiro_candidate_v5b\mashiro_candidate_v5b-e1.ckpt";
        private static readonly string MashiroConfig = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config", "v44_tts_infer.yaml");
        private const string MashiroApiBase = "http://127.0.0.1:9884";
        private const int MashiroApiPort = 9884;
        private Process mashiroProcess;
        private readonly object mashiroServerSync = new object();
        private bool mashiroServerVerified;
        private WaveInEvent waveIn;
#if LEGACY_V5
        private WaveOutEvent legacyMicOutput;
#endif
        private BufferedWaveProvider micBuffer;
        private DiscordAudioBus discordBus;
        private bool relaying;
        private bool pipMode;
        private bool ttsBusy;
        private TableLayoutPanel rootLayout;
        private TableLayoutPanel ttsPanel;
        private Control headingPanel;
        private Control audioCard;
        private Control footerLabel;
        private Size normalClientSize;
        private Size normalMinimumSize;
        private Image mashiroBackgroundImage;
        private Image mashiroAvatarImage;

        private static readonly Color Background = Color.FromArgb(18, 23, 36);
        private static readonly Color Panel = Color.FromArgb(29, 36, 54);
        private static readonly Color Field = Color.FromArgb(13, 18, 29);
        private static readonly Color TextMain = Color.FromArgb(238, 242, 255);
        private static readonly Color Muted = Color.FromArgb(166, 177, 202);
        private static readonly Color Accent = Color.FromArgb(116, 151, 255);

        public MainForm()
        {
            Text = "로컬 TTS 보이스";
            ClientSize = new Size(720, 760);
            MinimumSize = new Size(650, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = TextMain;
            Font = new Font("Malgun Gothic", 9F);
            Icon = SystemIcons.Application;
            mashiroBackgroundImage = LoadEmbeddedImage(
                "LocalTtsVoice.Assets.MashiroBackground");
            mashiroAvatarImage = LoadEmbeddedImage(
                "LocalTtsVoice.Assets.MashiroAvatar");
            BackgroundImage = mashiroBackgroundImage;
            BackgroundImageLayout = ImageLayout.Stretch;

            TableLayoutPanel root = new TableLayoutPanel();
            rootLayout = root;
            root.Dock = DockStyle.Fill;
            root.BackColor = Color.Transparent;
            root.Padding = new Padding(20);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 286));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Controls.Add(root);

            Panel heading = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            headingPanel = heading;
            Label title = new Label {
                Text = "로컬 TTS 보이스", Font = new Font("Malgun Gothic", 20F, FontStyle.Bold),
                AutoSize = true, Location = new Point(0, 0), ForeColor = TextMain,
                BackColor = Color.Transparent
            };
            Label subtitle = new Label {
                Text = "채팅을 음성으로 읽어 Discord와 내 헤드폰에 동시에 보냅니다.",
                AutoSize = true, Location = new Point(2, 40), ForeColor = Muted,
                BackColor = Color.Transparent
            };
            heading.Controls.Add(title); heading.Controls.Add(subtitle);
            pipHeaderButton.Location = new Point(558, 5);
            pipHeaderButton.Width = 104;
            heading.Controls.Add(pipHeaderButton);
            heading.SizeChanged += delegate {
                pipHeaderButton.Location = new Point(
                    Math.Max(0, heading.ClientSize.Width - pipHeaderButton.Width), 5);
            };
            root.Controls.Add(heading, 0, 0);

            TableLayoutPanel audioPanel = Card("오디오 연결", 7);
            ConfigureRows(audioPanel,
                new RowStyle(SizeType.Absolute, 26),
                new RowStyle(SizeType.Absolute, 53),
                new RowStyle(SizeType.Absolute, 53),
                new RowStyle(SizeType.Absolute, 53),
                new RowStyle(SizeType.Absolute, 42),
                new RowStyle(SizeType.Absolute, 35),
                new RowStyle(SizeType.Absolute, 0));
            audioCard = audioPanel;
            AddField(audioPanel, 1, "내 마이크", micBox);
            AddField(audioPanel, 2, "Discord로 보낼 출력 (예: CABLE Input)", discordBox);
            AddField(audioPanel, 3, "내가 들을 출력 (헤드폰 또는 스피커)", monitorBox);
            FlowLayoutPanel audioActions = NewActions();
            audioActions.Controls.Add(relayButton);
            audioActions.Controls.Add(refreshButton);
            audioPanel.Controls.Add(audioActions, 0, 4);
            audioPanel.SetColumnSpan(audioActions, 2);
            Panel statePanel = new Panel { Dock = DockStyle.Fill };
            stateDot.Text = "●"; stateDot.ForeColor = Color.FromArgb(215, 91, 102);
            stateDot.AutoSize = true; stateDot.Location = new Point(2, 7);
            statusLabel.Text = "장치를 선택하고 마이크 송출을 시작하세요.";
            statusLabel.ForeColor = Muted; statusLabel.AutoSize = true; statusLabel.Location = new Point(22, 7);
            statePanel.Controls.Add(stateDot); statePanel.Controls.Add(statusLabel);
            audioPanel.Controls.Add(statePanel, 0, 5); audioPanel.SetColumnSpan(statePanel, 2);
            root.Controls.Add(audioPanel, 0, 1);

            ttsPanel = Card("채팅 TTS", 7);
            ConfigureRows(ttsPanel,
                new RowStyle(SizeType.Absolute, 26),
                new RowStyle(SizeType.Absolute, 53),
                new RowStyle(SizeType.Absolute, 22),
                new RowStyle(SizeType.Percent, 100),
                new RowStyle(SizeType.Absolute, 58),
                new RowStyle(SizeType.Absolute, 42),
                new RowStyle(SizeType.Absolute, 26));
            AddField(ttsPanel, 1, "목소리", voiceBox);
            Label chatLabel = MakeLabel("TTS로 읽을 채팅 입력창");
            ttsPanel.Controls.Add(chatLabel, 0, 2); ttsPanel.SetColumnSpan(chatLabel, 2);
            chatBox.Multiline = true; chatBox.AcceptsReturn = true;
            chatBox.ScrollBars = ScrollBars.Vertical; chatBox.Dock = DockStyle.Fill;
            chatBox.BackColor = Field; chatBox.ForeColor = TextMain; chatBox.BorderStyle = BorderStyle.FixedSingle;
            chatBox.Font = new Font("Malgun Gothic", 11F); chatBox.Margin = new Padding(0, 3, 0, 8);
            ttsPanel.Controls.Add(chatBox, 0, 3); ttsPanel.SetColumnSpan(chatBox, 2);

            FlowLayoutPanel sliders = NewActions();
            sliders.Controls.Add(SliderGroup("말하기 속도", rateBar, 210));
            sliders.Controls.Add(SliderGroup("TTS 음량", volumeBar, 210));
            clarityCheck.Text = "발음 명료도 보정";
            clarityCheck.Checked = true;
            clarityCheck.AutoSize = true;
            clarityCheck.ForeColor = TextMain;
            clarityCheck.Padding = new Padding(0, 20, 0, 0);
            sliders.Controls.Add(clarityCheck);
            ttsPanel.Controls.Add(sliders, 0, 4); ttsPanel.SetColumnSpan(sliders, 2);
            FlowLayoutPanel ttsActions = NewActions();
            ttsActions.Controls.Add(speakButton); ttsActions.Controls.Add(stopTtsButton);
            Button erase = NewButton("입력 지우기", false);
            erase.Click += delegate { chatBox.Clear(); FocusChatInput(); };
            ttsActions.Controls.Add(erase);
            ttsActions.Controls.Add(pipButton);
            ttsPanel.Controls.Add(ttsActions, 0, 5); ttsPanel.SetColumnSpan(ttsActions, 2);
            Label help = MakeLabel("Enter: 읽기   ·   Shift+Enter: 줄바꿈");
            help.ForeColor = Muted; ttsPanel.Controls.Add(help, 0, 6); ttsPanel.SetColumnSpan(help, 2);
            root.Controls.Add(ttsPanel, 0, 2);

            Label footer = MakeLabel("TTS는 Discord 출력과 모니터 출력에서 동시에 재생됩니다. 헤드폰 사용을 권장합니다.");
            footerLabel = footer;
            footer.ForeColor = Muted; footer.Dock = DockStyle.Fill; footer.Padding = new Padding(2, 8, 0, 0);
            root.Controls.Add(footer, 0, 3);

            pipPanel.Dock = DockStyle.Fill;
            pipPanel.Visible = false;
            pipPanel.BackColor = Color.FromArgb(18, 23, 36);
            pipPanel.Padding = new Padding(8);
            pipAvatar.Image = mashiroAvatarImage;
            pipAvatar.SizeMode = PictureBoxSizeMode.Zoom;
            pipAvatar.Location = new Point(8, 8);
            pipAvatar.Size = new Size(56, 56);
            pipAvatar.BackColor = Color.FromArgb(29, 36, 54);
            pipChatBox.Multiline = false;
            pipChatBox.AcceptsReturn = false;
            pipChatBox.BackColor = Field;
            pipChatBox.ForeColor = TextMain;
            pipChatBox.BorderStyle = BorderStyle.FixedSingle;
            pipChatBox.Font = new Font("Malgun Gothic", 12F);
            pipChatBox.Location = new Point(74, 19);
            pipChatBox.Height = 32;
            pipChatBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            pipPanel.Controls.Add(pipAvatar);
            pipPanel.Controls.Add(pipChatBox);
            pipPanel.SizeChanged += delegate {
                pipChatBox.Width = Math.Max(120, pipPanel.ClientSize.Width - 84);
                pipChatBox.Top = Math.Max(8, (pipPanel.ClientSize.Height - pipChatBox.Height) / 2);
                pipAvatar.Top = Math.Max(4, (pipPanel.ClientSize.Height - pipAvatar.Height) / 2);
            };
            Controls.Add(pipPanel);

            relayButton.Click += ToggleRelay;
            speakButton.Click += Speak;
            stopTtsButton.Click += delegate { StopAllTts(); SetStatus("TTS를 중지했습니다.", relaying); };
            refreshButton.Click += delegate {
                StopRelay(); StopAllTts(); DisposeDiscordBus(); LoadDevices();
            };
            pipButton.Click += TogglePipMode;
            pipHeaderButton.Click += TogglePipMode;
            chatBox.KeyDown += ChatKeyDown;
            pipChatBox.KeyDown += PipChatKeyDown;
            FormClosing += delegate(object closingSender, FormClosingEventArgs closingArgs) {
                LogStability("창 닫기 요청: PIP=" + pipMode +
                    ", reason=" + closingArgs.CloseReason);
                if (pipMode &&
                    closingArgs.CloseReason != CloseReason.WindowsShutDown &&
                    closingArgs.CloseReason != CloseReason.ApplicationExitCall)
                {
                    closingArgs.Cancel = true;
                    SetPipMode(false);
                    return;
                }
                audioHealthTimer.Stop();
                StopRelay(); StopAllTts(); DisposeDiscordBus(); StopMashiroServer();
            };
            FormClosed += delegate {
                if (mashiroAvatarImage != null) mashiroAvatarImage.Dispose();
                if (mashiroBackgroundImage != null) mashiroBackgroundImage.Dispose();
            };
            Shown += delegate { LoadDevices(); LoadVoices(); FocusChatInput(); };
            audioHealthTimer.Interval = 1500;
            audioHealthTimer.Tick += delegate
            {
                if (!relaying || discordBus == null || discordBus.IsRunning) return;
                try
                {
                    EnsureDiscordBus(SelectedNumber(discordBox));
                    SetStatus("CABLE 연결을 자동으로 복구했습니다 · 마이크 송출 중", true);
                }
                catch (Exception ex)
                {
                    SetStatus("CABLE 자동 복구 대기 · " + ex.Message, false);
                }
            };
            audioHealthTimer.Start();
            normalClientSize = ClientSize;
            normalMinimumSize = MinimumSize;
        }

        private static ComboBox NewCombo()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
                BackColor = Field, ForeColor = TextMain, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 0, 6) };
        }

        private static Image LoadEmbeddedImage(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException(
                        "앱에 포함된 Mashiro UI 이미지를 찾을 수 없습니다.", resourceName);
                using (Image source = Image.FromStream(stream))
                    return new Bitmap(source);
            }
        }

        private static Button NewButton(string text, bool primary)
        {
            return new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.FromArgb(49, 61, 88),
                ForeColor = primary ? Color.FromArgb(8, 15, 31) : TextMain,
                FlatAppearance = { BorderSize = 0 }, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand };
        }

        private static TrackBar NewSlider(int min, int max, int value)
        {
            return new TrackBar { Minimum = min, Maximum = max, Value = value, TickStyle = TickStyle.None,
                Width = 200, Height = 28, BackColor = Panel };
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 0) };
        }

        private static TableLayoutPanel Card(string title, int rows)
        {
            GlassTableLayoutPanel panel = new GlassTableLayoutPanel();
            panel.Dock = DockStyle.Fill; panel.Padding = new Padding(16, 12, 16, 12);
            panel.ColumnCount = 2; panel.RowCount = rows;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Label label = new Label { Text = title, Font = new Font("Malgun Gothic", 10F, FontStyle.Bold),
                ForeColor = TextMain, AutoSize = true };
            panel.Controls.Add(label, 0, 0); panel.SetColumnSpan(label, 2);
            return panel;
        }

        private static void ConfigureRows(TableLayoutPanel panel,
                                          params RowStyle[] styles)
        {
            panel.RowStyles.Clear();
            foreach (RowStyle style in styles) panel.RowStyles.Add(style);
        }

        private static void AddField(TableLayoutPanel panel, int row, string label, Control control)
        {
            Panel wrapper = new Panel { Dock = DockStyle.Fill };
            Label fieldLabel = MakeLabel(label); fieldLabel.Location = new Point(0, 0);
            control.Location = new Point(0, 19); control.Width = wrapper.Width; control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            wrapper.Controls.Add(fieldLabel); wrapper.Controls.Add(control);
            panel.Controls.Add(wrapper, 0, row); panel.SetColumnSpan(wrapper, 2);
        }

        private static FlowLayoutPanel NewActions()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, Margin = new Padding(0, 5, 0, 0) };
        }

        private static Panel SliderGroup(string text, TrackBar slider, int width)
        {
            Panel panel = new Panel { Width = width, Height = 52 };
            Label label = MakeLabel(text); label.Location = new Point(0, 0);
            slider.Location = new Point(-5, 20);
            panel.Controls.Add(label); panel.Controls.Add(slider);
            return panel;
        }

        private void LoadDevices()
        {
            int oldMic = SelectedNumber(micBox);
            int oldDiscord = SelectedNumber(discordBox);
            int oldMonitor = SelectedNumber(monitorBox);
            micBox.Items.Clear(); discordBox.Items.Clear(); monitorBox.Items.Clear();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                micBox.Items.Add(new DeviceItem(i, WaveIn.GetCapabilities(i).ProductName));
            discordBox.Items.Add(new DeviceItem(-1, "Windows 기본 출력"));
            monitorBox.Items.Add(new DeviceItem(-1, "Windows 기본 출력"));
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                string name = WaveOut.GetCapabilities(i).ProductName;
                discordBox.Items.Add(new DeviceItem(i, name));
                monitorBox.Items.Add(new DeviceItem(i, name));
            }
            SelectNumber(micBox, oldMic, null);
            SelectNumber(discordBox, oldDiscord, "CABLE Input");
            SelectNumber(monitorBox, oldMonitor, "Windows 기본 출력");
            SetStatus("오디오 장치 목록을 불러왔습니다.", relaying);
        }

        private void LoadVoices()
        {
            try
            {
                voiceBox.Items.Clear();
                if (File.Exists(MashiroV2BaseGpt) &&
                    File.Exists(Path.Combine(MashiroVoiceRoot, "v44_fourway_sovits.pth")) &&
                    File.Exists(Path.Combine(MashiroVoiceRoot, "ref_v44_fourway.wav")))
                    voiceBox.Items.Add(MashiroVoice);
                if (voiceBox.Items.Count > 0) voiceBox.SelectedIndex = 0;
                else throw new FileNotFoundException(
                    "v4.4 음성 자산 또는 v5b GPT를 찾을 수 없습니다.");
            }
            catch (Exception ex) { ShowError("음성 목록을 불러오지 못했습니다.", ex); }
        }

        private static int SelectedNumber(ComboBox box)
        {
            DeviceItem item = box.SelectedItem as DeviceItem;
            return item == null ? int.MinValue : item.Number;
        }

        private static void SelectNumber(ComboBox box, int old, string preferred)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((DeviceItem)box.Items[i]).Number == old) { box.SelectedIndex = i; return; }
            if (preferred != null)
                for (int i = 0; i < box.Items.Count; i++)
                    if (((DeviceItem)box.Items[i]).Name.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0)
                    { box.SelectedIndex = i; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void ToggleRelay(object sender, EventArgs e)
        {
            if (relaying) { StopRelay(); return; }
            try
            {
                if (micBox.SelectedItem == null || discordBox.SelectedItem == null)
                    throw new InvalidOperationException("마이크와 Discord 출력 장치를 선택하세요.");
                int discord = SelectedNumber(discordBox);
#if !LEGACY_V5
                EnsureDiscordBus(discord);
#endif
                // Keep the broadly supported capture format used by the former
                // relay; the bus performs one explicit high-quality conversion
                // to Discord's 48 kHz format.
                WaveFormat format = new WaveFormat(44100, 16, 1);
                micBuffer = new BufferedWaveProvider(format);
                // A short bounded buffer absorbs scheduler jitter without
                // accumulating seconds of latency when a game is busy.
                micBuffer.BufferDuration = TimeSpan.FromMilliseconds(500);
                micBuffer.DiscardOnBufferOverflow = true;
                micBuffer.ReadFully = true;
                waveIn = new WaveInEvent { DeviceNumber = SelectedNumber(micBox), WaveFormat = format,
                    BufferMilliseconds = 20, NumberOfBuffers = 4 };
                waveIn.DataAvailable += delegate(object s, WaveInEventArgs args) {
                    micBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                };
                waveIn.RecordingStopped += delegate(object s, StoppedEventArgs args) {
                    if (args.Exception != null) BeginInvoke((Action)delegate { ShowError("마이크 오류", args.Exception); });
                };
#if LEGACY_V5
                legacyMicOutput = new WaveOutEvent {
                    DeviceNumber = discord, DesiredLatency = 120, NumberOfBuffers = 3
                };
                legacyMicOutput.Init(micBuffer);
                legacyMicOutput.Play();
#else
                discordBus.AddMicrophone(micBuffer);
#endif
                waveIn.StartRecording();
                relaying = true;
                relayButton.Text = "마이크 송출 중지";
                SetStatus("마이크 송출 중 · TTS를 입력할 수 있습니다.", true);
                FocusChatInput();
            }
            catch (Exception ex) { StopRelay(); ShowError("마이크 송출을 시작하지 못했습니다.", ex); }
        }

        private void StopRelay()
        {
            relaying = false;
            if (waveIn != null) { try { waveIn.StopRecording(); } catch { } waveIn.Dispose(); waveIn = null; }
#if LEGACY_V5
            if (legacyMicOutput != null)
            {
                try { legacyMicOutput.Stop(); } catch { }
                legacyMicOutput.Dispose(); legacyMicOutput = null;
            }
#else
            if (discordBus != null) discordBus.RemoveMicrophone();
#endif
            micBuffer = null;
#if !LEGACY_V5
            if (playbacks.Count == 0) DisposeDiscordBus();
#endif
            relayButton.Text = "마이크 송출 시작";
            SetStatus("마이크 송출이 중지되었습니다.", false);
        }

        private void ChatKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                Speak(sender, EventArgs.Empty);
            }
        }

        private void FocusChatInput()
        {
            if (pipMode) pipChatBox.Focus();
            else chatBox.Focus();
        }

        private void PipChatKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SpeakText(pipChatBox.Text);
        }

        private void TogglePipMode(object sender, EventArgs e)
        {
            SetPipMode(!pipMode);
        }

        private void SetPipMode(bool enabled)
        {
            if (pipMode == enabled) return;
            pipMode = enabled;
            SuspendLayout();
            if (pipMode)
            {
                pipChatBox.Text = chatBox.Text;
                chatBox.Clear();
                rootLayout.Visible = false;
                pipPanel.Visible = true;
                pipPanel.BringToFront();
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                TopMost = true;
                MaximizeBox = false;
                MinimizeBox = false;
                MinimumSize = new Size(380, 104);
                ClientSize = new Size(520, 72);
                Text = "독립 합성음성 v4.4 · Enter로 읽기";
                pipChatBox.Focus();
            }
            else
            {
                chatBox.Text = pipChatBox.Text;
                pipChatBox.Clear();
                pipPanel.Visible = false;
                rootLayout.Visible = true;
                FormBorderStyle = FormBorderStyle.Sizable;
                TopMost = false;
                MaximizeBox = true;
                MinimizeBox = true;
                MinimumSize = normalMinimumSize;
                ClientSize = normalClientSize;
                Text = "로컬 TTS 보이스";
                FocusChatInput();
            }
            ResumeLayout(true);
        }

        private void Speak(object sender, EventArgs e)
        {
            SpeakText(chatBox.Text);
        }

        private void SpeakText(string rawText)
        {
            string text = rawText.Trim();
            if (text.Length == 0) { SetStatus("읽을 채팅을 입력하세요.", relaying); return; }
            if (ttsBusy)
            {
                SetStatus("이전 TTS 재생이 끝난 뒤 다시 입력하세요 · 음성 겹침 방지", relaying);
                return;
            }
            if (discordBox.SelectedItem == null || monitorBox.SelectedItem == null)
            { SetStatus("Discord 출력과 모니터 출력을 선택하세요.", relaying); return; }
            int discord = SelectedNumber(discordBox);
            int monitor = SelectedNumber(monitorBox);
            float volume = volumeBar.Value / 100f;
            int rate = rateBar.Value;
            bool improveClarity = clarityCheck.Checked;
            string selectedVoice = voiceBox.SelectedItem == null ? "" : voiceBox.SelectedItem.ToString();
            // Once the request is accepted, clear both entry surfaces
            // immediately so the next chat can be typed while audio is made.
            chatBox.Clear();
            pipChatBox.Clear();
            ttsBusy = true;
            speakButton.Enabled = false;
            if (selectedVoice == MashiroVoice)
            {
                SetStatus("v4.4 AI 엔진을 준비하는 중… 첫 실행은 약 30초 걸립니다.", relaying);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        string file = SynthesizeMashiro(text, rate, improveClarity);
                        BeginInvoke((Action)delegate
                        {
                            try
                            {
                                StartPlayback(file, discord, monitor, volume);
                                FocusChatInput();
                            }
                            catch (Exception playbackError)
                            {
                                ttsBusy = false;
                                speakButton.Enabled = true;
                                try { File.Delete(file); } catch { }
                                ShowError("v4.4 TTS를 재생하지 못했습니다.", playbackError);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke((Action)delegate
                        {
                            ttsBusy = false;
                            speakButton.Enabled = true;
                            ShowError("v4.4 TTS를 만들지 못했습니다.", ex);
                        });
                    }
                });
                return;
            }
            SetStatus("TTS 음성을 만드는 중…", relaying);
            bool playbackStarted = false;
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "LocalTTS_" + Guid.NewGuid().ToString("N") + ".wav");
                using (SpeechSynthesizer synth = new SpeechSynthesizer())
                {
                    if (voiceBox.SelectedItem != null) synth.SelectVoice(voiceBox.SelectedItem.ToString());
                    synth.Rate = rateBar.Value;
                    synth.SetOutputToWaveFile(tempFile);
                    synth.Speak(text);
                }
                StartPlayback(tempFile, discord, monitor, volume);
                playbackStarted = true;
            }
            catch (Exception ex) { ShowError("TTS를 재생하지 못했습니다.", ex); }
            finally
            {
                if (!playbackStarted)
                {
                    ttsBusy = false;
                    speakButton.Enabled = true;
                }
                FocusChatInput();
            }
        }

        private void StartPlayback(string tempFile, int discord, int monitor, float volume)
        {
#if LEGACY_V5
            List<int> devices = new List<int>();
            devices.Add(discord);
            if (monitor != discord) devices.Add(monitor);
            PlaybackSession session = null;
            session = new PlaybackSession(tempFile, devices, volume,
                delegate(PlaybackSession done) {
                    BeginInvoke((Action)delegate {
                        done.Dispose(); playbacks.Remove(done);
                        try { File.Delete(tempFile); } catch { }
                        ttsBusy = false;
                        speakButton.Enabled = true;
                        SetStatus(relaying ? "마이크 송출 중" : "TTS 재생 완료", relaying);
                    });
                });
            playbacks.Add(session);
            session.Play();
#else
            EnsureDiscordBus(discord);
            PlaybackSession session = null;
            session = new PlaybackSession(tempFile,
                monitor != discord ? (int?)monitor : null, volume,
                delegate(PlaybackSession done) {
                    BeginInvoke((Action)delegate {
                        done.Dispose(); playbacks.Remove(done);
                        try { File.Delete(tempFile); } catch { }
                        // Do not keep an idle waveOut stream attached forever.
                        // Reopening per TTS burst restores the reliable behavior
                        // of the former version and recovers from cable resets.
                        // Keep the healthy WASAPI endpoint alive. Reopening it
                        // after every utterance caused the first-utterance-only
                        // regression on VB-CABLE. EnsureDiscordBus still replaces
                        // it automatically if PlaybackState is no longer Playing.
                        ttsBusy = false;
                        speakButton.Enabled = true;
                        SetStatus(relaying ? "마이크 송출 중" : "TTS 재생 완료", relaying);
                    });
                });
            playbacks.Add(session);
            try
            {
                discordBus.PlayFile(tempFile, volume, session.DiscordFinished);
                session.Play();
            }
            catch
            {
                playbacks.Remove(session);
                session.Dispose();
                try { File.Delete(tempFile); } catch { }
                throw;
            }
#endif
            SetStatus("TTS 재생 중 · 나와 Discord에 동시에 출력됩니다.", relaying);
        }

        private string SynthesizeMashiro(string text, int rate, bool improveClarity)
        {
            EnsureMashiroServer();
            string spokenText = improveClarity ? PrepareMashiroText(text) : text;
            // v4.4 was selected and validated at 0.94. Rate zero preserves
            // that production setting while the existing slider still works.
            double speed = Math.Max(0.72, Math.Min(1.28, 0.94 + rate * 0.065));
            return SynthesizeMashiroFragment(spokenText, speed);
        }

        private string SynthesizeMashiroFragment(string spokenText, double speed)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["text"] = spokenText;
            payload["text_lang"] = "all_ko";
            payload["ref_audio_path"] = Path.Combine(MashiroVoiceRoot,
                "ref_v44_fourway.wav").Replace('\\', '/');
            payload["aux_ref_audio_paths"] = new string[0];
            // The supplied Mashiro GPT checkpoint collapses to a two-frame response.
            // Use the stable v2 semantic model without a text prompt; Mashiro's
            // SoVITS checkpoint and reference audio still provide the voice timbre.
            payload["prompt_text"] = "";
            payload["prompt_lang"] = "all_ko";
            payload["text_split_method"] = "cut0";
            payload["top_k"] = 3;
            payload["top_p"] = 0.68;
            payload["temperature"] = 0.52;
            payload["repetition_penalty"] = 1.28;
            payload["media_type"] = "wav";
            payload["streaming_mode"] = false;
            payload["parallel_infer"] = false;
            payload["split_bucket"] = false;
            Exception lastError = null;
            double attemptSpeed = speed;
            bool engineRecovered = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                payload["speed_factor"] = attemptSpeed;
                // Preserve the current text-specific take on the first try.
                // Only malformed takes use the offline-vetted rescue seeds.
                payload["seed"] = MashiroSeedForAttempt(spokenText, attempt);
                byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
                string output = Path.Combine(Path.GetTempPath(),
                    "MashiroTTS_" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(MashiroApiBase + "/tts");
                    request.Method = "POST";
                    request.ContentType = "application/json; charset=utf-8";
                    request.Timeout = 180000;
                    request.ReadWriteTimeout = 180000;
                    request.ContentLength = body.Length;
                    using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (FileStream file = File.Create(output))
                        input.CopyTo(file);
                    RepairIsolatedMashiroClicks(output);
                    ApplyV44WaveBalance(output);
                    double[] quality = AnalyzeWaveQuality(output);
                    double rms = quality[0];
                    double nearSilence = quality[1];
                    double clickRate = quality[2];
                    double duration = WaveDuration(output);
                    if (rms >= 0.02 && nearSilence <= 0.82 && clickRate <= 4.0 &&
                        IsPlausibleV44Duration(spokenText, duration))
                        return output;
                    File.Delete(output);
                    Thread.Sleep(400);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { File.Delete(output); } catch { }
                    // Do not consume pronunciation rescue seeds when the
                    // inference process itself disappeared. Recover once and
                    // repeat the identical attempt and seed.
                    if (!engineRecovered && !MashiroServerReady())
                    {
                        engineRecovered = true;
                        LogStability("합성 중 엔진 응답 소실: " + ex.Message);
                        RestartMashiroServer();
                        attempt--;
                        continue;
                    }
                    Thread.Sleep(400);
                }
            }
            if (lastError != null) throw lastError;
            throw new InvalidOperationException(
                "v4.4 음성이 2회 연속 무음·반복·잡음이거나 입력 길이와 맞지 않게 생성됐습니다.");
        }

        private static int MashiroSeedForAttempt(string text, int attempt)
        {
            return attempt <= 1 ? 24680 : 101;
        }

        private static List<string> SplitMashiroPhrases(string text)
        {
            List<string> result = new List<string>();
            MatchCollection sentences = Regex.Matches(text, @"[^,.!?…]+[,.!?…]?");
            foreach (Match sentenceMatch in sentences)
            {
                string sentence = sentenceMatch.Value.Trim();
                if (sentence.Length == 0) continue;
                // Keep short and medium clauses together so the semantic model
                // can preserve Mashiro's sentence-level pitch and ending shape.
                // The old 14-16 syllable chunks sounded clipped and also caused
                // extra synthesis calls while gaming.
                if (Regex.Matches(sentence, @"[가-힣]").Count <= 32)
                {
                    result.Add(EnsureTerminalPunctuation(sentence));
                    continue;
                }

                string terminal = Regex.Match(sentence, @"[,.!?…]$").Value;
                string body = terminal.Length == 0
                    ? sentence
                    : sentence.Substring(0, sentence.Length - terminal.Length).Trim();
                List<string> words = new List<string>(body.Split(new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries));
                StringBuilder phrase = new StringBuilder();
                int phraseHangul = 0;
                foreach (string word in words)
                {
                    int wordHangul = Regex.Matches(word, @"[가-힣]").Count;
                    if (phrase.Length > 0 && phraseHangul >= 16 &&
                        phraseHangul + wordHangul > 30)
                    {
                        result.Add(EnsureTerminalPunctuation(phrase.ToString()));
                        phrase.Clear();
                        phraseHangul = 0;
                    }
                    if (phrase.Length > 0) phrase.Append(' ');
                    phrase.Append(word);
                    phraseHangul += wordHangul;
                }
                if (phrase.Length > 0)
                    result.Add(phrase.ToString() + (terminal.Length == 0 ? "." : terminal));
            }
            if (result.Count == 0 && text.Length > 0)
                result.Add(EnsureTerminalPunctuation(text));
            return result;
        }

        private static string EnsureTerminalPunctuation(string text)
        {
            return Regex.IsMatch(text, @"[,.!?…]$") ? text : text + ".";
        }

        private static void CombineWaveFiles(List<string> inputFiles, string outputFile)
        {
            if (inputFiles == null || inputFiles.Count == 0)
                throw new ArgumentException("결합할 Mashiro 음성 구간이 없습니다.");
            WaveFormat format;
            using (WaveFileReader first = new WaveFileReader(inputFiles[0]))
                format = first.WaveFormat;
            using (WaveFileWriter writer = new WaveFileWriter(outputFile, format))
            {
                for (int fileIndex = 0; fileIndex < inputFiles.Count; fileIndex++)
                {
                    string inputFile = inputFiles[fileIndex];
                    using (WaveFileReader reader = new WaveFileReader(inputFile))
                    {
                        if (!reader.WaveFormat.Equals(format))
                            throw new InvalidOperationException("Mashiro 음성 구간 형식이 서로 다릅니다.");
                        using (MemoryStream audio = new MemoryStream())
                        {
                            byte[] buffer = new byte[16384];
                            int read;
                            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                                audio.Write(buffer, 0, read);
                            byte[] samples = audio.ToArray();
                            FadePcm16Edges(samples, format, 12);
                            writer.Write(samples, 0, samples.Length);
                        }
                    }
                    if (fileIndex + 1 < inputFiles.Count)
                        writer.Write(new byte[format.AverageBytesPerSecond * 18 / 1000],
                            0, format.AverageBytesPerSecond * 18 / 1000);
                }
            }
        }

        private static void FadePcm16Edges(byte[] audio, WaveFormat format, int milliseconds)
        {
            if (format.Encoding != WaveFormatEncoding.Pcm ||
                format.BitsPerSample != 16 || audio.Length < format.BlockAlign * 4)
                return;

            int frames = audio.Length / format.BlockAlign;
            int fadeFrames = Math.Min(frames / 2, format.SampleRate * milliseconds / 1000);
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                double fadeIn = frame / (double)fadeFrames;
                double fadeOut = (fadeFrames - frame - 1) / (double)fadeFrames;
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int first = frame * format.BlockAlign + channel * 2;
                    int last = (frames - fadeFrames + frame) * format.BlockAlign + channel * 2;
                    short firstValue = BitConverter.ToInt16(audio, first);
                    short lastValue = BitConverter.ToInt16(audio, last);
                    WriteInt16(audio, first, (short)(firstValue * fadeIn));
                    WriteInt16(audio, last, (short)(lastValue * fadeOut));
                }
            }
        }

        private static void WriteInt16(byte[] target, int offset, short value)
        {
            target[offset] = (byte)(value & 0xff);
            target[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        private static string PrepareMashiroText(string input)
        {
            string text = input.Normalize(NormalizationForm.FormKC);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Repair only unambiguous particle spacing. Keep every other word
            // boundary exactly as entered and leave liaison to the Korean
            // frontend instead of rewriting Hangul spelling.
            text = Regex.Replace(
                text,
                @"(?<=[가-힣])\s+(에서|에게|으로|부터|까지|은|는|이|가|을|를|께|의|만|와|과|로)(?=\s|[,.!?]|$)",
                "$1");

            if (text.Length > 0 && !Regex.IsMatch(text, @"[,.!?]$")) text += ".";
            return text;
        }

        // Retained temporarily for source-level comparison with the former
        // broad liaison rewrite. It is no longer called by synthesis.
        private static string PrepareMashiroTextLegacy(string input)
        {
            string text = input.Normalize(NormalizationForm.FormKC);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Repair commonly separated Korean particles. This only changes the
            // pronunciation input; the user's text box remains untouched.
            text = Regex.Replace(
                text,
                @"(?<=[가-힣])\s+(은|는|이|가|을|를|에|에서|에게|께|의|도|만|부터|까지|와|과|으로|로)(?=\s|[,.!?]|$)",
                "$1");
            text = Regex.Replace(text, @"\b(이|그|저)\s+것", "$1것");

            // g2pk2 treats a space as a hard phonetic boundary. Standard Korean
            // liaison should continue when a batchim is followed by a vowel-onset
            // Hangul syllable (initial ㅇ), unless punctuation marks a pause.
            text = Regex.Replace(text, @"([가-힣])\s+([가-힣])", delegate(Match match)
            {
                char previous = match.Groups[1].Value[0];
                char next = match.Groups[2].Value[0];
                int previousIndex = previous - 0xAC00;
                int nextIndex = next - 0xAC00;
                bool previousHasBatchim = previousIndex >= 0 && previousIndex <= 11171 &&
                                          previousIndex % 28 != 0;
                bool nextStartsWithIeung = nextIndex >= 0 && nextIndex <= 11171 &&
                                           nextIndex / (21 * 28) == 11;
                // Preserve the user's word boundary. The Korean frontend is
                // responsible for contextual liaison; forcing a rewritten
                // spelling here damaged names, compounds, and some spacing.
                return match.Value;
            });

            if (text.Length > 0 && !Regex.IsMatch(text, @"[,.!?…]$")) text += ".";
            return text;
        }

        private static char NeutralizeBatchimForWordBoundary(char syllable)
        {
            int index = syllable - 0xAC00;
            if (index < 0 || index > 11171) return syllable;
            int batchim = index % 28;
            // Standard seven representative final consonants used before a
            // vowel-initial word: ㄱ, ㄴ, ㄷ, ㄹ, ㅁ, ㅂ, ㅇ.
            int[] representative = {
                0, 1, 1, 1, 4, 4, 4, 7, 8, 1, 16, 8, 8, 8,
                17, 8, 16, 17, 17, 7, 7, 21, 7, 7, 1, 7, 17, 7
            };
            int normalizedIndex = index - batchim + representative[batchim];
            return (char)(0xAC00 + normalizedIndex);
        }

        private static bool IsPlausibleV44Duration(string text, double seconds)
        {
            int characters = Regex.Replace(text, @"\s|[^가-힣0-9A-Za-z]", "").Length;
            double maximum = Math.Max(6.5, Math.Max(1, characters) * 0.30);
            return seconds >= 0.25 && seconds <= maximum;
        }

        private static void ApplyV44WaveBalance(string file)
        {
            WaveFormat format;
            byte[] pcm;
            using (WaveFileReader reader = new WaveFileReader(file))
            {
                format = reader.WaveFormat;
                if (format.Encoding != WaveFormatEncoding.Pcm ||
                    format.BitsPerSample != 16 || format.Channels != 1)
                    return;
                using (MemoryStream audio = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        audio.Write(buffer, 0, read);
                    pcm = audio.ToArray();
                }
            }

            int sampleCount = pcm.Length / 2;
            if (sampleCount < 32) return;
            double[] samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768.0;

            ApplyZeroPhasePeak(samples, format.SampleRate, 750.0, 0.8, -0.75);
            ApplyZeroPhasePeak(samples, format.SampleRate, 2800.0, 0.8, 0.75);

            double peak = 0.0;
            for (int i = 0; i < sampleCount; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            double scale = peak > 0.95 ? 0.95 / peak : 1.0;
            for (int i = 0; i < sampleCount; i++)
            {
                int value = (int)Math.Round(samples[i] * scale * 32767.0);
                value = Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
                WriteInt16(pcm, i * 2, (short)value);
            }

            string balanced = file + "." + Guid.NewGuid().ToString("N") + ".wav";
            try
            {
                using (WaveFileWriter writer = new WaveFileWriter(balanced, format))
                    writer.Write(pcm, 0, pcm.Length);
                File.Delete(file);
                File.Move(balanced, file);
            }
            finally
            {
                try { if (File.Exists(balanced)) File.Delete(balanced); } catch { }
            }
        }

        private static void ApplyZeroPhasePeak(double[] samples, int sampleRate,
            double frequency, double q, double gainDb)
        {
            double amplitude = Math.Pow(10.0, gainDb / 40.0);
            double omega = 2.0 * Math.PI * frequency / sampleRate;
            double alpha = Math.Sin(omega) / (2.0 * q);
            double cosine = Math.Cos(omega);
            double a0 = 1.0 + alpha / amplitude;
            double[] coefficients = {
                (1.0 + alpha * amplitude) / a0,
                (-2.0 * cosine) / a0,
                (1.0 - alpha * amplitude) / a0,
                (-2.0 * cosine) / a0,
                (1.0 - alpha / amplitude) / a0
            };
            ApplyBiquad(samples, coefficients);
            Array.Reverse(samples);
            ApplyBiquad(samples, coefficients);
            Array.Reverse(samples);
        }

        private static void ApplyBiquad(double[] samples, double[] c)
        {
            double x1 = 0.0, x2 = 0.0, y1 = 0.0, y2 = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i];
                double y = c[0] * x + c[1] * x1 + c[2] * x2 - c[3] * y1 - c[4] * y2;
                samples[i] = y;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
            }
        }

        private static double[] AnalyzeWaveQuality(string file)
        {
            using (AudioFileReader reader = new AudioFileReader(file))
            {
                float[] buffer = new float[8192];
                double squares = 0;
                long count = 0;
                long nearSilence = 0;
                long clickLike = 0;
                float previous = 0;
                bool hasPrevious = false;
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        float sample = buffer[i];
                        squares += sample * sample;
                        if (Math.Abs(sample) < 0.01) nearSilence++;
                        if (hasPrevious && Math.Abs(sample - previous) > 0.55)
                            clickLike++;
                        previous = sample;
                        hasPrevious = true;
                    }
                    count += read;
                }
                if (count == 0) return new[] { 0.0, 1.0, 999.0 };
                double seconds = count / (double)(reader.WaveFormat.SampleRate *
                    Math.Max(1, reader.WaveFormat.Channels));
                return new[] {
                    Math.Sqrt(squares / count),
                    nearSilence / (double)count,
                    clickLike / Math.Max(0.01, seconds)
                };
            }
        }

        private static int RepairIsolatedMashiroClicks(string file)
        {
            WaveFormat format;
            byte[] pcm;
            using (WaveFileReader reader = new WaveFileReader(file))
            {
                format = reader.WaveFormat;
                if (format.Encoding != WaveFormatEncoding.Pcm ||
                    format.BitsPerSample != 16 || format.Channels != 1)
                    return 0;
                using (MemoryStream audio = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        audio.Write(buffer, 0, read);
                    pcm = audio.ToArray();
                }
            }

            int sampleCount = pcm.Length / 2;
            if (sampleCount < 8) return 0;
            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(pcm, 0, samples, 0, sampleCount * 2);
            List<int> jumps = new List<int>();
            const int jumpThreshold = 18022; // 0.55 in normalized PCM16.
            for (int i = 0; i + 1 < sampleCount; i++)
            {
                int difference = samples[i + 1] - samples[i];
                if (Math.Abs(difference) > jumpThreshold) jumps.Add(i);
            }

            int repairs = 0;
            const int maxSpan = 4;
            const int returnTolerance = 8192; // Endpoints must return within 0.25.
            for (int index = 0; index < jumps.Count;)
            {
                int first = jumps[index];
                int last = first;
                int next = index + 1;
                while (next < jumps.Count && jumps[next] - last <= maxSpan)
                {
                    last = jumps[next];
                    next++;
                }
                int groupCount = next - index;
                if (groupCount >= 2 && last - first <= maxSpan)
                {
                    int left = first;
                    int right = last + 1;
                    int firstJump = samples[left + 1] - samples[left];
                    int lastJump = samples[right] - samples[right - 1];
                    int endpointDifference = samples[right] - samples[left];
                    if ((long)firstJump * lastJump < 0 &&
                        Math.Abs(endpointDifference) <= returnTolerance)
                    {
                        int interior = right - left - 1;
                        for (int offset = 1; offset <= interior; offset++)
                        {
                            double ratio = offset / (double)(interior + 1);
                            samples[left + offset] = (short)Math.Round(
                                samples[left] + endpointDifference * ratio);
                        }
                        repairs++;
                    }
                }
                index = next;
            }
            if (repairs == 0) return 0;

            Buffer.BlockCopy(samples, 0, pcm, 0, sampleCount * 2);
            string repaired = file + "." + Guid.NewGuid().ToString("N") + ".wav";
            try
            {
                using (WaveFileWriter writer = new WaveFileWriter(repaired, format))
                    writer.Write(pcm, 0, pcm.Length);
                File.Delete(file);
                File.Move(repaired, file);
            }
            finally
            {
                try { if (File.Exists(repaired)) File.Delete(repaired); } catch { }
            }
            return repairs;
        }

        private static double WaveDuration(string file)
        {
            using (AudioFileReader reader = new AudioFileReader(file))
                return reader.TotalTime.TotalSeconds;
        }

        private static bool IsPlausibleMashiroDuration(string text, double seconds, double speed)
        {
            int hangul = Regex.Matches(text, @"[가-힣]").Count;
            int punctuation = Regex.Matches(text, @"[,.!?…]").Count;
            if (hangul == 0) return seconds >= 0.25 && seconds <= Math.Max(5.0, text.Length * 0.45);

            // Broad bounds preserve natural timing but reject semantic-token
            // runaways (invented breaths/syllables) and severely swallowed text.
            double speedScale = 1.0 / Math.Max(0.72, speed);
            double minimum = MinimumMashiroDuration(text, speed);
            double maximum = Math.Max(4.0,
                (hangul / 4.0 + punctuation * 0.45 + 1.8) * speedScale);
            return seconds >= minimum && seconds <= maximum;
        }

        private static double MinimumMashiroDuration(string text, double speed)
        {
            int hangul = Regex.Matches(text, @"[가-힣]").Count;
            if (hangul == 0) return 0.25;
            double speedScale = 1.0 / Math.Max(0.72, speed);
            // Reject locally rushed takes more aggressively than the former
            // 7.2 syllables/sec bound. Only the rejected phrase is retried.
            return Math.Max(0.48, hangul / 6.35 * speedScale);
        }

        private void EnsureMashiroServer()
        {
            lock (mashiroServerSync)
            {
                if (MashiroServerReady())
                {
                    // A previous abnormal exit can leave api_v2.py alive.
                    // Re-select the frozen weights once before attaching.
                    if (!mashiroServerVerified)
                    {
                        if (mashiroProcess == null) VerifyMashiroWeights();
                        mashiroServerVerified = true;
                    }
                    return;
                }

                DisposeExitedMashiroProcess();
                ValidateMashiroAssets();
                Exception lastStartError = null;
                for (int launch = 1; launch <= 2; launch++)
                {
                    try
                    {
                        StartMashiroProcess();
                        // Model data stays on the HDD, while Numba's writable
                        // cache is redirected to the SSD-backed user profile.
                        DateTime deadline = DateTime.UtcNow.AddSeconds(300);
                        while (DateTime.UtcNow < deadline)
                        {
                            if (mashiroProcess.HasExited)
                                throw new InvalidOperationException(
                                    "Mashiro AI 엔진이 시작 중 종료됐습니다. 종료 코드: " +
                                    mashiroProcess.ExitCode);
                            if (MashiroServerReady())
                            {
                                VerifyMashiroWeights();
                                mashiroServerVerified = true;
                                LogStability("v4.4 엔진 준비 완료 (기동 " + launch + "회차)");
                                return;
                            }
                            Thread.Sleep(500);
                        }
                        throw new TimeoutException(
                            "Mashiro AI 엔진 준비 시간이 300초를 초과했습니다.");
                    }
                    catch (Exception ex)
                    {
                        lastStartError = ex;
                        LogStability("엔진 기동 " + launch + "회차 실패: " + ex.Message);
                        StopMashiroServerLocked();
                        if (launch < 2) Thread.Sleep(1200);
                    }
                }
                throw new InvalidOperationException(
                    "Mashiro AI 엔진을 두 번 기동했지만 준비되지 않았습니다. " +
                    "LocalTTS_stability.log를 확인하세요.", lastStartError);
            }
        }

        private static void ValidateMashiroAssets()
        {
            WriteV44RuntimeConfig();
            string python = Path.Combine(MashiroRoot, "runtime", "python.exe");
            string api = Path.Combine(MashiroRoot, "api_v2.py");
            string sovits = Path.Combine(MashiroVoiceRoot, "v44_fourway_sovits.pth");
            string reference = Path.Combine(MashiroVoiceRoot, "ref_v44_fourway.wav");
            string[] required = { python, api, MashiroConfig, MashiroV2BaseGpt,
                                  sovits, reference };
            foreach (string file in required)
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                    throw new FileNotFoundException(
                        "동결된 v4.4 엔진 구성 파일이 없거나 비어 있습니다.", file);
        }

        private static void WriteV44RuntimeConfig()
        {
            string sovits = Path.Combine(MashiroVoiceRoot,
                "v44_fourway_sovits.pth").Replace('\\', '/');
            string yaml =
                "custom:\n" +
                "  bert_base_path: GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large\n" +
                "  cnhuhbert_base_path: GPT_SoVITS/pretrained_models/chinese-hubert-base\n" +
                "  device: cuda\n" +
                "  is_half: false\n" +
                "  t2s_weights_path: " + MashiroV2BaseGpt.Replace('\\', '/') + "\n" +
                "  version: v2\n" +
                "  vits_weights_path: " + sovits + "\n" +
                "version: v2\n";
            Directory.CreateDirectory(Path.GetDirectoryName(MashiroConfig));
            File.WriteAllText(MashiroConfig, yaml, new UTF8Encoding(false));
        }

        private void StartMashiroProcess()
        {
            string python = Path.Combine(MashiroRoot, "runtime", "python.exe");
            string api = Path.Combine(MashiroRoot, "api_v2.py");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = python;
            info.Arguments = "\"" + api + "\" -a 127.0.0.1 -p " +
                MashiroApiPort + " -c \"" + MashiroConfig + "\"";
            info.WorkingDirectory = MashiroRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            // Keep the writable cache beside this standalone app. This avoids
            // AppData ACL/sandbox conflicts and still keeps it on the SSD.
            string numbaCache = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "cache", "numba");
            Directory.CreateDirectory(numbaCache);
            info.EnvironmentVariables["NUMBA_CACHE_DIR"] = numbaCache;
            mashiroProcess = Process.Start(info);
            mashiroProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrWhiteSpace(e.Data)) LogStability("engine: " + e.Data);
            };
            mashiroProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrWhiteSpace(e.Data)) LogStability("engine-error: " + e.Data);
            };
            mashiroProcess.BeginOutputReadLine();
            mashiroProcess.BeginErrorReadLine();
            try { mashiroProcess.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }

        private void DisposeExitedMashiroProcess()
        {
            if (mashiroProcess == null) return;
            try
            {
                if (!mashiroProcess.HasExited) return;
                mashiroProcess.Dispose();
                mashiroProcess = null;
            }
            catch { mashiroProcess = null; }
        }

        private static void VerifyMashiroWeights()
        {
            SetMashiroWeight("/set_gpt_weights?weights_path=", MashiroV2BaseGpt);
            SetMashiroWeight("/set_sovits_weights?weights_path=",
                Path.Combine(MashiroVoiceRoot, "v44_fourway_sovits.pth"));
            LogStability("v4.4 동결 GPT/SoVITS 가중치 적용 완료");
        }

        private static void SetMashiroWeight(string endpoint, string path)
        {
            string url = MashiroApiBase + endpoint + Uri.EscapeDataString(
                path.Replace('\\', '/'));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                if ((int)response.StatusCode != 200)
                    throw new InvalidOperationException("동결 모델 선택에 실패했습니다.");
        }

        private static bool MashiroServerReady()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(MashiroApiBase + "/docs");
                request.Method = "GET";
                request.Timeout = 800;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return (int)response.StatusCode == 200;
            }
            catch { return false; }
        }

        private void StopMashiroServer()
        {
            lock (mashiroServerSync) StopMashiroServerLocked();
        }

        private void RestartMashiroServer()
        {
            lock (mashiroServerSync)
            {
                StopMashiroServerLocked();
                EnsureMashiroServer();
            }
        }

        private void StopMashiroServerLocked()
        {
            if (mashiroProcess == null) return;
            try
            {
                if (!mashiroProcess.HasExited)
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                        MashiroApiBase + "/control?command=exit");
                    request.Timeout = 1000;
                    try { using (request.GetResponse()) { } } catch { }
                    if (!mashiroProcess.WaitForExit(2000)) mashiroProcess.Kill();
                }
            }
            catch { }
            finally
            {
                try { mashiroProcess.Dispose(); } catch { }
                mashiroProcess = null;
                mashiroServerVerified = false;
            }
        }

        private static void LogStability(string message)
        {
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "LocalTTS_stability.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1048576)
                    File.WriteAllText(file, "", Encoding.UTF8);
                File.AppendAllText(file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message +
                    Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void EnsureDiscordBus(int deviceNumber)
        {
            if (discordBus != null && discordBus.DeviceNumber == deviceNumber &&
                discordBus.IsRunning) return;
            if (relaying && discordBus != null && discordBus.DeviceNumber != deviceNumber)
                throw new InvalidOperationException(
                    "Discord 출력 장치를 바꾸려면 먼저 마이크 송출을 중지하세요.");
            bool restoreMicrophone = relaying && micBuffer != null;
            DisposeDiscordBus();
            discordBus = new DiscordAudioBus(deviceNumber);
            if (restoreMicrophone) discordBus.AddMicrophone(micBuffer);
        }

        private void DisposeDiscordBus()
        {
            if (discordBus == null) return;
            try { discordBus.Dispose(); } catch { }
            discordBus = null;
        }

        private void StopAllTts()
        {
            // Release mixer readers before deleting their temporary WAV files.
            if (discordBus != null) discordBus.StopFiles();
            PlaybackSession[] copy = playbacks.ToArray();
            playbacks.Clear();
            foreach (PlaybackSession playback in copy) playback.Dispose();
            ttsBusy = false;
            speakButton.Enabled = true;
        }

        private void SetStatus(string text, bool active)
        {
            statusLabel.Text = text;
            stateDot.ForeColor = active ? Color.FromArgb(67, 214, 132) : Color.FromArgb(215, 91, 102);
        }

        internal int RunV44EngineSelfTest()
        {
            string file = null;
            try
            {
                file = SynthesizeMashiro("오늘은 새로운 목소리로 또렷하고 자연스럽게 이야기할게요.", 0, true);
                if (!File.Exists(file) || new FileInfo(file).Length <= 44) return 31;
                double[] quality = AnalyzeWaveQuality(file);
                double duration = WaveDuration(file);
                if (quality[0] < 0.02 || quality[1] > 0.82 || quality[2] > 4.0) return 32;
                if (!IsPlausibleV44Duration("오늘은 새로운 목소리로 또렷하고 자연스럽게 이야기할게요.", duration))
                    return 33;
                return 0;
            }
            catch (Exception ex)
            {
                LogStability("v4.4 자체 검사 실패: " + ex);
                return 30;
            }
            finally
            {
                try { if (file != null) File.Delete(file); } catch { }
                StopMashiroServer();
            }
        }

        private void ShowError(string title, Exception ex)
        {
            SetStatus(title, relaying);
            MessageBox.Show(this, title + "\n\n" + ex.Message, "로컬 TTS 보이스",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static class Program
    {
        private static int OfflineRepeatedSignalSelfTest()
        {
            string file = Path.Combine(Path.GetTempPath(), "LocalTTS_SignalPathTest.wav");
            try
            {
                WaveFormat sourceFormat = new WaveFormat(32000, 16, 1);
                using (WaveFileWriter writer = new WaveFileWriter(file, sourceFormat))
                {
                    int samples = sourceFormat.SampleRate / 10;
                    byte[] tone = new byte[samples * 2];
                    for (int sample = 0; sample < samples; sample++)
                    {
                        short value = (short)(4000 * Math.Sin(2 * Math.PI * 700 * sample / 32000.0));
                        tone[sample * 2] = (byte)(value & 0xff);
                        tone[sample * 2 + 1] = (byte)((value >> 8) & 0xff);
                    }
                    writer.Write(tone, 0, tone.Length);
                }
                for (int cycle = 0; cycle < 30; cycle++)
                {
                    using (AudioFileReader reader = new AudioFileReader(file))
                    {
                        ISampleProvider provider = new MonoToStereoSampleProvider(reader);
                        provider = new WdlResamplingSampleProvider(provider,
                            DiscordAudioBus.SampleRate);
                        MixingSampleProvider mixer = new MixingSampleProvider(
                            WaveFormat.CreateIeeeFloatWaveFormat(
                                DiscordAudioBus.SampleRate, 2));
                        mixer.AddMixerInput(provider);
                        float[] buffer = new float[4096];
                        double squares = 0;
                        long count = 0;
                        int read;
                        while ((read = mixer.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int index = 0; index < read; index++)
                                squares += buffer[index] * buffer[index];
                            count += read;
                        }
                        double rms = count == 0 ? 0 : Math.Sqrt(squares / count);
                        if (rms < 0.05 || rms > 0.15) return 23;
                    }
                }
                return 0;
            }
            catch { return 24; }
            finally { try { File.Delete(file); } catch { } }
        }

        private static int LegacyOutputSelfTest()
        {
            int cable = int.MinValue;
            for (int index = 0; index < WaveOut.DeviceCount; index++)
            {
                string name = WaveOut.GetCapabilities(index).ProductName;
                if (name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                { cable = index; break; }
            }
            if (cable == int.MinValue) return 20;
            string file = Path.Combine(Path.GetTempPath(), "LocalTTS_v5_OutputTest.wav");
            try
            {
                WaveFormat format = new WaveFormat(32000, 16, 1);
                using (WaveFileWriter writer = new WaveFileWriter(file, format))
                {
                    byte[] silence = new byte[format.AverageBytesPerSecond / 20];
                    writer.Write(silence, 0, silence.Length);
                }
                for (int cycle = 0; cycle < 30; cycle++)
                {
                    using (ManualResetEvent ended = new ManualResetEvent(false))
                    using (PlaybackSession session = new PlaybackSession(file,
                        new[] { cable }, 1.0f, delegate { ended.Set(); }))
                    {
                        session.Play();
                        if (!ended.WaitOne(2000)) return 21;
                    }
                }
                return 0;
            }
            catch { return 22; }
            finally { try { File.Delete(file); } catch { } }
        }

        private static int AudioBusSelfTest()
        {
            int signalPath = OfflineRepeatedSignalSelfTest();
            if (signalPath != 0) return signalPath;
            int cable = int.MinValue;
            for (int index = 0; index < WaveOut.DeviceCount; index++)
            {
                string name = WaveOut.GetCapabilities(index).ProductName;
                if (name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                { cable = index; break; }
            }
            if (cable == int.MinValue) return 10;

            string file = Path.Combine(Path.GetTempPath(), "LocalTTS_CableSelfTest.wav");
            try
            {
                WaveFormat sourceFormat = new WaveFormat(32000, 16, 1);
                using (WaveFileWriter writer = new WaveFileWriter(file, sourceFormat))
                {
                    byte[] silence = new byte[sourceFormat.AverageBytesPerSecond / 4];
                    writer.Write(silence, 0, silence.Length);
                }
                const int stressInputs = 8;
                using (CountdownEvent completed = new CountdownEvent(stressInputs))
                using (DiscordAudioBus bus = new DiscordAudioBus(cable))
                {
                    BufferedWaveProvider microphone = new BufferedWaveProvider(
                        new WaveFormat(DiscordAudioBus.SampleRate, 16, 1));
                    microphone.BufferDuration = TimeSpan.FromMilliseconds(500);
                    microphone.ReadFully = true;
                    microphone.DiscardOnBufferOverflow = true;
                    bus.AddMicrophone(microphone);
                    for (int index = 0; index < stressInputs; index++)
                        bus.PlayFile(file, 1.0f, delegate { completed.Signal(); });
                    if (!completed.Wait(5000)) return 11;
                    for (int cycle = 0; cycle < 30; cycle++)
                    {
                        using (ManualResetEvent one = new ManualResetEvent(false))
                        {
                            bus.PlayFile(file, 1.0f, delegate { one.Set(); });
                            if (!one.WaitOne(2000)) return 15;
                        }
                        if (!bus.IsRunning) return 16;
                        Thread.Sleep(50);
                    }
                    bus.RemoveMicrophone();
                }
                for (int reopen = 0; reopen < 8; reopen++)
                {
                    using (ManualResetEvent one = new ManualResetEvent(false))
                    using (DiscordAudioBus bus = new DiscordAudioBus(cable))
                    {
                        bus.PlayFile(file, 1.0f, delegate { one.Set(); });
                        if (!one.WaitOne(2000)) return 17;
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(),
                        "LocalTTS_CableSelfTest_error.txt"), ex.ToString(), Encoding.UTF8);
                }
                catch { }
                return 12;
            }
            finally { try { File.Delete(file); } catch { } }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--v44-self-test")
            {
                using (MainForm form = new MainForm())
                    return form.RunV44EngineSelfTest();
            }
            if (args.Length > 0 && args[0] == "--audio-bus-self-test")
#if LEGACY_V5
                return LegacyOutputSelfTest();
#else
                return AudioBusSelfTest();
#endif
            if (args.Length > 0 && args[0] == "--self-test")
            {
                try
                {
                    int outputs = WaveOut.DeviceCount;
                    if (outputs < 1) return 2;
                    string root = AppDomain.CurrentDomain.BaseDirectory;
                    string[] required = {
                        Path.Combine(root, "NAudio.dll"),
                        Path.Combine(root, "models", "v44", "v44_fourway_sovits.pth"),
                        Path.Combine(root, "models", "v44", "ref_v44_fourway.wav"),
                        @"A:\LocalTTS-AI\GPT-SoVITS-v2-240821\runtime\python.exe",
                        @"A:\LocalTTS-AI\voices\mashiro_candidate_v5b\mashiro_candidate_v5b-e1.ckpt"
                    };
                    foreach (string file in required)
                        if (!File.Exists(file) || new FileInfo(file).Length == 0) return 3;
                    return 0;
                }
                catch { return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            const string instanceName = @"Local\LocalTTS_Independent_V44";
            bool created;
            using (Mutex instanceMutex = new Mutex(true, instanceName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("LocalTTS가 이미 실행 중입니다.", "로컬 TTS 보이스",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 4;
                }
                Application.Run(new MainForm());
            }
            return 0;
        }
    }
}
