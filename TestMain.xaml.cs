using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfVideoPet
{
    /// <summary>
    /// TestMain.xaml 的交互逻辑
    /// </summary>
    public partial class TestMain : Page
    {
        public TestMain()
        {
            InitializeComponent();
        }


        // 修改：新增按钮点击事件，打开弹出层
        private void btnShow_Click(object sender, RoutedEventArgs e)
        {
            popInfo.IsOpen = true; // 目的：点击按钮后在其下方显示弹出内容
        }

    }
}
