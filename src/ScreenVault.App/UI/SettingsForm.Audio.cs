using ScreenVault.App.UI.Controls;
using ScreenVault.App.UI.Theming;
using ScreenVault.Core.Audio;
using ScreenVault.Core.Settings;

namespace ScreenVault.App.UI;

public sealed partial class SettingsForm
{
    private readonly ModernComboBox _cmbMicMode = new();
    private readonly ModernComboBox _cmbMicDevice = new();
    private readonly ModernComboBox _cmbOutputMode = new();
    private readonly ModernComboBox _cmbOutputDevice = new();
    private SettingRow _micDeviceRow = null!;
    private SettingRow _outputDeviceRow = null!;
    private readonly ToggleSwitch _chkUnmuteOnNewSession = new();
    private readonly ModernSlider _trkMicGain = new() { Minimum = -20, Maximum = 20, Origin = 0 };
    private readonly TextLabel _lblMicGainVal = new("0 dB", Typography.BodyStrong);
    private readonly ModernSlider _trkSysGain = new() { Minimum = -20, Maximum = 20, Origin = 0 };
    private readonly TextLabel _lblSysGainVal = new("0 dB", Typography.BodyStrong);
    private readonly VuMeterControl _vuMic = new() { AccessibleName = "Microphone level" };
    private readonly VuMeterControl _vuSys = new() { AccessibleName = "System audio level" };
    private readonly NumberField _numJitterBuffer = new() { Minimum = 30, Maximum = 500, Increment = 10, Suffix = "ms" };
    private readonly NumberField _numAvOffset = new() { Minimum = -500, Maximum = 500, Increment = 10, Suffix = "ms" };

    private readonly List<MicMode> _micModeValues = [MicMode.DefaultCommunications, MicMode.DefaultMultimedia, MicMode.Specific, MicMode.None];
    private readonly List<OutputMode> _outputModeValues = [OutputMode.DefaultPlusCommunications, OutputMode.Default, OutputMode.AllActive, OutputMode.Specific, OutputMode.None];
    private readonly List<string?> _micDeviceIds = [];
    private readonly List<string?> _outputDeviceIds = [];

    private StackPanel BuildAudioPage()
    {
        _cmbMicMode.Items.AddRange(["Windows default – communications (recommended)", "Windows default – multimedia", "A specific microphone…", "Don't record the microphone"]);
        _cmbOutputMode.Items.AddRange(["Default + communications (recommended)", "Default output only", "All playback devices", "A specific output device…", "Don't record system audio"]);

        foreach (var combo in new[] { _cmbMicMode, _cmbMicDevice, _cmbOutputMode, _cmbOutputDevice })
        {
            combo.Width = 260;
        }

        _numJitterBuffer.Width = 120;
        _numAvOffset.Width = 120;

        PopulateAudioDeviceLists();
        _cmbMicMode.SelectedIndexChanged += (_, _) => UpdateAudioDeviceRowVisibility();
        _cmbOutputMode.SelectedIndexChanged += (_, _) => UpdateAudioDeviceRowVisibility();

        var audio = CreatePage("Audio", "Microphone and speaker capture sources, volumes, meters, and timing adjustments.");

        audio.Controls.Add(Section("Sources"));
        _micDeviceRow = Row("Microphone device", "Only used while \"A specific microphone\" is selected above.", _cmbMicDevice, Glyphs.Microphone);
        _outputDeviceRow = Row("Output device", "Only used while \"A specific output device\" is selected above.", _cmbOutputDevice, Glyphs.Volume);

        audio.Controls.Add(Card(
            Row("Microphone", "Recommended: records the microphone your call is using (Teams, Zoom, browser), otherwise the Windows default.", _cmbMicMode, Glyphs.Microphone),
            _micDeviceRow,
            Row("System audio", "Recommended: records your default speakers plus any headset or device a call or app is playing to.", _cmbOutputMode, Glyphs.Volume),
            _outputDeviceRow,
            Row("Unmute mic when recording starts", "Automatically ensures microphone is unmuted whenever a new session begins.", _chkUnmuteOnNewSession, Glyphs.Microphone)));

        audio.Controls.Add(Section("Levels"));
        _trkMicGain.ValueChanged += (_, _) => _lblMicGainVal.Text = FormatGain(_trkMicGain.Value);
        _trkSysGain.ValueChanged += (_, _) => _lblSysGainVal.Text = FormatGain(_trkSysGain.Value);
        _trkMicGain.AccessibleName = "Microphone gain";
        _trkSysGain.AccessibleName = "System audio gain";
        _vuMic.Size = new Size(260, 20);
        _vuSys.Size = new Size(260, 20);

        audio.Controls.Add(Card(
            Row("Microphone gain", "Boost a quiet microphone or soften a loud one.", GainEditor(_trkMicGain, _lblMicGainVal), Glyphs.Microphone),
            Row("System audio gain", "Balance other participants against your own voice.", GainEditor(_trkSysGain, _lblSysGainVal), Glyphs.Volume),
            Row("Microphone level", "Speak to check that your voice is picked up.", _vuMic),
            Row("System audio level", "Play something to check that computer audio is captured.", _vuSys)));

        audio.Controls.Add(Section("Advanced"));
        audio.Controls.Add(Card(
            Row("Jitter buffer", "Smooths out Bluetooth audio packets. Higher values are steadier.", _numJitterBuffer),
            Row("Audio / video offset", "Shift the audio if it is out of sync with the video picture.", _numAvOffset)));

        return audio;
    }

    private void LoadAudioSettings(AppSettings s)
    {
        _cmbMicMode.SelectedIndex = SelectValue(_cmbMicMode, _micModeValues, s.Audio.MicMode,
            _ => "A specific microphone (chosen earlier)");
        _cmbOutputMode.SelectedIndex = SelectValue(_cmbOutputMode, _outputModeValues, s.Audio.OutputMode,
            mode => mode == OutputMode.AllActive ? "All playback devices" : "A specific output device (chosen earlier)");
        _cmbMicDevice.SelectedIndex = SelectDevice(_cmbMicDevice, _micDeviceIds, s.Audio.MicDeviceId);
        _cmbOutputDevice.SelectedIndex = SelectDevice(_cmbOutputDevice, _outputDeviceIds, s.Audio.OutputDeviceId);
        UpdateAudioDeviceRowVisibility();

        _chkUnmuteOnNewSession.Checked = s.Audio.UnmuteOnNewSession;
        _trkMicGain.Value = (int)Math.Clamp(s.Audio.MicGainDb, -20, 20);
        _lblMicGainVal.Text = FormatGain(_trkMicGain.Value);
        _trkSysGain.Value = (int)Math.Clamp(s.Audio.SystemGainDb, -20, 20);
        _lblSysGainVal.Text = FormatGain(_trkSysGain.Value);
        _numJitterBuffer.Value = Math.Clamp(s.Audio.JitterTargetMs, 30, 500);
        _numAvOffset.Value = Math.Clamp(s.Audio.AvOffsetMs, -500, 500);
    }

    private void SaveAudioSettings(AppSettings s)
    {
        s.Audio.MicMode = ValueAt(_micModeValues, _cmbMicMode.SelectedIndex, MicMode.DefaultCommunications);
        s.Audio.OutputMode = ValueAt(_outputModeValues, _cmbOutputMode.SelectedIndex, OutputMode.DefaultPlusCommunications);
        s.Audio.MicDeviceId = ValueAt(_micDeviceIds, _cmbMicDevice.SelectedIndex, null);
        s.Audio.OutputDeviceId = ValueAt(_outputDeviceIds, _cmbOutputDevice.SelectedIndex, null);
        s.Audio.UnmuteOnNewSession = _chkUnmuteOnNewSession.Checked;
        s.Audio.MicGainDb = _trkMicGain.Value;
        s.Audio.SystemGainDb = _trkSysGain.Value;
        s.Audio.JitterTargetMs = (int)_numJitterBuffer.Value;
        s.Audio.AvOffsetMs = (int)_numAvOffset.Value;
    }

    private void UpdateMeters()
    {
        if (_audioEngine == null) return;

        var status = _audioEngine.GetStatus();
        float micPeak = -60f, micRms = -60f, sysPeak = -60f, sysRms = -60f;
        foreach (var device in status.ActiveDevices)
        {
            if (device.IsLoopback)
            {
                sysPeak = Math.Max(sysPeak, device.PeakDb);
                sysRms = Math.Max(sysRms, device.RmsDb);
            }
            else
            {
                micPeak = Math.Max(micPeak, device.PeakDb);
                micRms = Math.Max(micRms, device.RmsDb);
            }
        }

        _vuMic.IsMuted = status.IsMicMuted;
        _vuMic.SetLevels(micPeak, micPeak, micRms, micRms);
        _vuSys.SetLevels(sysPeak, sysPeak, sysRms, sysRms);
    }
}
