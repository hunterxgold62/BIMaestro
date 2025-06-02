using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace IA
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<Message> Messages { get; set; }
        private string lastScreenshotPath = string.Empty;
        private string screenshotDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "RevitDernierScreenIA");
        private Bitmap capturedImage;
        private bool isRequestPending;

        public bool IsRequestPending
        {
            get { return isRequestPending; }
            set
            {
                isRequestPending = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            Messages = new ObservableCollection<Message>();
            DataContext = this;
            Directory.CreateDirectory(screenshotDirectory);
            DeleteOldScreenshots(); // Delete old screenshots on startup
            IsRequestPending = false; // Initialize as false
        }

        private void DeleteOldScreenshots()
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(screenshotDirectory);
                foreach (FileInfo file in di.GetFiles())
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la suppression des anciennes captures d'écran : {ex.Message}");
            }
        }

        private async void TakeScreenshot_Click(object sender, RoutedEventArgs e)
        {
            await Task.Delay(500); // Laissez un léger délai pour vous permettre de déplacer la fenêtre
            CaptureFullScreen();
            ShowRegionSelection();
        }

        private void CaptureFullScreen()
        {
            var screenBounds = Screen.PrimaryScreen.Bounds;
            capturedImage = new Bitmap(screenBounds.Width, screenBounds.Height);

            using (Graphics g = Graphics.FromImage(capturedImage))
            {
                g.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size);
            }

            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            lastScreenshotPath = Path.Combine(screenshotDirectory, $"revit_screenshot_{timestamp}.png");
            capturedImage.Save(lastScreenshotPath, ImageFormat.Png);
        }

        private void ShowRegionSelection()
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = Screen.PrimaryScreen.Bounds,
                TopMost = true,
                Opacity = 0.5,
                BackColor = System.Drawing.Color.Gray
            };

            var selectionRectangle = new System.Windows.Forms.PictureBox
            {
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = System.Drawing.Color.FromArgb(50, 0, 0, 255) // Transparent blue
            };

            form.Controls.Add(selectionRectangle);
            Point startPoint = Point.Empty;

            form.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    startPoint = e.Location;
                    selectionRectangle.Bounds = new System.Drawing.Rectangle(e.Location, Size.Empty);
                }
            };

            form.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    var x = Math.Min(e.X, startPoint.X);
                    var y = Math.Min(e.Y, startPoint.Y);
                    var width = Math.Abs(e.X - startPoint.X);
                    var height = Math.Abs(e.Y - startPoint.Y);
                    selectionRectangle.Bounds = new System.Drawing.Rectangle(x, y, width, height);
                }
            };

            form.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    var selectionBounds = selectionRectangle.Bounds;
                    form.Close();

                    if (selectionBounds.Width > 0 && selectionBounds.Height > 0)
                    {
                        var croppedImage = CropImage(capturedImage, selectionBounds);
                        croppedImage.Save(lastScreenshotPath, ImageFormat.Png);
                        MessageBox.Show("Capture d'écran réalisée avec succès.");
                    }
                }
            };

            form.ShowDialog();
        }

        private Bitmap CropImage(Bitmap source, System.Drawing.Rectangle section)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (section.Width <= 0 || section.Height <= 0)
            {
                throw new ArgumentException("La section spécifiée n'est pas valide.");
            }

            Bitmap bitmap = new Bitmap(section.Width, section.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(source, 0, 0, section, GraphicsUnit.Pixel);
            }
            return bitmap;
        }

        private async void SendRequest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IsRequestPending = true; // Disable the button

                string currentScreenshotPath = lastScreenshotPath;
                if (!File.Exists(currentScreenshotPath))
                {
                    MessageBox.Show("Veuillez d'abord prendre une capture d'écran.");
                    IsRequestPending = false; // Re-enable the button if there's an error
                    return;
                }

                string base64Image = Convert.ToBase64String(File.ReadAllBytes(currentScreenshotPath));
                string userPrompt = InputBox.Text;
                Messages.Add(new Message { Role = "user", Content = userPrompt });

                var result = await SendImageToAPIAsync(base64Image, userPrompt, currentScreenshotPath);
                Messages.Add(new Message { Role = "assistant", Content = result });
                InputBox.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Une erreur s'est produite: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
            finally
            {
                IsRequestPending = false; // Re-enable the button after request completion
            }
        }

        private async Task<string> SendImageToAPIAsync(string base64Image, string userPrompt, string imagePath)
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKeys.OpenAIKey);

                var prompt = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = userPrompt },
                                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } }
                            }
                        }
                    },
                    max_tokens = 2000
                };

                var contentJson = new StringContent(System.Text.Json.JsonSerializer.Serialize(prompt), Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", contentJson);
                    response.EnsureSuccessStatusCode();
                    var responseBody = await response.Content.ReadAsStringAsync();

                    var responseJson = System.Text.Json.JsonDocument.Parse(responseBody);
                    var responseContent = responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    return responseContent;
                }
                catch (HttpRequestException e)
                {
                    return $"Request error: {e.Message}";
                }
                catch (Exception e)
                {
                    return $"Unexpected error: {e.Message}";
                }
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class Message
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }
}

