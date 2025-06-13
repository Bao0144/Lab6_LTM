using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Lab6
{
    public partial class WhiteBoardServer : Form
    {
        private TcpListener listener;
        private int port;
        private Thread? listenThread;
        private List<TcpClient> clients;
        private volatile bool isRunning = false;

        private Color currentColor = Color.Black;
        private int penSize = 2;

        private Bitmap canvas;
        private Graphics g;
        private bool isDrawing = false;
        private Point lastPoint;
        private object canvasLock = new object();
        public WhiteBoardServer(int port)
        {
            InitializeComponent();

            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                null, panel1, new object[] { true });


            this.port = port;
            listener = new TcpListener(IPAddress.Any, port);
            clients = new List<TcpClient>();

            canvas = new Bitmap(800, 600);
            g = Graphics.FromImage(canvas);
            g.Clear(Color.White);

            panel1.MouseDown += panel1_MouseDown;
            panel1.MouseMove += panel1_MouseMove;
            panel1.MouseUp += panel1_MouseUp;
            panel1.Paint += panel1_Paint;
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
                DrawAndBroadcast(lastPoint, e.Location, currentColor, penSize);
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

        private void DrawAndBroadcast(Point p1, Point p2, Color color, int size)
        {
            lock (canvasLock)
            {
                using (Pen pen = new Pen(color, size))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLine(pen, p1, p2);
                }
            }

            panel1.Invalidate();

            string message = $"{p1.X},{p1.Y},{p2.X},{p2.Y},{color.R},{color.G},{color.B},{size}";
            byte[] data = Encoding.UTF8.GetBytes(message);
            Broadcast(data, data.Length);
        }

        public void Start()
        {
            isRunning = true;
            listenThread = new Thread(ListenForClients);
            listenThread.IsBackground = true;
            listenThread.Start();
        }

        private void ListenForClients()
        {
            listener.Start();
            try
            {
                while (isRunning)
                {
                    if (listener.Pending())
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (clients)
                        {
                            clients.Add(client);
                            UpdateClientCount();
                        }

                        Thread clientThread = new Thread(HandleClientComm);
                        clientThread.IsBackground = true;
                        clientThread.Start(client);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine("Socket closed: " + ex.Message);
            }
        }

        private void HandleClientComm(object? clientObj)
        {
            TcpClient client = (TcpClient)clientObj!;
            NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[1024];
            int bytesRead;

            try
            {
                while (isRunning && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    ProcessMessage(message);
                    Broadcast(buffer, bytesRead); // Gửi tới client khác
                }
            }
            catch
            {
                // Bỏ qua lỗi
            }
            finally
            {
                lock (clients)
                {
                    clients.Remove(client);
                    UpdateClientCount();
                }
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

                    panel1.Invoke(new Action(() => panel1.Invalidate()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessMessage error: " + ex.Message);
            }
        }

        private void Broadcast(byte[] data, int length)
        {
            lock (clients)
            {
                foreach (var client in clients)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        stream.Write(data, 0, length);
                        stream.Flush();
                    }
                    catch
                    {
                        // Bỏ qua nếu lỗi gửi
                    }
                }
            }
        }

        private void StopServer()
        {
            isRunning = false;
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients)
                {
                    client.Close();
                }
                clients.Clear();
            }

            listenThread?.Join();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            SaveBoardImage();
            StopServer();
            Application.Exit();
        }
        private void UpdateClientCount()
        {
            if (label1.InvokeRequired)
            {
                label1.Invoke(new Action(UpdateClientCount));
            }
            else
            {
                label1.Text = $"Clients: {clients.Count}";
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
            if (radioButton2.Checked) penSize = 6;
        }

        private void radioButton3_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButton3.Checked) penSize = 10;
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
            StopServer();
            Application.Exit();
        }
    }
}