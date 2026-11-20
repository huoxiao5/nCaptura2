using System.Windows;
using Captura.ViewModels;

namespace Captura
{
    public enum PauseDialogAction { Resume, Save, Discard }

    public partial class PauseDialog : Window
    {
        public PauseDialogAction Action { get; private set; } = PauseDialogAction.Resume;

        private readonly Settings _settings;

        public PauseDialog()
        {
            InitializeComponent();

            _settings = ServiceProvider.Get<Settings>();

            // 核心逻辑：只有设置了定时录制（时间大于0秒）时，才显示这个按钮
            if (_settings.Duration > 0)
            {
                ToggleShutdownBtn.Visibility = Visibility.Visible;
                UpdateButtonState();
            }
            else
            {
                ToggleShutdownBtn.Visibility = Visibility.Collapsed;
            }

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    Action = PauseDialogAction.Resume;
                    DialogResult = true;
                }
            };
        }

        // 刷新按钮的显示文字
        private void UpdateButtonState()
        {
            if (_settings.AutoShutdownAfterTimer)
            {
                ToggleShutdownBtn.Content = "关闭：录制结束后自动关机";
                ToggleShutdownBtn.Foreground = System.Windows.Media.Brushes.Red; // 开启状态下，文字变红提示可以关闭
                ToggleShutdownBtn.FontWeight = FontWeights.Bold;
            }
            else
            {
                ToggleShutdownBtn.Content = "开启：录制结束后自动关机";
                ToggleShutdownBtn.Foreground = System.Windows.Media.Brushes.Black;
                ToggleShutdownBtn.FontWeight = FontWeights.Normal;
            }
        }

        private void ToggleShutdown_Click(object sender, RoutedEventArgs e)
        {
            // 切换状态
            _settings.AutoShutdownAfterTimer = !_settings.AutoShutdownAfterTimer;

            // 刷新 UI
            UpdateButtonState();

            // 弹出提示框（模态，关闭后依然停留在当前的 PauseDialog 中）
            if (_settings.AutoShutdownAfterTimer)
            {
                MessageBox.Show(this, "已开启：录制结束后将自动关机。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "已取消：录制结束后将不会自动关机。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        void Resume_Click(object sender, RoutedEventArgs e) { Action = PauseDialogAction.Resume; DialogResult = true; }

        void Save_Click(object sender, RoutedEventArgs e) { Action = PauseDialogAction.Save; DialogResult = true; }

        void Discard_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this, "确定要丢弃当前录像吗？\n刚才录制的所有内容将不会被保存。", "确认丢弃", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Action = PauseDialogAction.Discard;
                DialogResult = true;
            }
        }
    }
}