using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        public class DrawCommand
        {
            public Point StartPoint { get; set; }
            public Point EndPoint { get; set; }
            public Color Color { get; set; }
            public int PenSize { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class ImageCommand
        {
            public Rectangle Rect { get; set; }
            public string Base64 { get; set; }
        }

        private List<ImageCommand> imageHistory = new List<ImageCommand>(); // ⬅ để lưu lịch sử ảnh
        private List<DrawCommand> drawHistory = new List<DrawCommand>();
        private readonly object historyLock = new object();

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
            // Lưu vào lịch sử
            lock (historyLock)
            {
                drawHistory.Add(new DrawCommand
                {
                    StartPoint = p1,
                    EndPoint = p2,
                    Color = color,
                    PenSize = size,
                    Timestamp = DateTime.Now
                });
            }

            // Vẽ trên canvas
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

            string message = $"DRAW:{p1.X},{p1.Y},{p2.X},{p2.Y},{color.R},{color.G},{color.B},{size}";
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

                        // Gửi lịch sử các nét vẽ cho client mới
                        SendDrawHistoryToClient(client);

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

        private void ProcessImageMessage(string message)
        {
            try
            {
                string data = message.Substring("IMAGE:".Length);
                string[] parts = data.Split(new[] { ',' }, 5);

                if (parts.Length == 5)
                {
                    int x = int.Parse(parts[0]);
                    int y = int.Parse(parts[1]);
                    int w = int.Parse(parts[2]);
                    int h = int.Parse(parts[3]);
                    string base64 = parts[4];

                    byte[] imgData = Convert.FromBase64String(base64);
                    using (MemoryStream ms = new MemoryStream(imgData))
                    using (Image img = Image.FromStream(ms))
                    {
                        lock (canvasLock)
                        {
                            g.DrawImage(img, new Rectangle(x, y, w, h));
                        }

                        panel1.Invoke(new Action(() => panel1.Invalidate()));
                    }

                    // ✅ Lưu vào lịch sử ảnh
                    lock (historyLock)
                    {
                        imageHistory.Add(new ImageCommand
                        {
                            Rect = new Rectangle(x, y, w, h),
                            Base64 = base64
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessImageMessage error: " + ex.Message);
                Console.WriteLine("❌ ProcessImageMessage ERROR: " + ex.Message);
                Console.WriteLine("❌ Message base64 phần đầu: " + message.Substring(0, Math.Min(100, message.Length)));
            }
        }
        private void HandleClientComm(object? clientObj)
        {
            TcpClient client = (TcpClient)clientObj!;
            NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[65*1024];
            int bytesRead;

            try
            {
                while (isRunning && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (message.StartsWith("DRAW:"))
                    {
                        ProcessDrawMessage(message);
                        BroadcastToOthers(buffer, bytesRead, client);
                    }
                    else if (message.StartsWith("IMAGE:"))
                    {
                        // Forward ảnh tới các client khác
                        ProcessImageMessage(message);
                        BroadcastToOthers(buffer, bytesRead, client);
                    }
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

        private void ProcessDrawMessage(string message)
        {
            try
            {
                // Bỏ prefix "DRAW:"
                string drawData = message.Substring(5);
                string[] parts = drawData.Split(',');

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
                    Point p1 = new Point(x1, y1);
                    Point p2 = new Point(x2, y2);

                    // Lưu vào lịch sử
                    lock (historyLock)
                    {
                        drawHistory.Add(new DrawCommand
                        {
                            StartPoint = p1,
                            EndPoint = p2,
                            Color = color,
                            PenSize = size,
                            Timestamp = DateTime.Now
                        });
                    }

                    // Vẽ lên canvas
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

                    panel1.Invoke(new Action(() => panel1.Invalidate()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessDrawMessage error: " + ex.Message);
            }
        }

        private void SendDrawHistoryToClient(TcpClient client)
        {
            try
            {
                lock (historyLock)
                {
                    Console.WriteLine($"Gửi {drawHistory.Count} nét vẽ cho client mới");

                    NetworkStream stream = client.GetStream();

                    // Gửi số lượng commands trước
                    foreach (var img in imageHistory)
                    {
                        string msg = $"IMAGE:{img.Rect.X},{img.Rect.Y},{img.Rect.Width},{img.Rect.Height},{img.Base64}";
                        byte[] imgData = Encoding.UTF8.GetBytes(msg);
                        stream.Write(imgData, 0, imgData.Length);
                        stream.Flush();
                        Thread.Sleep(10); // Delay nhỏ để tránh client bị ngợp
                    }

                    string countMessage = $"HISTORY_COUNT:{drawHistory.Count}";
                    byte[] countData = Encoding.UTF8.GetBytes(countMessage);
                    stream.Write(countData, 0, countData.Length);
                    stream.Flush();
                    Thread.Sleep(10); // Đợi client xử lý

                    // Gửi từng command
                    foreach (var cmd in drawHistory)
                    {
                        string message = $"HISTORY:{cmd.StartPoint.X},{cmd.StartPoint.Y},{cmd.EndPoint.X},{cmd.EndPoint.Y},{cmd.Color.R},{cmd.Color.G},{cmd.Color.B},{cmd.PenSize}";
                        byte[] data = Encoding.UTF8.GetBytes(message);

                        stream.Write(data, 0, data.Length);
                        stream.Flush();

                        // Delay nhỏ để tránh overwhelm client
                        Thread.Sleep(5);
                    }

                    // Gửi signal kết thúc
                    string endMessage = "HISTORY_END";
                    byte[] endData = Encoding.UTF8.GetBytes(endMessage);
                    stream.Write(endData, 0, endData.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SendDrawHistoryToClient error: {ex.Message}");
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

        private void BroadcastToOthers(byte[] data, int length, TcpClient sender)
        {
            lock (clients)
            {
                foreach (var client in clients)
                {
                    if (client != sender) // Không gửi lại cho client gửi
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