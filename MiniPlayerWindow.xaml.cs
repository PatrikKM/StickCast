using System.Windows;

namespace StickCast;

public partial class MiniPlayerWindow : Window
{
    private bool _syncing;

    public event Action? PrevClicked;
    public event Action? PlayPauseClicked;
    public event Action? StopClicked;
    public event Action? NextClicked;
    public event Action<int>? VolumeChangedByUser;

    public MiniPlayerWindow()
    {
        InitializeComponent();
    }

    public void UpdateState(string nowPlaying, string stateText, int volume, bool hasVolume)
    {
        if (TitleText != null) TitleText.Text = nowPlaying;
        if (StateText != null) StateText.Text = stateText;
        if (VolumeSlider != null)
        {
            _syncing = true;
            VolumeSlider.IsEnabled = hasVolume;
            VolumeSlider.Value = Math.Clamp(volume, 0, 100);
            _syncing = false;
        }
        if (VolumeText != null) VolumeText.Text = $"{Math.Clamp(volume, 0, 100)}%";
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => PrevClicked?.Invoke();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => NextClicked?.Invoke();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (VolumeText != null) VolumeText.Text = $"{val}%";
        if (_syncing) return;
        VolumeChangedByUser?.Invoke(val);
    }
}
