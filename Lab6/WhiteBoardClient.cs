using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace Lab6
{
    public partial class WhiteBoardClient : Form
    {
        private bool isListening = true;
        private TcpClient client;
        private NetworkStream stream;
        private Thread listenThread;

        private Color currentColor = Color.Black;
        private int penSize = 2;
        private bool isDrawing = false;
        private Point lastPoint;

        private Bitmap canvas;
        private Graphics g;
        private readonly object canvasLock = new object();

        public WhiteBoardClient(string serverIP, int port)
        {
            InitializeComponent();

            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                null, panel1, new object[] { true });

            client = new TcpClient();
            client.Connect(serverIP, port);
            stream = client.GetStream();

            canvas = new Bitmap(800, 600);
            g = Graphics.FromImage(canvas);
            g.Clear(Color.White);
            panel1.Paint += panel1_Paint;

            panel1.MouseDown += panel1_MouseDown;
            panel1.MouseMove += panel1_MouseMove;
            panel1.MouseUp += panel1_MouseUp;

            listenThread = new Thread(ListenForServer);
            listenThread.IsBackground = true;
            listenThread.Start();
        }

        private void panel1_Paint(object? sender, PaintEventArgs e)
        {
            lock (canvasLock)
            {
                e.Graphics.DrawImage(canvas, Point.Empty);
            }
        }

        private void panel1_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDrawing = true;
                lastPoint = e.Location;
            }
        }

        private void panel1_MouseMove(object? sender, MouseEventArgs e)
        {
            if (isDrawing)
            {
                lock (canvasLock)
                {
                    using (Pen pen = new Pen(currentColor, penSize))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawLine(pen, lastPoint, e.Location);
                    }
                }

                panel1.Invalidate();

                string message = $"{lastPoint.X},{lastPoint.Y},{e.Location.X},{e.Location.Y},{currentColor.R},{currentColor.G},{currentColor.B},{penSize}";
                SendMessage(message);

                lastPoint = e.Location;
            }
        }

        private void panel1_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDrawing = false;
            }
        }

        private void ListenForServer()
        {
            byte[] buffer = new byte[1024];
            int bytesRead;

            try
            {
                while (isListening && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string message = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    panel1.Invoke(new Action(() => ProcessMessage(message)));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Disconnected from server: " + ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                string[] parts = message.Split(',');
                if (parts.Length == 8)
                {
                    int x1 = int.Parse(parts[0]);
                    int y1 = int.Parse(parts[1]);
                    int x2 = int.Parse(parts[2]);
                    int y2 = int.Parse(parts[3]);
                    int r = int.Parse(parts[4]);
                    int gVal = int.Parse(parts[5]);
                    int b = int.Parse(parts[6]);
                    int size = int.Parse(parts[7]);

                    Color color = Color.FromArgb(r, gVal, b);

                    lock (canvasLock)
                    {
                        using (Pen pen = new Pen(color, size))
                        {
                            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                            g.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                        }
                    }

                    panel1.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessMessage error: " + ex.Message);
            }
        }

        private void SendMessage(string message)
        {
            try
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Send error: " + ex.Message);
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            SaveBoardImage();
            isListening = false; // cho vòng lặp dừng
            try
            {
                stream?.Close();  // đóng stream để Read() ném lỗi
                client?.Close();  // đóng kết nối
            }
            catch { }

            listenThread?.Join(); // chờ thread kết thúc an toàn
            this.Close();
        }
        private void SaveBoardImage()
        {
            try
            {
                using (SaveFileDialog saveFileDialog = new SaveFileDialog())
                {
                    saveFileDialog.Filter = "PNG Image|*.png";
                    saveFileDialog.Title = "Chọn nơi lưu hình whiteboard";
                    saveFileDialog.FileName = $"Whiteboard_Server_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (saveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        canvas.Save(saveFileDialog.FileName, ImageFormat.Png);
                        MessageBox.Show($"Đã lưu hình whiteboard server: {saveFileDialog.FileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi lưu hình: " + ex.Message);
            }
        }
        private void button2_Click(object sender, EventArgs e)
        {
            ColorDialog colorDlg = new ColorDialog();
            if (colorDlg.ShowDialog() == DialogResult.OK)
            {
                currentColor = colorDlg.Color;
            }
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton1.Checked) penSize = 2;
        }

        private void radioButton2_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton2.Checked) penSize = 10;
        }

        private void radioButton3_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton3.Checked) penSize = 6;
        }

        private void radioButton4_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton4.Checked) penSize = 15;
        }

        private void radioButton5_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton5.Checked) penSize = 20;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            isListening = false; // cho vòng lặp dừng
            try
            {
                stream?.Close();  // đóng stream để Read() ném lỗi
                client?.Close();  // đóng kết nối
            }
            catch { }

            listenThread?.Join(); // chờ thread kết thúc an toàn
        }
    }
}
