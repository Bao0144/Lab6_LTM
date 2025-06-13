using System;
using System.Windows.Forms;

namespace Lab6
{
    public partial class Form1 : Form
    {
        private string defaultIP = "192.168.231.50";  // IP LAN mặc định
        private int port = 9000;

        public Form1()
        {
            InitializeComponent();
        }

        // Nút CreateRoom — khởi động server và mở WhiteboardForm
        private void button1_Click(object sender, EventArgs e)
        {
            // Tạo server ở cổng mặc định
            WhiteBoardServer server = new WhiteBoardServer(port);
            server.Start();
            server.Show();
        }

        // Nút JoinRoom — kết nối vào server IP LAN mặc định
        private void button2_Click(object sender, EventArgs e)
        {
            // Khởi chạy WhiteboardForm với vai trò client
            WhiteBoardClient whiteboardForm = new WhiteBoardClient(defaultIP, port);
            whiteboardForm.Show();
        }
    }
}
