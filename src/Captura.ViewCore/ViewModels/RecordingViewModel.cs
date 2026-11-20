using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Captura.Audio;
using Captura.FFmpeg;
using Captura.Loc;
using Captura.Models;
using Captura.Video;
using Captura.Webcam;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Captura.ViewModels
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class RecordingViewModel : ViewModelBase
    {
        readonly RecordingModel _recordingModel;
        readonly TimerModel _timerModel;
        readonly VideoSourcesViewModel _videoSourcesViewModel;
        public VideoSourcesViewModel VideoSources => _videoSourcesViewModel;
        readonly VideoWritersViewModel _videoWritersViewModel;
        readonly ISystemTray _systemTray;
        readonly IMainWindow _mainWindow;
        readonly IAudioPlayer _audioPlayer;
        readonly IRecentList _recentList;
        readonly IMessageProvider _messageProvider;
        readonly AudioSourceViewModel _audioSourceViewModel;
        readonly IFFmpegViewsProvider _ffmpegViewsProvider;

        readonly SyncContextManager _syncContext = new SyncContextManager();

        public ICommand RecordCommand { get; }
        public ICommand PauseCommand { get; }

        bool _isDiscarding = false;
        public RecordingModel RecordingModel => _recordingModel;

        // ====== 新增：全局事件，用于通知 UI 层（主项目）刷新状态栏和边框 ======
        public event Action<RecorderState> OnRecorderStateChanged;
        // ====================================================================
        public event Action<bool> OnTimerFinished;
        bool _isTimerTriggered = false;
        public RecordingViewModel(RecordingModel RecordingModel,
            Settings Settings,
            TimerModel TimerModel,
            WebcamModel WebcamModel,
            VideoSourcesViewModel VideoSourcesViewModel,
            VideoWritersViewModel VideoWritersViewModel,
            ISystemTray SystemTray,
            IMainWindow MainWindow,
            ILocalizationProvider Loc,
            IAudioPlayer AudioPlayer,
            IRecentList RecentList,
            IMessageProvider MessageProvider,
            AudioSourceViewModel AudioSourceViewModel,
            IFFmpegViewsProvider FFmpegViewsProvider) : base(Settings, Loc)
        {
            _recordingModel = RecordingModel;
            _timerModel = TimerModel;
            _videoSourcesViewModel = VideoSourcesViewModel;
            _videoWritersViewModel = VideoWritersViewModel;
            _systemTray = SystemTray;
            _mainWindow = MainWindow;
            _audioPlayer = AudioPlayer;
            _recentList = RecentList;
            _messageProvider = MessageProvider;
            _audioSourceViewModel = AudioSourceViewModel;
            _ffmpegViewsProvider = FFmpegViewsProvider;

            // ... 这里的 RecordCommand 和 PauseCommand 保持不变 ...
            var hasAudio = new[]
            {
                Settings.Audio.ObserveProperty(M => M.RecordMicrophone),
                Settings.Audio.ObserveProperty(M => M.RecordSpeaker)
            }
            .CombineLatest(M => M[0] || M[1]);

            RecordCommand = new[]
                {
                    hasAudio,
                    VideoSourcesViewModel.ObserveProperty(M => M.SelectedVideoSourceKind).Select(M => M is NoVideoSourceProvider),
                    VideoSourcesViewModel.ObserveProperty(M => M.SelectedVideoSourceKind).Select(M => M is WebcamSourceProvider),
                    WebcamModel.ObserveProperty(M => M.SelectedCam).Select(M => M is NoWebcamItem),
                    Settings.Video.ObserveProperty(M => M.RecorderMode).Select(M => M == RecorderMode.Steps),
                    VideoSourcesViewModel.ObserveProperty(M => M.SelectedVideoSourceKind).Select(M => M.SupportsStepsMode),
                }
                .CombineLatest(M =>
                {
                    var audioEnabled = M[0];
                    var audioOnlyMode = M[1];
                    var webcamMode = M[2];
                    var noWebcam = M[3];
                    var stepsMode = M[4];
                    var supportsStepsMode = M[5];

                    if (stepsMode) return supportsStepsMode;
                    if (audioOnlyMode) return audioEnabled;
                    if (webcamMode) return !noWebcam;
                    return true;
                })
                .ToReactiveCommand()
                .WithSubscribe(OnRecordExecute);

            PauseCommand = new[]
                {
                    TimerModel.ObserveProperty(M => M.Waiting),
                    RecordingModel.ObserveProperty(M => M.RecorderState).Select(M => M != Models.RecorderState.NotRecording)
                }
                .CombineLatest(M => !M[0] && M[1])
                .ToReactiveCommand()
                .WithSubscribe(() =>
                {
                    _audioPlayer.Play(SoundKind.Pause);
                    RecordingModel.OnPauseExecute();
                });

            RecorderState = RecordingModel
                .ObserveProperty(M => M.RecorderState)
                .ToReadOnlyReactivePropertySlim();

            // ===== 修改点：使用事件向外通知，避免 UI 依赖 =====
            RecorderState.Subscribe(state =>
            {
                // 如果是录制结束（包括手动停止、定时到点、丢弃）
                if (state == Models.RecorderState.NotRecording)
                {
                    // 1. 重置底层时长
                    Settings.Duration = 0;

                    // 2. 同步重置时分秒，通知 UI 刷新显示
                    Settings.SyncHmsFromDuration();

                    
 
                }
                OnRecorderStateChanged?.Invoke(state);
            });
            // ====================================================

            //TimerModel.DurationElapsed += () =>
            //{
            //    _syncContext.Run(async () => await StopRecording(), true);
            //};
            TimerModel.DurationElapsed += () =>
            {
                _isTimerTriggered = true; // 标记是定时触发的停止
                _syncContext.Run(async () =>
                {
                    // 先记录下用户是否勾选了自动关机
                    var shouldShutdown = Settings.AutoShutdownAfterTimer;

                    // 1. 安全停止录制并保存文件
                    await StopRecording();

                    // 2. 触发事件，把是否关机的决定权交给 UI 层
                    OnTimerFinished?.Invoke(shouldShutdown);
                }, true);
            };
        }

        // ... 下面的 OnRecordExecute, StopRecording, StartRecording, DiscardRecording 等代码与你原来的完全一致 ...
        async void OnRecordExecute()
        {
            if (RecorderState.Value == Models.RecorderState.NotRecording)
            {
                StartRecording();
            }
            else await StopRecording();
        }

        readonly object _stopRecTaskLock = new object();
        readonly List<Task> _stopRecTasks = new List<Task>();

        int RunningStopRecordingCount
        {
            get
            {
                lock (_stopRecTaskLock)
                {
                    return _stopRecTasks.Count(M => !M.IsCompleted);
                }
            }
        }

        async Task StopRecording()
        {
            _audioPlayer.Play(SoundKind.Stop);

            FileRecentItem savingRecentItem = null;
            FileSaveNotification notification = null;

            var fileName = _recordingModel.CurrentFileName;
            var isVideo = _recordingModel.IsVideo;

            IVideoConverter postWriter = null;

            if (!_isDiscarding && !_timerModel.Waiting && !string.IsNullOrWhiteSpace(_videoWritersViewModel.SelectedVideoWriter.Extension))
            {
                savingRecentItem = new FileRecentItem(fileName, isVideo ? RecentFileType.Video : RecentFileType.Audio, true);
                _recentList.Add(savingRecentItem);

                notification = new FileSaveNotification(savingRecentItem);
                notification.OnDelete += () => savingRecentItem.RemoveCommand.ExecuteIfCan();
                _systemTray.ShowNotification(notification);

                if (isVideo && Settings.Video.PostConvert)
                    postWriter = _videoWritersViewModel.SelectedPostWriter;
            }

            var task = _recordingModel.StopRecording();

            lock (_stopRecTaskLock)
            {
                _stopRecTasks.Add(task);
            }

            var wasWaiting = _timerModel.Waiting;
            _timerModel.Waiting = false;

            try
            {
                await task;

                if (_isDiscarding)
                {
                    _isDiscarding = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                        {
                            File.Delete(fileName);
                        }
                    }
                    catch { }

                    lock (_stopRecTaskLock)
                    {
                        _stopRecTasks.Remove(task);
                    }

                    return;
                }

                if (postWriter != null)
                {
                    notification.Converting();

                    var progress = new Progress<int>();
                    progress.ProgressChanged += (S, E) => notification.Progress = E;

                    var outFileName = Path.Combine(
                        Path.GetDirectoryName(fileName),
                        $"{Path.GetFileNameWithoutExtension(fileName)}.converted{postWriter.Extension}");

                    try
                    {
                        await postWriter.StartAsync(new VideoConverterArgs
                        {
                            AudioQuality = Settings.Audio.Quality,
                            VideoQuality = Settings.Video.Quality,
                            InputFile = fileName,
                            FileName = outFileName
                        }, progress);

                        File.Delete(fileName);

                        var targetFileName = Path.Combine(
                            Path.GetDirectoryName(fileName),
                            $"{Path.GetFileNameWithoutExtension(fileName)}{postWriter.Extension}");

                        File.Move(outFileName, targetFileName);

                        savingRecentItem.Converted(targetFileName);
                        notification.Converted(targetFileName);
                    }
                    catch (FFmpegNotFoundException e)
                    {
                        try { _ffmpegViewsProvider.ShowUnavailable(); }
                        catch { _messageProvider.ShowException(e, e.Message); }
                    }
                    catch (Exception e)
                    {
                        _messageProvider.ShowException(e, "Conversion Failed");
                    }
                }

                lock (_stopRecTaskLock)
                {
                    _stopRecTasks.Remove(task);
                }
            }
            catch (Exception e)
            {
                _messageProvider.ShowException(e, "Error occurred when stopping recording.\nThis might sometimes occur if you stop recording just as soon as you start it.");
                if (_isDiscarding) _isDiscarding = false;
                return;
            }

            if (wasWaiting)
            {
                try { File.Delete(fileName); } catch { }
            }

            if (savingRecentItem != null)
            {
                AfterSave(savingRecentItem, notification);
            }
            // ===== 新增：如果是手动结束录制，取消自动关机 =====
            if (!_isTimerTriggered && Settings.AutoShutdownAfterTimer)
            {
                Settings.AutoShutdownAfterTimer = false; // 取消勾选，WPF 界面会自动同步更新
            }

            _isTimerTriggered = false; // 恢复默认状态，为下次录制做准备
        }

        void AfterSave(FileRecentItem SavingRecentItem, FileSaveNotification Notification)
        {
            SavingRecentItem.Saved();
            if (Settings.CopyOutPathToClipboard)
                SavingRecentItem.FileName.WriteToClipboard();
            Notification.Saved();
        }

        void StartRecording()
        {
            _systemTray.HideNotification();

            if (_recordingModel.StartRecording(new RecordingModelParams
            {
                VideoSourceKind = _videoSourcesViewModel.SelectedVideoSourceKind,
                VideoWriter = Settings.Video.RecorderMode == RecorderMode.Steps
                    ? _videoWritersViewModel.SelectedStepsWriter
                    : _videoWritersViewModel.SelectedVideoWriter,
                Microphone = Settings.Audio.RecordMicrophone ? _audioSourceViewModel.SelectedMicrophone : null,
                Speaker = Settings.Audio.RecordSpeaker ? _audioSourceViewModel.SelectedSpeaker : null
            }))
            {
                if (Settings.Tray.MinToTrayOnCaptureStart)
                    _mainWindow.IsVisible = false;

                _audioPlayer.Play(SoundKind.Start);
            }
            else _audioPlayer.Play(SoundKind.Error);
        }

        public IReadOnlyReactiveProperty<RecorderState> RecorderState { get; }

        public bool CanExit()
        {
            if (RecorderState.Value == Models.RecorderState.Recording)
            {
                if (!_messageProvider.ShowYesNo("当前正在录制，确认要退出程序和丢弃当前录制的视频吗？", "提醒"))
                    return false;
            }
            else if (RunningStopRecordingCount > 0)
            {
                if (!_messageProvider.ShowYesNo("Some Recordings have not finished writing to disk. Are you sure you want to exit?", "提醒"))
                    return false;
            }
            return true;
        }

        public async void DiscardRecording()
        {
            if (RecorderState.Value == Models.RecorderState.Recording || RecorderState.Value == Models.RecorderState.Paused)
            {
                _isDiscarding = true;
                await StopRecording();
            }
        }
    }
}