using Captura.Models;
using Reactive.Bindings.Extensions;
using System;
using System.Drawing;
using System.Linq;
using System.Reactive.Linq; // 引入 Rx 命名空间用于监听状态
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace Captura
{
    public partial class MainWindow
    {
        public static MainWindow Instance { get; private set; }

        readonly MainWindowHelper _helper;

        public MainWindow()
        {
            Instance = this;

            InitializeComponent();

            _helper = ServiceProvider.Get<MainWindowHelper>();

            _helper.MainViewModel.Init(!App.CmdOptions.NoPersist, !App.CmdOptions.Reset);

            _helper.HotkeySetup.Setup();

            _helper.TimerModel.Init();

            Loaded += (Sender, Args) =>
            {
                RepositionWindowIfOutside();

                ServiceProvider.Get<WebcamPage>().SetupPreview();

                _helper.HotkeySetup.ShowUnregistered();
            };

            if (App.CmdOptions.Tray || _helper.Settings.Tray.MinToTrayOnStartup)
                Hide();

            // 修改：接管窗口关闭拦截，防止卡死
            Closing += (Sender, Args) =>
            {
                // 如果正在录制
                if (_helper.RecordingViewModel.RecorderState.Value != RecorderState.NotRecording)
                {
                    Args.Cancel = true; // 先取消原生关闭

                    // 弹窗确认（CanExit 内部会弹窗）
                    if (!_helper.RecordingViewModel.CanExit())
                        return; // 用户点否，停留在界面

                    // 用户点是，执行安全退出（异步停止录制 -> 销毁资源 -> 关闭程序）
                    SafeExitWhileRecording();
                    return;
                }

                // 没有在录制，走原有退出逻辑
                if (!TryExit())
                    Args.Cancel = true;
            };

            // Register to bring this instance to foreground when other instances are launched.
            SingleInstanceManager.StartListening(WakeApp);
        }

        void WakeApp()
        {
            Dispatcher.Invoke(() =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }

                Activate();
            });
        }

        void RepositionWindowIfOutside()
        {
            // Window dimensions taking care of DPI
            var rect = new RectangleF((float)Left,
                (float)Top,
                (float)ActualWidth,
                (float)ActualHeight).ApplyDpi();

            if (!Screen.AllScreens.Any(M => M.Bounds.Contains(rect)))
            {
                Left = 50;
                Top = 50;
            }
        }

        void Grid_PreviewMouseLeftButtonDown(object Sender, MouseButtonEventArgs Args)
        {
            DragMove();

            Args.Handled = true;
        }

        void MinButton_Click(object Sender, RoutedEventArgs Args) => SystemCommands.MinimizeWindow(this);

        // 修改：点击 X 按钮的响应逻辑
        void CloseButton_Click(object Sender, RoutedEventArgs Args)
        {
            // 如果正在录制
            if (_helper.RecordingViewModel.RecorderState.Value != RecorderState.NotRecording)
            {
                // 弹窗确认
                if (!_helper.RecordingViewModel.CanExit())
                    return; // 用户点否

                // 用户点是，执行安全退出
                SafeExitWhileRecording();
            }
            else
            {
                // 没有录制，正常退出
                if (_helper.Settings.Tray.MinToTrayOnClose)
                {
                    Hide();
                }
                else
                {
                    Close();
                }
            }
        }

        void SystemTray_TrayMouseDoubleClick(object Sender, RoutedEventArgs Args)
        {
            if (Visibility == Visibility.Visible)
            {
                Hide();
            }
            else this.ShowAndFocus();
        }

        bool TryExit()
        {
            if (!_helper.RecordingViewModel.CanExit())
                return false;

            ServiceProvider.Dispose();

            return true;
        }

        void MenuExit_Click(object Sender, RoutedEventArgs Args) => Close();

        void HideButton_Click(object Sender, RoutedEventArgs Args) => Hide();

        void ShowMainWindow(object Sender, RoutedEventArgs E) => this.ShowAndFocus();

        // =====================================================================
        // 新增：核心安全退出方法（彻底解决录制中退出卡死的问题）
        // =====================================================================
        
        private void SafeExitWhileRecording()
        {
            try
            {
                // 先隐藏主窗口，避免用户看到未响应的假死状态
                Hide();

                var vm = _helper.RecordingViewModel;

                // 1. 强制触发丢弃录制（停止录制并删除刚才产生的视频文件）
                vm.DiscardRecording();

                // 2. 监听录制状态，等底层彻底完成文件封箱和删除（变为 NotRecording）
                var subscription = vm.RecorderState
                    .ObserveProperty(x => x.Value)
                    .Where(state => state == RecorderState.NotRecording)
                    .Take(1) // 只要触发一次就取消订阅
                    .Subscribe(state =>
                    {
                        // 回到主线程执行关闭
                        Dispatcher.Invoke(() =>
                        {
                            // 禁用最小化到托盘，防止再次被拦截
                            _helper.Settings.Tray.MinToTrayOnClose = false;

                            // 安全清理依赖并关闭应用
                            ServiceProvider.Dispose();
                            System.Windows.Application.Current.Shutdown();
                        });
                    });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("退出时发生错误: " + ex.Message);
                System.Windows.Application.Current.Shutdown();
            }
        }
    }
}