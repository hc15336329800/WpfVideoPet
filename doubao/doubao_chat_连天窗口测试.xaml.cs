using System;
using System.Windows;
using System.Windows.Input;
using WpfVideoPet.doubao;

namespace WpfVideoPet.doubao
{


    /// <summary>
    /// 豆包AI知识库API接口调用通过
    /// </summary>
    public partial class doubao_chat : Window
    {
        private readonly doubao_service_chat _doubao = new();

        public doubao_chat()
        {
            InitializeComponent();
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            string query = txtInput.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            btnSend.IsEnabled = false;
            txtChat.AppendText($"你：{query}\n");
            txtInput.Clear();

            try
            {
                // 调用服务类的流式方法
                var answer = await _doubao.AskAsync(
                    query,
                    onDelta: delta => Dispatcher.Invoke(() =>
                    {
                        txtChat.AppendText(delta);
                        txtChat.ScrollToEnd();
                    })
                );

                txtChat.AppendText("\n\n");
            }
            catch (Exception ex)
            {
                txtChat.AppendText($"\n[错误] {ex.Message}\n");
            }
            finally
            {
                btnSend.IsEnabled = true;
            }
        }

        // 支持按下 Enter 直接发送消息
        private void txtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                e.Handled = true;
                btnSend_Click(sender, e);
            }
        }
    }
}
