using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenCaptureApp
{
    public class ScreenCaptureServer
    {
        private TcpListener tcpListener;
        private bool isRunning;
        private int port;
        private int captureIntervalMs;
        private CancellationTokenSource cts;
        private TcpClient activeClient;
        private NetworkStream activeStream;

        public event Action<Image> ScreenshotReceived;
        public event Action<string> StatusChanged;
        public event Action<string> LogUpdated;

        public bool IsRunning => isRunning;
        public bool IsClientConnected => activeClient != null && activeClient.Connected;

        public ScreenCaptureServer(int port = 9000, int captureIntervalMs = 5000)
        {
            this.port = port;
            this.captureIntervalMs = captureIntervalMs;
            isRunning = false;
        }

        public void Start()
        {
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Start();
                isRunning = true;
                LogMessage($"Сервер запущено на порту {port}");
                StatusChanged?.Invoke($"Сервер запущено на порту {port}");

                Task.Run(() => AcceptClientsAsync());
            }
            catch (Exception ex)
            {
                LogMessage($"Помилка запуску сервера: {ex.Message}");
                StatusChanged?.Invoke($"Помилка: {ex.Message}");
            }
        }

        private async Task AcceptClientsAsync()
        {
            try
            {
                while (isRunning)
                {
                    TcpClient client = await tcpListener.AcceptTcpClientAsync();
                    LogMessage($"Клієнт підключився: {((IPEndPoint)client.Client.RemoteEndPoint).Address}");
                    StatusChanged?.Invoke("Клієнт підключився");

                    if (activeClient != null && activeClient.Connected)
                    {
                        byte[] busyMsg = Encoding.UTF8.GetBytes("BUSY|Сервер вже обробляє іншого клієнта");
                        client.GetStream().Write(busyMsg, 0, busyMsg.Length);
                        client.Close();
                        LogMessage("Відмовлено новому клієнту: сервер зайнятий");
                        continue;
                    }

                    activeClient = client;
                    activeStream = client.GetStream();

                    cts = new CancellationTokenSource();
                    Task.Run(() => HandleClientAsync(client, cts.Token));
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (isRunning)
                {
                    LogMessage($"Помилка прийому клієнтів: {ex.Message}");
                    StatusChanged?.Invoke($"Помилка: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4];

            try
            {
                byte[] readyMsg = Encoding.UTF8.GetBytes("READY");
                await stream.WriteAsync(readyMsg, 0, readyMsg.Length, token);
                LogMessage("Клієнт готовий до отримання команд");

                while (!token.IsCancellationRequested && client.Connected)
                {
                    byte[] requestMsg = Encoding.UTF8.GetBytes("SCREENSHOT");
                    await stream.WriteAsync(requestMsg, 0, requestMsg.Length, token);
                    LogMessage($"Запит скріншота відправлено. Очікування {captureIntervalMs / 1000} сек...");
                    StatusChanged?.Invoke($"Запит скріншота... Наступний через {captureIntervalMs / 1000} сек");

                    int bytesRead = await stream.ReadAsync(buffer, 0, 4, token);
                    if (bytesRead < 4) break;

                    int imageSize = BitConverter.ToInt32(buffer, 0);
                    if (imageSize <= 0 || imageSize > 50 * 1024 * 1024)
                    {
                        LogMessage($"Помилка: некоректний розмір зображення: {imageSize}");
                        break;
                    }

                    byte[] imageData = new byte[imageSize];
                    int totalRead = 0;
                    while (totalRead < imageSize)
                    {
                        int read = await stream.ReadAsync(imageData, totalRead, imageSize - totalRead, token);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead == imageSize)
                    {
                        using (MemoryStream ms = new MemoryStream(imageData))
                        {
                            Image screenshot = Image.FromStream(ms);
                            ScreenshotReceived?.Invoke(screenshot);
                            LogMessage($"Скріншот отримано. Розмір: {imageSize / 1024} KB");
                            StatusChanged?.Invoke($"Скріншот отримано ({imageSize / 1024} KB)");
                        }
                    }

                    await Task.Delay(captureIntervalMs, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"Помилка обробки клієнта: {ex.Message}");
                    StatusChanged?.Invoke($"Помилка: {ex.Message}");
                }
            }
            finally
            {
                LogMessage("Клієнт відключився");
                StatusChanged?.Invoke("Клієнт відключився");
                activeClient = null;
                activeStream = null;
                client.Close();
            }
        }

        public void StopCapture()
        {
            cts?.Cancel();
            if (activeStream != null && activeClient.Connected)
            {
                try
                {
                    byte[] stopMsg = Encoding.UTF8.GetBytes("STOP");
                    activeStream.Write(stopMsg, 0, stopMsg.Length);
                }
                catch { }
            }
        }

        public void UpdateInterval(int newIntervalMs)
        {
            captureIntervalMs = newIntervalMs;
            LogMessage($"Інтервал захоплення змінено на {newIntervalMs} мс");
        }

        private void LogMessage(string message)
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogUpdated?.Invoke(logEntry);
        }

        public void Stop()
        {
            isRunning = false;
            cts?.Cancel();
            activeClient?.Close();
            tcpListener?.Stop();
            LogMessage("Сервер зупинено");
            StatusChanged?.Invoke("Сервер зупинено");
        }
    }

    public class ServerForm : Form
    {
        private ScreenCaptureServer server;
        private PictureBox pictureBox;
        private TextBox txtPort;
        private TextBox txtInterval;
        private Button btnStart;
        private Button btnStop;
        private Button btnCaptureNow;
        private Button btnSaveScreenshot;
        private TextBox txtLog;
        private Label lblStatus;
        private Label lblConnectionStatus;
        private Image currentScreenshot;

        public ServerForm()
        {
            InitializeComponents();
            this.Text = "Сервер захоплення екрану (TCP)";
            this.FormClosing += (s, e) => server?.Stop();
        }

        private void InitializeComponents()
        {
            this.Width = 900;
            this.Height = 750;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;

            Panel controlPanel = new Panel()
            {
                Left = 10, Top = 10, Width = 865, Height = 80,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblPort = new Label() { Text = "Порт:", Left = 10, Top = 15, Width = 40 };
            txtPort = new TextBox() { Text = "9000", Left = 55, Top = 12, Width = 70 };

            var lblInterval = new Label() { Text = "Інтервал (сек):", Left = 135, Top = 15, Width = 90 };
            txtInterval = new TextBox() { Text = "5", Left = 230, Top = 12, Width = 60 };

            btnStart = new Button() { Text = "Запустити сервер", Left = 10, Top = 42, Width = 130 };
            btnStart.Click += (s, e) => StartServer();

            btnStop = new Button() { Text = "Зупинити", Left = 150, Top = 42, Width = 100 };
            btnStop.Enabled = false;
            btnStop.Click += (s, e) => StopServer();

            btnCaptureNow = new Button() { Text = "Знімок зараз", Left = 260, Top = 42, Width = 110 };
            btnCaptureNow.Enabled = false;
            btnCaptureNow.Click += (s, e) => server?.StopCapture();

            btnSaveScreenshot = new Button() { Text = "Зберегти знімок", Left = 380, Top = 42, Width = 130 };
            btnSaveScreenshot.Enabled = false;
            btnSaveScreenshot.Click += (s, e) => SaveScreenshot();

            lblConnectionStatus = new Label()
            {
                Text = "● Не підключено", Left = 520, Top = 15, Width = 200,
                ForeColor = Color.Red, Font = new Font("Arial", 9, FontStyle.Bold)
            };

            lblStatus = new Label()
            {
                Text = "Сервер не запущено", Left = 520, Top = 40, Width = 330,
                ForeColor = Color.Gray
            };

            controlPanel.Controls.AddRange(new Control[] { lblPort, txtPort, lblInterval, txtInterval,
                btnStart, btnStop, btnCaptureNow, btnSaveScreenshot, lblConnectionStatus, lblStatus });

            Panel imagePanel = new Panel()
            {
                Left = 10, Top = 100, Width = 865, Height = 440,
                BorderStyle = BorderStyle.FixedSingle
            };

            pictureBox = new PictureBox()
            {
                Left = 5, Top = 5, Width = 855, Height = 430,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.None
            };

            var lblPlaceholder = new Label()
            {
                Text = "Тут буде відображатися скріншот робочого столу клієнта",
                Left = 250, Top = 200, Width = 400,
                ForeColor = Color.Gray, Font = new Font("Arial", 12)
            };
            pictureBox.Controls.Add(lblPlaceholder);

            imagePanel.Controls.Add(pictureBox);

            Panel logPanel = new Panel()
            {
                Left = 10, Top = 550, Width = 865, Height = 150,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblLog = new Label()
            {
                Text = "Лог подій:", Left = 5, Top = 5, Width = 100,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };

            txtLog = new TextBox()
            {
                Left = 5, Top = 25, Width = 855, Height = 120,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical
            };

            logPanel.Controls.Add(lblLog);
            logPanel.Controls.Add(txtLog);

            this.Controls.Add(controlPanel);
            this.Controls.Add(imagePanel);
            this.Controls.Add(logPanel);
        }

        private void StartServer()
        {
            if (!int.TryParse(txtPort.Text, out int port))
            {
                MessageBox.Show("Невірний порт!");
                return;
            }

            if (!int.TryParse(txtInterval.Text, out int interval))
            {
                MessageBox.Show("Невірний інтервал!");
                return;
            }

            server = new ScreenCaptureServer(port, interval * 1000);
            server.ScreenshotReceived += OnScreenshotReceived;
            server.StatusChanged += OnStatusChanged;
            server.LogUpdated += OnLogUpdated;
            server.Start();

            btnStart.Enabled = false;
            btnStop.Enabled = true;
            btnCaptureNow.Enabled = true;
            txtPort.Enabled = false;
            txtInterval.Enabled = false;
        }

        private void StopServer()
        {
            server?.Stop();
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            btnCaptureNow.Enabled = false;
            btnSaveScreenshot.Enabled = false;
            txtPort.Enabled = true;
            txtInterval.Enabled = true;
            UpdateConnectionStatus(false);
        }

        private void OnScreenshotReceived(Image screenshot)
        {
            if (pictureBox.InvokeRequired)
            {
                pictureBox.Invoke(new Action<Image>(OnScreenshotReceived), screenshot);
                return;
            }

            currentScreenshot?.Dispose();
            currentScreenshot = new Bitmap(screenshot);
            pictureBox.Image = currentScreenshot;

            var placeholder = pictureBox.Controls.Count > 0 ? pictureBox.Controls[0] as Label : null;
            if (placeholder != null)
                placeholder.Visible = false;

            btnSaveScreenshot.Enabled = true;
            UpdateConnectionStatus(true);
        }

        private void OnStatusChanged(string status)
        {
            if (lblStatus.InvokeRequired)
            {
                lblStatus.Invoke(new Action<string>(OnStatusChanged), status);
                return;
            }
            lblStatus.Text = status;
        }

        private void OnLogUpdated(string logEntry)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action<string>(OnLogUpdated), logEntry);
                return;
            }
            txtLog.AppendText(logEntry + "\r\n");
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();
        }

        private void UpdateConnectionStatus(bool connected)
        {
            if (lblConnectionStatus.InvokeRequired)
            {
                lblConnectionStatus.Invoke(new Action<bool>(UpdateConnectionStatus), connected);
                return;
            }

            if (connected)
            {
                lblConnectionStatus.Text = "● Підключено";
                lblConnectionStatus.ForeColor = Color.Green;
            }
            else
            {
                lblConnectionStatus.Text = "● Не підключено";
                lblConnectionStatus.ForeColor = Color.Red;
                btnSaveScreenshot.Enabled = false;
            }
        }

        private void SaveScreenshot()
        {
            if (currentScreenshot == null) return;

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                sfd.FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}";
                sfd.DefaultExt = "png";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    currentScreenshot.Save(sfd.FileName);
                    MessageBox.Show($"Скріншот збережено: {sfd.FileName}", "Збереження", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }

    class ScreenCaptureClient
    {
        private TcpClient tcpClient;
        private NetworkStream stream;
        private bool isRunning;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Console.WriteLine("=== Клієнт захоплення екрану ===");

            string serverIP = "127.0.0.1";
            int port = 9000;

            if (args.Length >= 1)
                serverIP = args[0];
            if (args.Length >= 2)
                int.TryParse(args[1], out port);

            Console.Write($"IP сервера (за замовчуванням {serverIP}): ");
            string ipInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(ipInput))
                serverIP = ipInput;

            Console.Write($"Порт (за замовчуванням {port}): ");
            string portInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(portInput))
                int.TryParse(portInput, out port);

            ScreenCaptureClient client = new ScreenCaptureClient();
            client.ConnectAndRun(serverIP, port);
        }

        public void ConnectAndRun(string serverIP, int port)
        {
            try
            {
                tcpClient = new TcpClient();
                Console.WriteLine($"Підключення до {serverIP}:{port}...");
                tcpClient.Connect(serverIP, port);
                stream = tcpClient.GetStream();
                isRunning = true;

                byte[] buffer = new byte[1024];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                string serverMsg = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (serverMsg.StartsWith("BUSY"))
                {
                    Console.WriteLine($"Сервер зайнятий: {serverMsg.Replace("BUSY|", "")}");
                    return;
                }

                Console.WriteLine("Підключено до сервера. Очікування запитів...");
                Console.WriteLine("Натисніть Ctrl+C для виходу");

                while (isRunning && tcpClient.Connected)
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string command = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (command == "STOP")
                    {
                        Console.WriteLine("Отримано команду зупинки");
                        break;
                    }

                    if (command == "SCREENSHOT")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Запит скріншота...");
                        SendScreenshot();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка: {ex.Message}");
            }
            finally
            {
                isRunning = false;
                stream?.Close();
                tcpClient?.Close();
                Console.WriteLine("Відключено від сервера.");
                Console.WriteLine("Натисніть будь-яку клавішу для виходу...");
                Console.ReadKey();
            }
        }

        private void SendScreenshot()
        {
            try
            {
                byte[] screenshotData = CaptureScreen();

                byte[] sizeBytes = BitConverter.GetBytes(screenshotData.Length);
                stream.Write(sizeBytes, 0, sizeBytes.Length);
                stream.Write(screenshotData, 0, screenshotData.Length);
                stream.Flush();

                Console.WriteLine($"Скріншот надіслано. Розмір: {screenshotData.Length / 1024} KB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка відправки скріншота: {ex.Message}");
            }
        }

        private byte[] CaptureScreen()
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    ImageCodecInfo jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    EncoderParameters encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 70L);

                    bitmap.Save(ms, jpegEncoder, encoderParams);
                    return ms.ToArray();
                }
            }
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ServerForm());
        }
    }
}