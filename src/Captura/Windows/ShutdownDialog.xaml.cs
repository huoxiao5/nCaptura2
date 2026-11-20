using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Captura
{
    public partial class ShutdownDialog : Window
    {
        private DispatcherTimer _timer;
        private int _secondsLeft = 60;

        // 标记是否真正执行了关机（防止正常关机时也弹出取消提示）
        private bool _isShutdownExecuted = false;

        public ShutdownDialog()
        {
            InitializeComponent();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _secondsLeft--;
                CountdownRun.Text = _secondsLeft.ToString();

                if (_secondsLeft <= 0)
                {
                    ExecuteShutdown();
                }
            };
            _timer.Start();
        }

        private void ExecuteShutdown()
        {
            try
            {
                _isShutdownExecuted = true;
                _timer.Stop();
                System.Diagnostics.Process.Start("shutdown", "/s /t 5 /f");
                this.Close();
            }
            catch { }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // 1. 立刻停掉定时器，斩断倒计时
            _timer.Stop();

            // 2. 如果不是因为倒计时结束或点“立即关机”触发的关闭
            if (!_isShutdownExecuted)
            {
                // 3. 核心思路：使用 Task.Delay 延迟 1 秒，让当前窗口先彻底灰飞烟灭
                Task.Delay(1000).ContinueWith(_ =>
                {
                    // 回到 UI 线程弹出提示
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("您已取消自动关机！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                });
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ShutdownNow_Click(object sender, RoutedEventArgs e)
        {
            ExecuteShutdown();
        }
    }
}