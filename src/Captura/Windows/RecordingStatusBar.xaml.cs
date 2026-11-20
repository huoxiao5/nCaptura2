using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Captura.Models;
using Captura.ViewModels;

namespace Captura
{
    public partial class RecordingStatusBar : Window
    {
        [DllImport("user32.dll")]
        static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
        const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        readonly RecordingViewModel _recordingViewModel;
        readonly RecordingModel _recordingModel;
        readonly TimerModel _timerModel;
        readonly Settings _settings;
        readonly DispatcherTimer _updateTimer;

        private readonly RegionSelector _parentSelector;
        private bool _isDragged = false;

        private DateTime? _fixedEndTime = null;
        private RecorderState _lastState = RecorderState.NotRecording;

        // 新增：记录暂停时的剩余时间，用于冻结时间显示
        private TimeSpan _pausedRemaining = TimeSpan.Zero;

        public RecordingStatusBar(RegionSelector parent, RecordingViewModel vm, RecordingModel model)
        {
            InitializeComponent();

            _parentSelector = parent;
            _recordingViewModel = vm;
            _recordingModel = model;
            _timerModel = ServiceProvider.Get<TimerModel>();
            _settings = ServiceProvider.Get<Settings>();

            SourceInitialized += (s, e) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                }
                catch { }
            };

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            UpdateIconState(_recordingViewModel.RecorderState.Value);
            _recordingViewModel.RecorderState.Subscribe(UpdateIconState);
        }

        void UpdateTimer_Tick(object sender, EventArgs e)
        {
            var currentState = _recordingViewModel.RecorderState.Value;

            // 1. 状态切换时的拦截逻辑
            if (currentState != _lastState)
            {
                if (currentState == RecorderState.Recording)
                {
                    // 从暂停恢复或全新开始，重新计算固定的结束时间
                    if (_settings.Duration > 0)
                    {
                        var currentTime = _timerModel.TimeSpan;
                        var remainingSeconds = _settings.Duration - (int)currentTime.TotalSeconds;
                        if (remainingSeconds > 0)
                        {
                            _fixedEndTime = DateTime.Now.AddSeconds(remainingSeconds);
                        }
                    }
                }
                else if (currentState == RecorderState.Paused)
                {
                    // 刚进入暂停状态，记录当前的剩余时间
                    if (_fixedEndTime.HasValue)
                    {
                        _pausedRemaining = _fixedEndTime.Value - DateTime.Now;
                    }
                }
                else if (currentState == RecorderState.NotRecording)
                {
                    _fixedEndTime = null;
                }
                _lastState = currentState;
            }

            // 2. 计时与文本刷新
            if (currentState == RecorderState.Recording || currentState == RecorderState.Paused)
            {
                var currentTime = _timerModel.TimeSpan;
                DurationText.Text = FormatDuration(currentTime);

                if (_settings.Duration > 0 && _fixedEndTime.HasValue)
                {
                    // 如果是录制中，实时计算；如果是暂停，使用冻结的剩余时间
                    TimeSpan remaining = currentState == RecorderState.Recording
                        ? _fixedEndTime.Value - DateTime.Now
                        : _pausedRemaining;

                    if (remaining.TotalSeconds > 0)
                    {
                        TimerInfoText.Visibility = Visibility.Visible;

                        // 截断毫秒防抖动
                        var displayRemaining = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));

                        string remainStr = FormatRemainingTime(displayRemaining);
                        string endStr = FormatEndTime(_fixedEndTime.Value);

                        // 每次 Tick 都重新拼接文本，这样你在弹窗里切换开关时，能立刻看到状态栏更新
                        TimerInfoText.Inlines.Clear();
                        TimerInfoText.Inlines.Add(new System.Windows.Documents.Run($"定时：{remainStr}后 在 {endStr}")
                        {
                            Foreground = System.Windows.Media.Brushes.Black
                        });

                        if (_settings.AutoShutdownAfterTimer)
                        {
                            TimerInfoText.Inlines.Add(new System.Windows.Documents.Run("后关机")
                            {
                                Foreground = System.Windows.Media.Brushes.Red,
                                FontWeight = FontWeights.Bold
                            });
                        }
                    }
                    else
                    {
                        TimerInfoText.Text = "定时即将结束";
                    }
                }
            }

            var path = _recordingModel.CurrentFileName;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var info = new FileInfo(path);
                    info.Refresh();
                    FileSizeText.Text = FormatSize(info.Length);
                }
                catch { }
            }

            // 位置更新
            if (_parentSelector != null && _parentSelector.IsVisible && !_isDragged)
            {
                var regionPos = _parentSelector.RegionGrid.PointToScreen(new Point(0, 0));
                double regionHeight = _parentSelector.RegionGrid.ActualHeight;
                double statusHeight = this.ActualHeight;

                double targetLeft = regionPos.X;
                double targetTop = regionPos.Y + regionHeight;

                if (targetTop + statusHeight > SystemParameters.PrimaryScreenHeight)
                {
                    targetTop = regionPos.Y - statusHeight;
                    if (targetTop < 0) targetTop = regionPos.Y;
                }

                this.Left = targetLeft;
                this.Top = targetTop;
            }
        }

        static string FormatRemainingTime(TimeSpan remaining)
        {
            int totalSeconds = (int)remaining.TotalSeconds;
            int hours = (int)remaining.TotalHours;
            int minutes = remaining.Minutes;
            int seconds = remaining.Seconds;

            if (totalSeconds < 60) return $"{seconds}秒";
            if (totalSeconds < 3600) return $"{minutes}分钟{seconds}秒";
            return $"{hours}小时{minutes}分钟{seconds}秒";
        }

        static string FormatEndTime(DateTime endTime)
        {
            string timeStr = $"{endTime:HH时mm分ss秒}结束";

            if (endTime.Date == DateTime.Today) return $"今天 {timeStr}";
            if (endTime.Date == DateTime.Today.AddDays(1)) return $"明天 {timeStr}";

            return $"{endTime:MM月dd日} {timeStr}"; // 修复了冒号
        }

        static string FormatDuration(TimeSpan time)
        {
            int totalHours = (int)time.TotalHours;
            return $"{totalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        void UpdateIconState(RecorderState state)
        {
            var pauseIcon = (FrameworkElement)PauseButton.Template.FindName("PauseIcon", PauseButton);
            var playIcon = (FrameworkElement)PauseButton.Template.FindName("PlayIcon", PauseButton);

            if (state == RecorderState.Recording)
            {
                PauseButton.ToolTip = "暂停";
                if (pauseIcon != null) pauseIcon.Visibility = Visibility.Visible;
                if (playIcon != null) playIcon.Visibility = Visibility.Collapsed;
            }
            else if (state == RecorderState.Paused)
            {
                PauseButton.ToolTip = "继续";
                if (pauseIcon != null) pauseIcon.Visibility = Visibility.Collapsed;
                if (playIcon != null) playIcon.Visibility = Visibility.Visible;
            }
            else if (state == RecorderState.NotRecording)
            {
                DurationText.Text = "00:00:00";
                FileSizeText.Text = "0 KB";
                TimerInfoText.Visibility = Visibility.Collapsed;
                PauseButton.ToolTip = "暂停";
                if (pauseIcon != null) pauseIcon.Visibility = Visibility.Visible;
                if (playIcon != null) playIcon.Visibility = Visibility.Collapsed;
            }
        }

        void PauseButton_Click(object Sender, RoutedEventArgs e)
        {
            _recordingViewModel.PauseCommand.ExecuteIfCan();
        }

        void DragArea_MouseDown(object Sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragged = true;
                _updateTimer.Stop();
                DragMove();
                _updateTimer.Start();
            }
        }

        static string FormatSize(long Bytes)
        {
            if (Bytes >= 1024L * 1024 * 1024) return $"{Bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (Bytes >= 1024L * 1024) return $"{Bytes / (1024.0 * 1024):F2} MB";
            return $"{Bytes / 1024.0:F1} KB";
        }

        public void Stop()
        {
            _updateTimer.Stop();
            this.Close();
        }
    }
}