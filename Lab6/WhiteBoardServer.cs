using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Mail;
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

        // Email configuration
        private const int MAX_CLIENTS = 5;
        private bool emailSent = false; // Để tránh gửi email nhiều lần
        private readonly string smtpServer = "smtp.gmail.com"; // Thay đổi theo email provider
        private readonly int smtpPort = 587;
        private readonly string adminEmail = "23520501@gm.uit.edu.vn"; // Email quản trị viên
        private readonly string senderEmail = "quocbaoo2005@gmail.com"; // Email gửi
        private readonly string senderPassword = "vqgw pmte nibz wcmh"; // App password hoặc mật khẩu

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

        private async void SendMaxClientsAlert()
        {
            if (emailSent) return; // Đã gửi email rồi thì không gửi nữa

            try
            {
                using (SmtpClient smtpClient = new SmtpClient(smtpServer, smtpPort))
                {
                    smtpClient.EnableSsl = true;
                    smtpClient.Credentials = new NetworkCredential(senderEmail, senderPassword);

                    MailMessage mail = new MailMessage();
                    mail.From = new MailAddress(senderEmail, "WhiteBoard Server");
                    mail.To.Add(adminEmail);
                    mail.Subject = "Cảnh báo: Đạt số lượng client tối đa";
                    mail.Body = $@"
                        Kính gửi Quản trị viên,

                        Hệ thống WhiteBoard Server đã đạt số lượng client kết nối tối đa.

                        Thông tin chi tiết:
                        - Thời gian: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                        - Số lượng client hiện tại: {clients.Count}
                        - Số lượng client tối đa: {MAX_CLIENTS}
                        - Port server: {port}
                        - IP server: {IPAddress.Any}

                        Vui lòng kiểm tra và xử lý khi cần thiết.

                        Trân trọng,
                        WhiteBoard Server System";

                    mail.IsBodyHtml = false;

                    await smtpClient.SendMailAsync(mail);
                    emailSent = true;

                    // Log thông báo gửi email thành công
                    Console.WriteLine($"[{DateTime.Now}] Email cảnh báo đã được gửi tới {adminEmail}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now}] Lỗi gửi email: {ex.Message}");
                MessageBox.Show($"Không thể gửi email cảnh báo: {ex.Message}", "Lỗi Email",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                        // Kiểm tra số lượng client trước khi chấp nhận kết nối mới
                        lock (clients)
                        {
                            if (clients.Count >= MAX_CLIENTS)
                            {
                                // Từ chối kết nối nếu đã đạt tối đa
                                TcpClient rejectedClient = listener.AcceptTcpClient();
                                rejectedClient.Close();
                                Console.WriteLine($"[{DateTime.Now}] Từ chối client mới - Đã đạt tối đa {MAX_CLIENTS} clients");
                                continue;
                            }
                        }

                        TcpClient client = listener.AcceptTcpClient();
                        client.NoDelay = true;
                        lock (clients)
                        {
                            clients.Add(client);
                            UpdateClientCount();

                            // Kiểm tra và gửi email cảnh báo nếu đạt tối đa
                            if (clients.Count >= MAX_CLIENTS)
                            {
                                _ = Task.Run(() => SendMaxClientsAlert());
                            }
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

                    // Reset flag gửi email khi số client giảm xuống dưới tối đa
                    if (clients.Count < MAX_CLIENTS)
                    {
                        emailSent = false;
                    }
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
                // Hiển thị màu cảnh báo khi đạt tối đa
                if (clients.Count >= MAX_CLIENTS)
                {
                    label1.Text = $"Clients: {clients.Count}/{MAX_CLIENTS}";
                    label1.ForeColor = Color.Red;
                }
                else
                {
                    label1.Text = $"Clients: {clients.Count}/{MAX_CLIENTS}";
                    label1.ForeColor = Color.Black;
                }
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