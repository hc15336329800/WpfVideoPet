
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfVideoPet
{
    public partial class OverlayWindow : Window
    {
        private const int GwlExstyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x8000000;

        public OverlayWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            var styles = GetWindowLong(handle, GwlExstyle);
            SetWindowLong(handle, GwlExstyle, styles | WsExTransparent | WsExNoActivate);
        }

        public void UpdateWeather(string city, string weather, string temperature)
        {
            TxtCity.Text = city;
            TxtWeather.Text = weather;
            TxtTemp.Text = temperature;
        }

        public void UpdateTime(string timeText)
        {
            TxtTime.Text = timeText;
        }

        public void UpdatePetRotation(double angle)
        {
            CubeRotation.Angle = angle;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}