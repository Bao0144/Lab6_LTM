using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using System.Text;
using System.Drawing.Drawing2D;
using System.Diagnostics;

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
        private DateTime lastSendTime = DateTime.MinValue;
        private readonly TimeSpan sendInterval = TimeSpan.FromMilliseconds(15); // mỗi 15ms

        private Image? insertedImage = null;
        private Rectangle insertedImageRect = new Rectangle(100, 100, 200, 150); // Vị trí và kích thước ảnh

        private bool draggingImage = false;
        private Point imageDragOffset;
        private enum DragMode { None, Move, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }
        private DragMode dragMode = DragMode.None;
        private const int handleSize = 10; // kích thước điểm kéo

        private DateTime lastSendImageTime = DateTime.MinValue;
        private readonly TimeSpan sendImageInterval = TimeSpan.FromMilliseconds(100);

        // Để theo dõi việc nhận lịch sử
        private bool isReceivingHistory = false;
        private int expectedHistoryCount = 0;
        private int receivedHistoryCount = 0;

        public WhiteBoardClient(string serverIP, int port)
        {
            InitializeComponent();

            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                null, panel1, new object[] { true });

            client = new TcpClient();
            client.NoDelay = true;
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

            // Vẽ ảnh nếu có
            if (insertedImage != null)
            {
                e.Graphics.DrawImage(insertedImage, insertedImageRect);
                // (Tùy chọn: Vẽ viền)
                e.Graphics.DrawRectangle(Pens.Blue, insertedImageRect);
                int handleSize = 8; // Kích thước ô vuông handle
                Brush handleBrush = Brushes.White;
                Pen handlePen = Pens.Black;

                Rectangle tl = new Rectangle(insertedImageRect.Left - handleSize / 2, insertedImageRect.Top - handleSize / 2, handleSize, handleSize);
                Rectangle tr = new Rectangle(insertedImageRect.Right - handleSize / 2, insertedImageRect.Top - handleSize / 2, handleSize, handleSize);
                Rectangle bl = new Rectangle(insertedImageRect.Left - handleSize / 2, insertedImageRect.Bottom - handleSize / 2, handleSize, handleSize);
                Rectangle br = new Rectangle(insertedImageRect.Right - handleSize / 2, insertedImageRect.Bottom - handleSize / 2, handleSize, handleSize);

                // Vẽ 4 góc handle
                e.Graphics.FillRectangle(handleBrush, tl);
                e.Graphics.DrawRectangle(handlePen, tl);

                e.Graphics.FillRectangle(handleBrush, tr);
                e.Graphics.DrawRectangle(handlePen, tr);

                e.Graphics.FillRectangle(handleBrush, bl);
                e.Graphics.DrawRectangle(handlePen, bl);

                e.Graphics.FillRectangle(handleBrush, br);
                e.Graphics.DrawRectangle(handlePen, br);

            }
        }


        private void panel1_MouseDown(object? sender, MouseEventArgs e)
        {
            if (insertedImage != null)
            {
                dragMode = GetResizeHandle(e.Location); // ✅ xác định người dùng click vào đâu

                if (dragMode == DragMode.Move)
                {
                    imageDragOffset = new Point(e.X - insertedImageRect.X, e.Y - insertedImageRect.Y);
                    return;
                }
                else if (dragMode != DragMode.None)
                {
                    return; // nếu đang resize → không vẽ
                }
            }

            if (e.Button == MouseButtons.Left)
            {
                isDrawing = true;
                lastPoint = e.Location;
            }
        }


        private void panel1_MouseMove(object? sender, MouseEventArgs e)
        {
            if (insertedImage != null && dragMode != DragMode.None)
            {
                // Resize hoặc move ảnh
                switch (dragMode)
                {
                    case DragMode.Move:
                        Cursor = Cursors.SizeAll;
                        insertedImageRect.X = e.X - imageDragOffset.X;
                        insertedImageRect.Y = e.Y - imageDragOffset.Y;
                        break;
                    case DragMode.ResizeTopLeft:
                        Cursor = Cursors.SizeNWSE;
                        insertedImageRect.Width = Math.Max(20, insertedImageRect.Right - e.X);
                        insertedImageRect.Height = Math.Max(20, insertedImageRect.Bottom - e.Y);
                        insertedImageRect.X = e.X;
                        insertedImageRect.Y = e.Y;
                        break;

                    case DragMode.ResizeTopRight:
                        Cursor = Cursors.SizeNESW;
                        insertedImageRect.Width = Math.Max(20, e.X - insertedImageRect.X);
                        insertedImageRect.Height = Math.Max(20, insertedImageRect.Bottom - e.Y);
                        insertedImageRect.Y = e.Y;
                        break;

                    case DragMode.ResizeBottomLeft:
                        Cursor = Cursors.SizeNESW;
                        insertedImageRect.Width = Math.Max(20, insertedImageRect.Right - e.X);
                        insertedImageRect.Height = Math.Max(20, e.Y - insertedImageRect.Y);
                        insertedImageRect.X = e.X;
                        break;

                    case DragMode.ResizeBottomRight:
                        Cursor = Cursors.SizeNWSE;
                        insertedImageRect.Width = Math.Max(20, e.X - insertedImageRect.X);
                        insertedImageRect.Height = Math.Max(20, e.Y - insertedImageRect.Y);
                        break;
                }

                // Gửi ảnh cập nhật nếu cần
                DateTime now = DateTime.Now;
                if ((now - lastSendImageTime) >= sendImageInterval)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        insertedImage.Save(ms, ImageFormat.Png);
                        string base64 = Convert.ToBase64String(ms.ToArray());
                        string msg = $"IMAGE:{insertedImageRect.X},{insertedImageRect.Y},{insertedImageRect.Width},{insertedImageRect.Height},{base64}";
                        SendMessage(msg);
                    }
                    lastSendImageTime = now;
                }

                panel1.Invalidate();
                return;
            }

            // Nếu đang vẽ bằng bút
            if (isDrawing)
            {
                DateTime now = DateTime.Now;
                if ((now - lastSendTime) >= sendInterval)
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

                    string message = $"DRAW:{lastPoint.X},{lastPoint.Y},{e.Location.X},{e.Location.Y},{currentColor.R},{currentColor.G},{currentColor.B},{penSize}";
                    SendMessage(message);

                    lastPoint = e.Location;
                    lastSendTime = now;
                }
            }

            // Đổi con trỏ chuột nếu hover lên handle
            if (insertedImage != null)
            {
                var mode = GetResizeHandle(e.Location);
                switch (mode)
                {
                    case DragMode.Move:
                        Cursor = Cursors.SizeAll;
                        break;
                    case DragMode.ResizeTopLeft:
                    case DragMode.ResizeBottomRight:
                        Cursor = Cursors.SizeNWSE;
                        break;
                    case DragMode.ResizeTopRight:
                    case DragMode.ResizeBottomLeft:
                        Cursor = Cursors.SizeNESW;
                        break;
                    default:
                        Cursor = Cursors.Default;
                        break;
                }
            }
        }

        private void panel1_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDrawing = false;
                dragMode = DragMode.None; // ✅ reset chế độ kéo/resize
                Cursor = Cursors.Default; // (tùy chọn) đưa con trỏ về lại bình thường
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
                    {
                        insertedImage = Image.FromStream(ms);
                        insertedImageRect = new Rectangle(x, y, w, h);
                    }

                    this.Invoke(new Action(() => panel1.Invalidate()));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ProcessImageMessage error: " + ex.Message);
            }
        }

        private void ListenForServer()
        {
            byte[] buffer = new byte[64*1024];
            int bytesRead;

            try
            {
                while (isListening && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Xử lý các message có thể được ghép lại
                    string[] messages = message.Split(new string[] { "DRAW:", "HISTORY:", "HISTORY_COUNT:", "HISTORY_END" },
                        StringSplitOptions.RemoveEmptyEntries);

                    // Tìm và xử lý từng loại message
                    if (message.Contains("HISTORY_COUNT:"))
                    {
                        ProcessHistoryCount(message);
                    }
                    else if (message.Contains("HISTORY:"))
                    {
                        ProcessHistoryMessage(message);
                    }
                    else if (message.Contains("HISTORY_END"))
                    {
                        ProcessHistoryEnd();
                    }
                    else if (message.Contains("DRAW:"))
                    {
                        ProcessDrawMessage(message);
                    }
                    else if (message.StartsWith("IMAGE:"))
                    {
                        ProcessImageMessage(message);
                    }
                }
            }
            catch (Exception ex)
            {
               Debug.WriteLine("Disconnected from server: " + ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        private void ProcessHistoryCount(string message)
        {
            try
            {
                // Tìm vị trí của "HISTORY_COUNT:"
                int startIndex = message.IndexOf("HISTORY_COUNT:") + "HISTORY_COUNT:".Length;
                string countStr = message.Substring(startIndex);

                // Lấy chỉ số phần đầu (trước ký tự không phải số)
                string numberPart = "";
                foreach (char c in countStr)
                {
                    if (char.IsDigit(c))
                        numberPart += c;
                    else
                        break;
                }

                if (int.TryParse(numberPart, out int count))
                {
                    expectedHistoryCount = count;
                    receivedHistoryCount = 0;
                    isReceivingHistory = true;
                    Console.WriteLine($"Sẽ nhận {expectedHistoryCount} nét vẽ từ lịch sử");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessHistoryCount error: " + ex.Message);
            }
        }

        private void ProcessHistoryMessage(string message)
        {
            try
            {
                // Tìm vị trí của "HISTORY:"
                int startIndex = message.IndexOf("HISTORY:") + "HISTORY:".Length;
                string historyData = message.Substring(startIndex);

                // Tìm phần data trước ký tự không phải là số hoặc dấu phay
                string drawData = "";
                foreach (char c in historyData)
                {
                    if (char.IsDigit(c) || c == ',' || c == '-')
                        drawData += c;
                    else
                        break;
                }

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

                    // Vẽ lên canvas
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

                    receivedHistoryCount++;
                    Console.WriteLine($"Đã nhận nét vẽ lịch sử {receivedHistoryCount}/{expectedHistoryCount}");

                    // Cập nhật UI
                    this.Invoke(new Action(() => panel1.Invalidate()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessHistoryMessage error: " + ex.Message);
            }
        }

        private void ProcessHistoryEnd()
        {
            isReceivingHistory = false;
            Console.WriteLine($"Hoàn thành nhận lịch sử: {receivedHistoryCount}/{expectedHistoryCount} nét vẽ");

            // Cập nhật UI cuối cùng
            this.Invoke(new Action(() => panel1.Invalidate()));
        }

        private void ProcessDrawMessage(string message)
        {
            try
            {
                // Tìm vị trí của "DRAW:"
                int startIndex = message.IndexOf("DRAW:") + "DRAW:".Length;
                string drawData = message.Substring(startIndex);

                // Tìm phần data trước ký tự không phải là số hoặc dấu phay
                string validDrawData = "";
                foreach (char c in drawData)
                {
                    if (char.IsDigit(c) || c == ',' || c == '-')
                        validDrawData += c;
                    else
                        break;
                }

                string[] parts = validDrawData.Split(',');
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

                    this.Invoke(new Action(() => panel1.Invalidate()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessDrawMessage error: " + ex.Message);
            }
        }

        private void SendMessage(string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
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
                    saveFileDialog.FileName = $"Whiteboard_Client_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (saveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        lock (canvasLock)
                        {
                            canvas.Save(saveFileDialog.FileName, ImageFormat.Png);
                        }
                        MessageBox.Show($"Đã lưu hình whiteboard client: {saveFileDialog.FileName}");
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
        private void InsertImageFromFile(string filePath)
        {
            try
            {
                using (var img = Image.FromFile(filePath))
                {
                    insertedImage = new Bitmap(img);
                }
                // gửi ảnh đến server: IMAGE:x,y,width,height,base64
                using (MemoryStream ms = new MemoryStream())
                {
                    insertedImage.Save(ms, ImageFormat.Png);
                    string base64 = Convert.ToBase64String(ms.ToArray());
                    string message = $"IMAGE:{insertedImageRect.X},{insertedImageRect.Y},{insertedImageRect.Width},{insertedImageRect.Height},{base64}";
                    // DEBUG:
                    Debug.WriteLine("CLIENT gửi ảnh:");
                    Debug.WriteLine("Base64 length: " + base64.Length);
                    Debug.WriteLine(message.Substring(0, 100) + "..."); // in 100 ký tự đầu

                    SendMessage(message);
                }

                panel1.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không mở được ảnh: " + ex.Message);
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Title = "Chọn ảnh để chèn vào whiteboard";
            openFileDialog.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif";

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                InsertImageFromFile(openFileDialog.FileName);
            }
        }
        private DragMode GetResizeHandle(Point location)
        {
            Rectangle tl = new Rectangle(insertedImageRect.Left - handleSize / 2, insertedImageRect.Top - handleSize / 2, handleSize, handleSize);
            Rectangle tr = new Rectangle(insertedImageRect.Right - handleSize / 2, insertedImageRect.Top - handleSize / 2, handleSize, handleSize);
            Rectangle bl = new Rectangle(insertedImageRect.Left - handleSize / 2, insertedImageRect.Bottom - handleSize / 2, handleSize, handleSize);
            Rectangle br = new Rectangle(insertedImageRect.Right - handleSize / 2, insertedImageRect.Bottom - handleSize / 2, handleSize, handleSize);

            if (tl.Contains(location)) return DragMode.ResizeTopLeft;
            if (tr.Contains(location)) return DragMode.ResizeTopRight;
            if (bl.Contains(location)) return DragMode.ResizeBottomLeft;
            if (br.Contains(location)) return DragMode.ResizeBottomRight;
            if (insertedImageRect.Contains(location)) return DragMode.Move;

            return DragMode.None;
        }
    }
}