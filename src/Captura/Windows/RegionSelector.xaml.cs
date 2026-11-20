using Captura.Models;
using Captura.Video;
using Captura.ViewModels;
using System;
using System.Reactive.Disposables;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace Captura
{
    public partial class RegionSelector
    {
        readonly RegionSelectorViewModel _viewModel;
        readonly RecordingViewModel _recordingViewModel;
        readonly RecordingModel _recordingModel;
        readonly CompositeDisposable _disposables = new CompositeDisposable();

        readonly DispatcherTimer _blinkTimer;
        bool _isYellow = true;

        // 独立的状态栏窗口
        private RecordingStatusBar _statusBarWindow;

        public RegionSelector(RegionSelectorViewModel ViewModel)
        {
            _viewModel = ViewModel;
            InitializeComponent();

            _recordingViewModel = ServiceProvider.Get<RecordingViewModel>();
            _recordingModel = _recordingViewModel.RecordingModel;

            Closing += (S, E) => E.Cancel = true;

            ViewModel.BrushColor.Subscribe(M => InkCanvas.DefaultDrawingAttributes.Color = M);
            ViewModel.BrushSize.Subscribe(M => InkCanvas.DefaultDrawingAttributes.Height = InkCanvas.DefaultDrawingAttributes.Width = M);
            ViewModel.SelectedTool.Subscribe(OnToolChange);
            ViewModel.ClearAllDrawingsCommand.Subscribe(() => InkCanvas.Strokes.Clear());
            ViewModel.ExitDrawingModeCommand.Subscribe(() => ViewModel.SelectedTool.Value = InkCanvasEditingMode.None);

            InkCanvas.DefaultDrawingAttributes.FitToCurve = true;

            InkCanvas.PreviewMouseRightButtonDown += (s, e) => { ViewModel.SelectedTool.Value = InkCanvasEditingMode.None; e.Handled = true; };
            this.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && ViewModel.SelectedTool.Value != InkCanvasEditingMode.None) { ViewModel.SelectedTool.Value = InkCanvasEditingMode.None; e.Handled = true; } };

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _blinkTimer.Tick += BlinkTimer_Tick;

            _disposables.Add(_recordingViewModel.RecorderState.Subscribe(OnRecorderStateChanged));

            Closed += (s, e) =>
            {
                _blinkTimer.Stop();
                _disposables.Dispose();
                _statusBarWindow?.Stop();
            };
        }

        void OnRecorderStateChanged(RecorderState State)
        {
            if (State == RecorderState.Recording)
            {
                // === 核心修复：如果当前不是区域录制，RegionSelector 绝不抢着弹状态栏 ===
                var currentSource = ServiceProvider.Get<VideoSourcesViewModel>().SelectedVideoSourceKind;
                if (!(currentSource is RegionSourceProvider))
                {
                    _blinkTimer.Stop();
                    RegionBorder.BorderBrush = Brushes.Transparent;
                    return; // 直接退出，把 UI 交给 RecordingUIManager
                }

                if (!_blinkTimer.IsEnabled)
                {
                    StripedBorderControl.Visibility = Visibility.Hidden;

                    _isYellow = true;
                    RegionBorder.BorderThickness = new Thickness(3);
                    RegionBorder.BorderBrush = Brushes.Yellow;
                    _blinkTimer.Start();
                }

                if (_statusBarWindow == null)
                {
                    _statusBarWindow = new RecordingStatusBar(this, _recordingViewModel, _recordingModel);
                    _statusBarWindow.Show();
                }
            }
            else if (State == RecorderState.Paused)
            {
                // 修复问题2：暂停时停止闪烁，但显示常亮红色边框，以示区域
                _blinkTimer.Stop();
                RegionBorder.BorderThickness = new Thickness(3);
                RegionBorder.BorderBrush = Brushes.Red;
            }
            else if (State == RecorderState.NotRecording)
            {
                _blinkTimer.Stop();
                StripedBorderControl.Visibility = Visibility.Visible;
                RegionBorder.BorderBrush = Brushes.Transparent;
                RegionBorder.BorderThickness = new Thickness(2); // 恢复默认粗细

                if (_statusBarWindow != null)
                {
                    _statusBarWindow.Stop();
                    _statusBarWindow = null;
                }
            }
        }

        void BlinkTimer_Tick(object sender, EventArgs e)
        {
            _isYellow = !_isYellow;
            RegionBorder.BorderBrush = _isYellow ? Brushes.Yellow : Brushes.Red;
        }

        void OnToolChange(InkCanvasEditingMode Tool)
        {
            InkCanvas.EditingMode = Tool;
            if (Tool == InkCanvasEditingMode.Ink) { InkCanvas.UseCustomCursor = true; InkCanvas.Cursor = Cursors.Pen; }
            else InkCanvas.UseCustomCursor = false;
            InkCanvas.Background = new SolidColorBrush(Tool == InkCanvasEditingMode.None ? Colors.Transparent : Color.FromArgb(1, 0, 0, 0));
        }

        protected override void OnStateChanged(EventArgs E)
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            base.OnStateChanged(E);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo SizeInfo)
        {
            InkCanvas.Strokes.Clear();
            base.OnRenderSizeChanged(SizeInfo);
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        // ================= 鼠标拖动（包含边界实时限制） =================
        void UIElement_OnPreviewMouseLeftButtonDown(object Sender, MouseButtonEventArgs E)
        {
            if (E.LeftButton == MouseButtonState.Pressed)
            {
                Point originPos = E.GetPosition(this);

                MouseEventHandler mouseMove = null;
                MouseButtonEventHandler mouseUp = null;

                mouseMove = (s, ev) =>
                {
                    if (ev.LeftButton == MouseButtonState.Released) return;

                    Point currentPos = ev.GetPosition(this);
                    double deltaX = currentPos.X - originPos.X;
                    double deltaY = currentPos.Y - originPos.Y;

                    double newLeft = _viewModel.LeftDip + deltaX;
                    double newTop = _viewModel.TopDip + deltaY;

                    // 包含任务栏的全屏尺寸
                    double maxRight = SystemParameters.PrimaryScreenWidth;
                    double maxBottom = SystemParameters.PrimaryScreenHeight;

                    double regionWidth = RegionGrid.ActualWidth;
                    double regionHeight = RegionGrid.ActualHeight;

                    if (newLeft < 0) newLeft = 0;
                    if (newTop < 0) newTop = 0;
                    if (newLeft + regionWidth > maxRight) newLeft = maxRight - regionWidth;
                    if (newTop + regionHeight > maxBottom) newTop = maxBottom - regionHeight;

                    _viewModel.LeftDip = newLeft;
                    _viewModel.TopDip = newTop;
                };

                mouseUp = (s, ev) =>
                {
                    Mouse.Capture(null);
                    this.MouseMove -= mouseMove;
                    this.MouseLeftButtonUp -= mouseUp;
                };

                Mouse.Capture(this);
                this.MouseMove += mouseMove;
                this.MouseLeftButtonUp += mouseUp;
            }
        }

        // ================= 拖拽调整大小（包含边界限制） =================
        void Thumb_OnDragDelta(object Sender, DragDeltaEventArgs E)
        {
            if (!(Sender is FrameworkElement element)) return;

            double maxWidth = SystemParameters.PrimaryScreenWidth;
            double maxHeight = SystemParameters.PrimaryScreenHeight;

            double horizontalChange = E.HorizontalChange;
            double verticalChange = E.VerticalChange;

            switch (element.Tag)
            {
                case "Bottom":
                    if (_viewModel.Top + _viewModel.Height + verticalChange > maxHeight)
                        verticalChange = maxHeight - _viewModel.Top - _viewModel.Height;
                    _viewModel.Height += (int)verticalChange;
                    break;
                case "Left":
                    if (_viewModel.Left + horizontalChange < 0) horizontalChange = -_viewModel.Left;
                    _viewModel.Left += (int)horizontalChange;
                    _viewModel.Width -= (int)horizontalChange;
                    break;
                case "Right":
                    if (_viewModel.Left + _viewModel.Width + horizontalChange > maxWidth)
                        horizontalChange = maxWidth - _viewModel.Left - _viewModel.Width;
                    _viewModel.Width += (int)horizontalChange;
                    break;
                case "TopLeft":
                    if (_viewModel.Left + horizontalChange < 0) horizontalChange = -_viewModel.Left;
                    if (_viewModel.Top + verticalChange < 0) verticalChange = -_viewModel.Top;
                    _viewModel.Left += (int)horizontalChange;
                    _viewModel.Width -= (int)horizontalChange;
                    _viewModel.Top += (int)verticalChange;
                    _viewModel.Height -= (int)verticalChange;
                    break;
                case "TopRight":
                    if (_viewModel.Top + verticalChange < 0) verticalChange = -_viewModel.Top;
                    if (_viewModel.Left + _viewModel.Width + horizontalChange > maxWidth)
                        horizontalChange = maxWidth - _viewModel.Left - _viewModel.Width;
                    _viewModel.Width += (int)horizontalChange;
                    _viewModel.Top += (int)verticalChange;
                    _viewModel.Height -= (int)verticalChange;
                    break;
                case "BottomLeft":
                    if (_viewModel.Left + horizontalChange < 0) horizontalChange = -_viewModel.Left;
                    if (_viewModel.Top + _viewModel.Height + verticalChange > maxHeight)
                        verticalChange = maxHeight - _viewModel.Top - _viewModel.Height;
                    _viewModel.Left += (int)horizontalChange;
                    _viewModel.Width -= (int)horizontalChange;
                    _viewModel.Height += (int)verticalChange;
                    break;
                case "BottomRight":
                    if (_viewModel.Left + _viewModel.Width + horizontalChange > maxWidth)
                        horizontalChange = maxWidth - _viewModel.Left - _viewModel.Width;
                    if (_viewModel.Top + _viewModel.Height + verticalChange > maxHeight)
                        verticalChange = maxHeight - _viewModel.Top - _viewModel.Height;
                    _viewModel.Width += (int)horizontalChange;
                    _viewModel.Height += (int)verticalChange;
                    break;
            }
        }
    }
}