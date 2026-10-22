using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Nasaq.Controls;
using Nasaq.ViewModels;

namespace Nasaq.Views;

public partial class EditorView : UserControl
{
    private readonly DispatcherTimer _playbackTimer;
    private EditorViewModel? _viewModel;
    private bool _updatingFromTimer;
    private bool _suppressSeek;
    private bool _videoMediaReady;
    private bool _audioMediaReady;
    private bool _mergeShouldPlay;
    private bool _mergeSeekPending;
    private int _mergeTrackIndex = -1;
    private double _mergeTrackOffset;
    private string? _videoSourcePath;
    private string? _audioSourcePath;

    public EditorView()
    {
        InitializeComponent();
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        DataContextChanged += EditorView_DataContextChanged;
        Loaded += EditorView_Loaded;
        Unloaded += EditorView_Unloaded;
    }

    public void Activate()
    {
        if (DataContext is EditorViewModel viewModel)
            viewModel.Activate();
    }

    private void EditorView_Loaded(object sender, RoutedEventArgs e)
    {
        Attach(DataContext as EditorViewModel);
        SynchroniseSources();
    }

    private void EditorView_Unloaded(object sender, RoutedEventArgs e) => StopPlayback();

    private void EditorView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        Attach(e.NewValue as EditorViewModel);
        SynchroniseSources();
    }

    private void Attach(EditorViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(_viewModel, viewModel)) return;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.AudioTracks.CollectionChanged += AudioTracks_CollectionChanged;
    }

    private void Detach()
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.AudioTracks.CollectionChanged -= AudioTracks_CollectionChanged;
        _viewModel = null;
    }

    private void AudioTracks_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.IsAudioMerge == true)
        {
            StopPlayback();
            _mergeTrackIndex = -1;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;

        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.ToolId):
            case nameof(EditorViewModel.AudioModeId):
                StopPlayback();
                SynchroniseSources();
                break;
            case nameof(EditorViewModel.VideoPath):
                SetVideoSource(vm.VideoPath);
                break;
            case nameof(EditorViewModel.AudioPath):
                if (!vm.IsAudioMerge) SetAudioSource(vm.AudioPath);
                break;
            case nameof(EditorViewModel.VideoPosition) when !_updatingFromTimer && !_suppressSeek:
                SeekVideo(vm.VideoPosition);
                break;
            case nameof(EditorViewModel.AudioPosition) when !_updatingFromTimer && !_suppressSeek:
                if (vm.IsAudioTrim) SeekAudioTrim(vm.AudioPosition);
                break;
            case nameof(EditorViewModel.AudioMergePosition) when !_updatingFromTimer && !_suppressSeek:
                if (vm.IsAudioMerge) SeekAudioMerge(vm.AudioMergePosition, vm.IsMediaPlaying);
                break;
            case nameof(EditorViewModel.HasActiveEdit) when !vm.HasActiveEdit:
                StopPlayback();
                break;
        }
    }

    private void SynchroniseSources()
    {
        var vm = _viewModel;
        if (vm is null || !IsLoaded) return;

        if (vm.IsVideoTool)
        {
            SetVideoSource(vm.VideoPath);
            SetAudioSource(null);
        }
        else if (vm.IsAudioTool && vm.IsAudioMerge)
        {
            SetVideoSource(null);
            if (_mergeTrackIndex >= 0 && _mergeTrackIndex < vm.AudioTracks.Count)
                SetAudioSource(vm.AudioTracks[_mergeTrackIndex].Path);
            else
                SetAudioSource(null);
        }
        else if (vm.IsAudioTool)
        {
            SetVideoSource(null);
            SetAudioSource(vm.AudioPath);
        }
        else
        {
            SetVideoSource(null);
            SetAudioSource(null);
        }
    }

    private void SetVideoSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _videoMediaReady = false;
            _videoSourcePath = null;
            VideoPlayer.Stop();
            VideoPlayer.Source = null;
            VideoPlayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.Equals(_videoSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            VideoPlayer.Visibility = Visibility.Visible;
            return;
        }

        _videoMediaReady = false;
        _videoSourcePath = path;
        VideoPlayer.Stop();
        VideoPlayer.Source = new Uri(path);
        VideoPlayer.Visibility = Visibility.Visible;
    }

    private void SetAudioSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _audioMediaReady = false;
            _audioSourcePath = null;
            _mergeSeekPending = false;
            AudioPlayer.Stop();
            AudioPlayer.Source = null;
            return;
        }

        if (string.Equals(_audioSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _audioMediaReady = true;
            return;
        }

        _audioMediaReady = false;
        _audioSourcePath = path;
        _mergeSeekPending = false;
        AudioPlayer.Stop();
        AudioPlayer.Source = new Uri(path);
    }

    // ───────────────────────── playback ─────────────────────────

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;

        if (vm.IsVideoTool)
        {
            ToggleVideoPlayback();
            return;
        }

        if (!vm.IsAudioTool) return;
        if (vm.IsAudioMerge) ToggleMergePlayback();
        else ToggleAudioTrimPlayback();
    }

    private void ToggleVideoPlayback()
    {
        var vm = _viewModel;
        if (vm is null || string.IsNullOrWhiteSpace(vm.VideoPath)) return;
        if (vm.IsMediaPlaying)
        {
            PauseVideo();
            return;
        }

        if (VideoPlayer.Source is null) SetVideoSource(vm.VideoPath);
        if (!_videoMediaReady)
        {
            // MediaElement queues Play until MediaOpened in most formats. Start
            // the timer once it is ready; this avoids a stuck play button.
            _playbackTimer.Start();
            VideoPlayer.Play();
            vm.IsMediaPlaying = true;
            return;
        }

        var position = vm.VideoPosition;
        if (position < vm.VideoStart || position >= vm.VideoEnd - 0.02)
            position = vm.VideoStart;
        _suppressSeek = true;
        vm.VideoPosition = position;
        _suppressSeek = false;
        SeekVideo(position);
        VideoPlayer.Play();
        vm.IsMediaPlaying = true;
        _playbackTimer.Start();
    }

    private void PauseVideo()
    {
        var vm = _viewModel;
        VideoPlayer.Pause();
        _playbackTimer.Stop();
        if (vm is not null)
        {
            _updatingFromTimer = true;
            vm.SetPlaybackPosition(VideoPlayer.Position.TotalSeconds);
            _updatingFromTimer = false;
            vm.IsMediaPlaying = false;
        }
    }

    private void ToggleAudioTrimPlayback()
    {
        var vm = _viewModel;
        if (vm is null || string.IsNullOrWhiteSpace(vm.AudioPath)) return;
        if (vm.IsMediaPlaying)
        {
            PauseAudio();
            return;
        }

        if (AudioPlayer.Source is null) SetAudioSource(vm.AudioPath);
        var position = vm.AudioPosition;
        if (position < vm.AudioStart || position >= vm.AudioEnd - 0.02)
            position = vm.AudioStart;
        _suppressSeek = true;
        vm.AudioPosition = position;
        _suppressSeek = false;
        if (_audioMediaReady)
            AudioPlayer.Position = TimeSpan.FromSeconds(position);
        AudioPlayer.Play();
        vm.IsMediaPlaying = true;
        _playbackTimer.Start();
    }

    private void ToggleMergePlayback()
    {
        var vm = _viewModel;
        if (vm is null || vm.AudioTracks.Count == 0) return;
        if (vm.IsMediaPlaying)
        {
            PauseAudio();
            return;
        }

        var position = vm.AudioMergePosition;
        if (position >= vm.AudioMergeDuration - 0.02) position = 0;
        SeekAudioMerge(position, play: true);
        vm.IsMediaPlaying = true;
        _playbackTimer.Start();
    }

    private void PauseAudio(bool atEnd = false)
    {
        var vm = _viewModel;
        AudioPlayer.Pause();
        _playbackTimer.Stop();
        _mergeShouldPlay = false;
        if (vm is null) return;

        if (vm.IsAudioMerge)
        {
            if (atEnd)
            {
                _updatingFromTimer = true;
                vm.AudioMergePosition = vm.AudioMergeDuration;
                _updatingFromTimer = false;
            }
            else
            {
                UpdateMergePositionFromPlayer();
            }
        }
        else
        {
            _updatingFromTimer = true;
            vm.AudioPosition = atEnd
                ? vm.AudioEnd
                : AudioPlayer.Position.TotalSeconds;
            _updatingFromTimer = false;
        }

        vm.IsMediaPlaying = false;
    }

    private void SeekVideo(double seconds)
    {
        if (!_videoMediaReady) return;
        var clamped = Math.Clamp(seconds, 0, Math.Max(0, _viewModel?.VideoDuration ?? 0));
        VideoPlayer.Position = TimeSpan.FromSeconds(clamped);
    }

    private void SeekAudioTrim(double seconds)
    {
        if (!_audioMediaReady) return;
        var clamped = Math.Clamp(seconds, 0, Math.Max(0, _viewModel?.AudioDuration ?? 0));
        AudioPlayer.Position = TimeSpan.FromSeconds(clamped);
    }

    private void SeekAudioMerge(double seconds, bool play)
    {
        var vm = _viewModel;
        if (vm is null || vm.AudioTracks.Count == 0) return;
        var total = Math.Max(0.01, vm.AudioMergeDuration);
        var target = Math.Clamp(seconds, 0, total);
        var trackIndex = 0;
        for (var i = 0; i < vm.AudioTracks.Count; i++)
        {
            if (target >= vm.AudioTracks[i].StartOffset - 0.001 &&
                target < vm.AudioTracks[i].EndOffset - 0.001)
            {
                trackIndex = i;
                break;
            }
            trackIndex = i;
        }

        var track = vm.AudioTracks[trackIndex];
        _mergeTrackIndex = trackIndex;
        _mergeTrackOffset = Math.Clamp(target - track.StartOffset, 0, track.Duration);
        _mergeShouldPlay = play;

        if (!string.Equals(_audioSourcePath, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            _audioMediaReady = false;
            _mergeSeekPending = true;
            _audioSourcePath = track.Path;
            AudioPlayer.Stop();
            AudioPlayer.Source = new Uri(track.Path);
            return;
        }

        if (_audioMediaReady)
        {
            AudioPlayer.Position = TimeSpan.FromSeconds(_mergeTrackOffset);
            if (play) AudioPlayer.Play();
            else AudioPlayer.Pause();
        }
        else
        {
            _mergeSeekPending = true;
        }
    }

    private void UpdateMergePositionFromPlayer()
    {
        var vm = _viewModel;
        if (vm is null || _mergeTrackIndex < 0 || _mergeTrackIndex >= vm.AudioTracks.Count) return;
        var track = vm.AudioTracks[_mergeTrackIndex];
        var position = track.StartOffset + Math.Max(0, AudioPlayer.Position.TotalSeconds);
        _updatingFromTimer = true;
        vm.AudioMergePosition = position;
        _updatingFromTimer = false;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        var vm = _viewModel;
        if (vm is null)
        {
            _playbackTimer.Stop();
            return;
        }

        if (vm.IsVideoTool)
        {
            if (!_videoMediaReady)
            {
                if (!vm.IsMediaPlaying) _playbackTimer.Stop();
                return;
            }

            var seconds = VideoPlayer.Position.TotalSeconds;
            if (double.IsNaN(seconds) || seconds < 0) return;
            _updatingFromTimer = true;
            vm.VideoPosition = seconds;
            _updatingFromTimer = false;

            if (seconds >= vm.VideoEnd - 0.03)
            {
                if (vm.LoopMedia)
                {
                    SeekVideo(vm.VideoStart);
                }
                else
                {
                    VideoPlayer.Pause();
                    _playbackTimer.Stop();
                    vm.IsMediaPlaying = false;
                    _suppressSeek = true;
                    vm.VideoPosition = vm.VideoEnd;
                    _suppressSeek = false;
                }
            }
            else if (seconds < vm.VideoStart - 0.15)
            {
                SeekVideo(vm.VideoStart);
            }
            return;
        }

        if (!vm.IsAudioTool) return;

        if (vm.IsAudioMerge)
        {
            if (!_audioMediaReady)
            {
                if (!vm.IsMediaPlaying) _playbackTimer.Stop();
                return;
            }

            UpdateMergePositionFromPlayer();
            if (_mergeTrackIndex < 0 || _mergeTrackIndex >= vm.AudioTracks.Count)
            {
                StopPlayback();
                return;
            }

            var track = vm.AudioTracks[_mergeTrackIndex];
            if (AudioPlayer.Position.TotalSeconds >= track.Duration - 0.03)
            {
                var next = _mergeTrackIndex + 1;
                if (next < vm.AudioTracks.Count)
                {
                    _mergeTrackIndex = next;
                    _mergeTrackOffset = 0;
                    _mergeShouldPlay = true;
                    _mergeSeekPending = true;
                    _audioMediaReady = false;
                    _audioSourcePath = vm.AudioTracks[next].Path;
                    AudioPlayer.Stop();
                    AudioPlayer.Source = new Uri(vm.AudioTracks[next].Path);
                }
                else if (vm.LoopMedia && vm.AudioTracks.Count > 0)
                {
                    SeekAudioMerge(0, play: true);
                }
                else
                {
                    _playbackTimer.Stop();
                    AudioPlayer.Pause();
                    vm.IsMediaPlaying = false;
                    _mergeShouldPlay = false;
                    _updatingFromTimer = true;
                    vm.AudioMergePosition = vm.AudioMergeDuration;
                    _updatingFromTimer = false;
                }
            }
            return;
        }

        if (vm.IsAudioTrim)
        {
            if (!_audioMediaReady)
            {
                if (!vm.IsMediaPlaying) _playbackTimer.Stop();
                return;
            }

            var seconds = AudioPlayer.Position.TotalSeconds;
            if (double.IsNaN(seconds) || seconds < 0) return;
            _updatingFromTimer = true;
            vm.AudioPosition = seconds;
            _updatingFromTimer = false;

            if (seconds >= vm.AudioEnd - 0.03)
            {
                if (vm.LoopMedia)
                {
                    SeekAudioTrim(vm.AudioStart);
                }
                else
                {
                    AudioPlayer.Pause();
                    _playbackTimer.Stop();
                    vm.IsMediaPlaying = false;
                    _suppressSeek = true;
                    vm.AudioPosition = vm.AudioEnd;
                    _suppressSeek = false;
                }
            }
            else if (seconds < vm.AudioStart - 0.15)
            {
                SeekAudioTrim(vm.AudioStart);
            }
        }
    }

    private void StopPlayback()
    {
        _playbackTimer.Stop();
        VideoPlayer.Pause();
        AudioPlayer.Pause();
        _mergeShouldPlay = false;
        if (_viewModel is not null) _viewModel.IsMediaPlaying = false;
    }

    // ───────────────────────── media events ─────────────────────────

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoMediaReady = true;
        VideoPlayer.Visibility = Visibility.Visible;
        if (_viewModel is not null)
        {
            _viewModel.ResetVideoPlaybackIssue();
            if (!_viewModel.IsMediaPlaying)
                VideoPlayer.Position = TimeSpan.FromSeconds(_viewModel.VideoPosition);
        }
        if (_viewModel?.IsMediaPlaying == true) VideoPlayer.Play();
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        if (vm.LoopMedia)
        {
            SeekVideo(vm.VideoStart);
            if (vm.IsMediaPlaying) VideoPlayer.Play();
        }
        else
        {
            PauseVideo();
        }
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _videoMediaReady = false;
        VideoPlayer.Visibility = Visibility.Collapsed;
        _playbackTimer.Stop();
        if (_viewModel is not null)
        {
            _viewModel.IsMediaPlaying = false;
            _viewModel.ReportVideoPlaybackIssue(
                "This video can't play in the preview. Set cut points on the timeline and Export — FFmpeg will still cut it.");
        }
    }

    private void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _audioMediaReady = true;
        var vm = _viewModel;
        if (vm is null) return;

        if (vm.IsAudioMerge && _mergeSeekPending)
        {
            _mergeSeekPending = false;
            AudioPlayer.Position = TimeSpan.FromSeconds(_mergeTrackOffset);
            if (_mergeShouldPlay) AudioPlayer.Play();
            else AudioPlayer.Pause();
            return;
        }

        if (vm.IsAudioTrim)
        {
            AudioPlayer.Position = TimeSpan.FromSeconds(vm.AudioPosition);
            if (vm.IsMediaPlaying) AudioPlayer.Play();
        }
    }

    private void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        if (vm.IsAudioMerge)
        {
            var next = _mergeTrackIndex + 1;
            if (next < vm.AudioTracks.Count)
            {
                SeekAudioMerge(vm.AudioTracks[next].StartOffset, play: true);
            }
            else if (vm.LoopMedia && vm.AudioTracks.Count > 0)
            {
                SeekAudioMerge(0, play: true);
            }
            else
            {
                PauseAudio(atEnd: true);
            }
        }
        else if (vm.LoopMedia)
        {
            SeekAudioTrim(vm.AudioStart);
            AudioPlayer.Play();
            vm.IsMediaPlaying = true;
            _playbackTimer.Start();
        }
        else
        {
            PauseAudio(atEnd: true);
        }
    }

    private void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _audioMediaReady = false;
        _mergeSeekPending = false;
        _playbackTimer.Stop();
        if (_viewModel is not null) _viewModel.IsMediaPlaying = false;
    }

    // ───────────────────────── timeline events ─────────────────────────

    private void VideoTimeline_SeekRequested(object sender, SeekRequestedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        _suppressSeek = true;
        vm.VideoPosition = e.Seconds;
        _suppressSeek = false;
        SeekVideo(e.Seconds);
    }

    private void AudioTimeline_SeekRequested(object sender, SeekRequestedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        _suppressSeek = true;
        vm.AudioPosition = e.Seconds;
        _suppressSeek = false;
        SeekAudioTrim(e.Seconds);
    }

    private void AudioMergeTimeline_SeekRequested(object sender, SeekRequestedEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        _suppressSeek = true;
        vm.AudioMergePosition = e.Seconds;
        _suppressSeek = false;
        SeekAudioMerge(e.Seconds, vm.IsMediaPlaying);
    }

    private void Timeline_RangeChanged(object sender, EventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        if (ReferenceEquals(sender, VideoTimeline))
        {
            if (vm.VideoPosition < vm.VideoStart || vm.VideoPosition > vm.VideoEnd)
            {
                _suppressSeek = true;
                vm.VideoPosition = vm.VideoStart;
                _suppressSeek = false;
                SeekVideo(vm.VideoStart);
            }
        }
        else if (ReferenceEquals(sender, AudioTimeline))
        {
            if (vm.AudioPosition < vm.AudioStart || vm.AudioPosition > vm.AudioEnd)
            {
                _suppressSeek = true;
                vm.AudioPosition = vm.AudioStart;
                _suppressSeek = false;
                SeekAudioTrim(vm.AudioStart);
            }
        }
    }

    // ───────────────────────── preview / keyboard ─────────────────────────

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Focus();

    private void PhotoCropOverlay_CropChangedByUser(object? sender, EventArgs e) =>
        _viewModel?.SetCropAspectFree();

    private void EditorView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = _viewModel;
        if (vm is null) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or Slider or ButtonBase
            or ListBox or ListBoxItem or ToggleButton)
            return;

        var delta = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5.0 : 1.0;
        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(vm, -delta);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(vm, delta);
                e.Handled = true;
                break;
            case Key.Home:
                SeekRelative(vm, double.MinValue);
                e.Handled = true;
                break;
            case Key.End:
                SeekRelative(vm, double.MaxValue);
                e.Handled = true;
                break;
            case Key.I:
                if (vm.IsVideoTool) vm.SetVideoStartCommand.Execute(null);
                else if (vm.IsAudioTool && vm.IsAudioTrim) vm.SetAudioStartCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.O:
                if (vm.IsVideoTool) vm.SetVideoEndCommand.Execute(null);
                else if (vm.IsAudioTool && vm.IsAudioTrim) vm.SetAudioEndCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void SeekRelative(EditorViewModel vm, double delta)
    {
        if (vm.IsVideoTool)
        {
            var target = delta switch
            {
                double.MinValue => 0,
                double.MaxValue => vm.VideoDuration,
                _ => vm.VideoPosition + delta,
            };
            _suppressSeek = true;
            vm.VideoPosition = target;
            _suppressSeek = false;
            SeekVideo(vm.VideoPosition);
            return;
        }

        if (!vm.IsAudioTool) return;
        if (vm.IsAudioMerge)
        {
            var target = delta switch
            {
                double.MinValue => 0,
                double.MaxValue => vm.AudioMergeDuration,
                _ => vm.AudioMergePosition + delta,
            };
            _suppressSeek = true;
            vm.AudioMergePosition = target;
            _suppressSeek = false;
            SeekAudioMerge(vm.AudioMergePosition, vm.IsMediaPlaying);
            return;
        }

        var trimTarget = delta switch
        {
            double.MinValue => 0,
            double.MaxValue => vm.AudioDuration,
            _ => vm.AudioPosition + delta,
        };
        _suppressSeek = true;
        vm.AudioPosition = trimTarget;
        _suppressSeek = false;
        SeekAudioTrim(vm.AudioPosition);
    }
}
