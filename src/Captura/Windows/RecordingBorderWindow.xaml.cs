using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Captura
{
    public partial class RecordingBorderWindow : Window
    {
        private DispatcherTimer _blinkTimer;
        private bool _isYellow = true;

        public RecordingBorderWindow()
        {
            InitializeComponent();
            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _blinkTimer.Tick += (s, e) =>
            {
                _isYellow = !_isYellow;
                BlinkBorder.BorderBrush = _isYellow ? Brushes.Yellow : Brushes.Red;
            };
        }

        public void SetWindowBounds(Rect bounds, bool expand = false)
        {
            if (expand)
            {
                double margin = 8;
                this.Left = bounds.Left - margin;
                this.Top = bounds.Top - margin;
                this.Width = bounds.Width + margin * 2;
                this.Height = bounds.Height + margin * 2;
            }
            else
            {
                this.Left = bounds.Left;
                this.Top = bounds.Top;
                this.Width = bounds.Width;
                this.Height = bounds.Height;
            }
        }

        public void StartBlink()
        {
            _isYellow = true;
            BlinkBorder.BorderBrush = Brushes.Yellow;
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }

        public void PauseBlink()
        {
            _blinkTimer.Stop();
            BlinkBorder.BorderBrush = Brushes.Red;
        }

        public void SetStaticRed()
        {
            _blinkTimer.Stop();
            BlinkBorder.BorderBrush = Brushes.Red;
        }

        public void SetDragState(bool isDragging)
        {
            this.Visibility = isDragging ? Visibility.Collapsed : Visibility.Visible;
        }

        public void StopBlink()
        {
            _blinkTimer.Stop();
            this.Close();
        }
    }
}