using Captura.Models;
using Captura.Video;
using Captura.ViewModels;
using Reactive.Bindings.Extensions;
using System;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Captura
{
    public static class RecordingUIManager
    {
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private static WinEventDelegate _winEventDelegate;
        private static IntPtr _winEventHook = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        private static RecordingStatusBar _statusBar;
        private static RecordingBorderWindow _borderWindow;
        private static TimeOverlayWindow _timeOverlayWindow;
        private static RecordingViewModel _vm;
        private static IntPtr _trackedHwnd = IntPtr.Zero;

        private static DispatcherTimer _trackTimer;
        private static bool _isInitialized = false;

        public static void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            _vm = ServiceProvider.Get<RecordingViewModel>();

            // ===== 新增：监听定时结束事件 =====
            // 替换掉之前的监听代码：
            _vm.OnTimerFinished += (shouldShutdown) =>
            {
                // 回到 UI 线程操作
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (shouldShutdown)
                    {
                        var dialog = new ShutdownDialog();
                        dialog.ShowDialog(); // 这一步会阻塞，直到倒计时结束或用户点击取消
                    }

                    // 核心：无论用户是取消了，还是真的关机了（如果没关机成功），
                    // 弹窗流程走完后，把标记清空，UI 上的勾选会自动消失
                    var settings = ServiceProvider.Get<Settings>();
                    settings.AutoShutdownAfterTimer = false;
                });
            };


            _winEventDelegate = (hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime) =>
            {
                if (hwnd != _trackedHwnd) return;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_borderWindow != null)
                    {
                        if (eventType == EVENT_SYSTEM_MOVESIZESTART)
                        {
                            _borderWindow.SetDragState(true);
                        }
                        else if (eventType == EVENT_SYSTEM_MOVESIZEEND)
                        {
                            UpdateTrackingPosition();
                            _borderWindow.SetDragState(false);
                        }
                    }
                });
            };

            _winEventHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            _trackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _trackTimer.Tick += (s, e) => UpdateTrackingPosition();

            _vm.RecorderState.Subscribe(state =>
            {
                var sourceKind = _vm.VideoSources.SelectedVideoSourceKind;
                bool isRegion = sourceKind is RegionSourceProvider;

                if (state == RecorderState.Recording)
                {
                    if (!isRegion)
                    {
                        if (_statusBar == null)
                        {
                            _statusBar = new RecordingStatusBar(null, _vm, _vm.RecordingModel);
                            _statusBar.Show();
                        }

                        if (_borderWindow == null)
                        {
                            if (sourceKind is WindowSourceProvider windowProvider && windowProvider.Source is WindowItem windowItem)
                            {
                                _borderWindow = new RecordingBorderWindow();
                                _trackedHwnd = windowItem.Window.Handle;
                                _borderWindow.Show();
                                _trackTimer.Start();
                            }
                        }

                        // ===== 核心修复：确保开始录制时，立即更新位置（带 margin 防录）并开始闪烁 =====
                        if (_borderWindow != null)
                        {
                            UpdateTrackingPosition(); // 立马更新坐标，确保加上外扩 8 像素
                            _borderWindow.StartBlink(); // 强制开启闪烁
                        }
                    }

                    if (_timeOverlayWindow == null)
                    {
                        _timeOverlayWindow = new TimeOverlayWindow();
                        _timeOverlayWindow.Show();

                        Rect recordingBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

                        if (sourceKind is WindowSourceProvider winProvider && winProvider.Source is WindowItem winItem)
                        {
                            if (GetWindowRect(winItem.Window.Handle, out RECT rect))
                                recordingBounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        }
                        else if (isRegion)
                        {
                            var regionSelectorVm = ServiceProvider.Get<RegionSelectorViewModel>();
                            recordingBounds = new Rect(regionSelectorVm.Left, regionSelectorVm.Top, regionSelectorVm.Width, regionSelectorVm.Height);
                        }

                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _timeOverlayWindow.Left = recordingBounds.Left + 20;
                            _timeOverlayWindow.Top = recordingBounds.Top + recordingBounds.Height - _timeOverlayWindow.ActualHeight - 20;
                        }), DispatcherPriority.Loaded);
                    }
                }
                else if (state == RecorderState.Paused)
                {
                    if (!isRegion && _borderWindow != null) _borderWindow.PauseBlink();

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var dialog = new PauseDialog();
                        if (dialog.ShowDialog() == true)
                        {
                            switch (dialog.Action)
                            {
                                case PauseDialogAction.Resume: _vm.PauseCommand.ExecuteIfCan(); break;
                                case PauseDialogAction.Save: _vm.RecordCommand.ExecuteIfCan(); break;
                                case PauseDialogAction.Discard: _vm.DiscardRecording(); break;
                            }
                        }
                        else { _vm.PauseCommand.ExecuteIfCan(); }
                    }));
                }
                else if (state == RecorderState.NotRecording)
                {
                    if (_statusBar != null) { _statusBar.Stop(); _statusBar = null; }
                    if (_borderWindow != null) { _borderWindow.StopBlink(); _borderWindow = null; }
                    if (_timeOverlayWindow != null) { _timeOverlayWindow.Stop(); _timeOverlayWindow = null; }

                    _trackTimer.Stop();
                    _trackedHwnd = IntPtr.Zero;
                }
            });

            _vm.VideoSources.ObserveProperty(x => x.SelectedVideoSourceKind).Subscribe(sourceKind =>
            {
                Application.Current.Dispatcher.Invoke(async () =>
                {
                    if (_vm.RecorderState.Value != RecorderState.NotRecording) return;

                    if (_borderWindow != null) { _borderWindow.StopBlink(); _borderWindow = null; }
                    _trackTimer.Stop();
                    _trackedHwnd = IntPtr.Zero;

                    if (sourceKind is WindowSourceProvider windowProvider)
                    {
                        await Task.Delay(300);

                        if (windowProvider.Source is WindowItem windowItem)
                        {
                            _trackedHwnd = windowItem.Window.Handle;

                            _borderWindow = new RecordingBorderWindow();
                            _borderWindow.SetStaticRed();
                            _borderWindow.Show();

                            UpdateTrackingPosition();
                            _trackTimer.Start();
                        }
                    }
                });
            });
        }

        private static void UpdateTrackingPosition()
        {
            if (_borderWindow != null && _trackedHwnd != IntPtr.Zero)
            {
                if (GetWindowRect(_trackedHwnd, out RECT rect))
                {
                    var bounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

                    if (_vm.RecorderState.Value == RecorderState.Recording)
                    {
                        _borderWindow.SetWindowBounds(bounds, expand: true);
                    }
                    else
                    {
                        _borderWindow.SetWindowBounds(bounds, expand: false);
                    }

                    UpdateStatusBarPosition(bounds);
                }
            }
        }

        private static void UpdateStatusBarPosition(Rect targetBounds)
        {
            if (_statusBar == null) return;

            // 核心修复：录制时，红框向外扩了 8 像素，状态栏也得跟着往外让 8 像素，避免重叠。
            double margin = (_vm.RecorderState.Value == RecorderState.Recording) ? 8 : 0;

            double targetLeft = targetBounds.Left;

            // 默认状态栏在下方：窗口底边 + 8像素外扩 + 小小的间距
            double targetTop = targetBounds.Top + targetBounds.Height + margin + 2;

            // 如果下方放不下，自动翻转在上方
            if (targetTop + _statusBar.ActualHeight > SystemParameters.PrimaryScreenHeight)
            {
                // 放到上方时，同样要避开 8 像素的外扩
                targetTop = targetBounds.Top - _statusBar.ActualHeight - margin - 2;

                // 如果上方也没空间了，只能盖在内部（极少情况）
                if (targetTop < 0) targetTop = targetBounds.Top;
            }

            _statusBar.Left = targetLeft;
            _statusBar.Top = targetTop;
        }
    }
}