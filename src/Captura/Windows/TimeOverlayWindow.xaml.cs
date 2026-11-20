using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Captura
{
    public partial class TimeOverlayWindow : Window
    {
        private DispatcherTimer _timer;

        public TimeOverlayWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) =>
            {
                // 只更新一个 TextBlock 即可
                TimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            _timer.Start();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove(); // 依然可以随便拖动
            }
        }

        public void Stop()
        {
            _timer.Stop();
            this.Close();
        }
    }
}