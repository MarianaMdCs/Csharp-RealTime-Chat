using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient client;
        private NetworkStream stream;
        private Thread receiveThread;
        private string username;
        private CancellationTokenSource cancellationTokenSource;
        private List<string> onlineUsers = new List<string>();
        private volatile bool isConnected = false;
        private DateTime lastReceivedTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Inicializar el RichTextBox correctamente
            TxtChat.Document.Blocks.Clear();
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string user = TxtUsername.Text.Trim();
            string pass = TxtPassword.Password.Trim();

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                LoginMessage.Text = "Por favor, ingresa usuario y contraseña.";
                LoginMessage.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                ConnectToServer();

                string loginMessage = $"AUTH|{user}|{pass}";
                SendMessage(loginMessage);
                Console.WriteLine($"[Cliente] Enviando mensaje: {loginMessage}");

                byte[] buffer = new byte[2048];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"[Cliente] Respuesta recibida: {response}");

                if (response == "AUTH|SUCCESS")
                {
                    username = user;
                    SwitchToChatPanel();
                    StartReceiving();
                }
                else if (response.StartsWith("AUTH|FAILURE"))
                {
                    LoginMessage.Text = "Error en la autenticación. " + (response.Split('|').Length > 2 ? response.Split('|')[2] : "");
                    LoginMessage.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al conectar con el servidor: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConnectToServer()
        {
            try
            {
                client = new TcpClient();
                SetClientKeepAlive(client); // Configuración simple

                // Conexión sin timeout de operación
                client.Connect("127.0.0.1", 5000);
                stream = client.GetStream();
                isConnected = true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"Error de conexión: {ex.Message}", "Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error));
                throw;
            }
        }

        private void SetClientKeepAlive(TcpClient client)
        {
            try
            {
                // Solo activar KeepAlive sin parámetros de timeout
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch
            {
                // Ignorar errores
            }
        }

        private void SwitchToChatPanel()
        {
            // Limpiar el chat al cambiar de panel
            TxtChat.Document.Blocks.Clear();

            LoginPanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Conectado como " + username;

            // Forzar un renderizado para asegurar que está listo
            TxtChat.UpdateLayout();
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtMessage.Text)) return;

            string recipient = "ALL";
            if (CmbRecipients.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                recipient = selected.Tag.ToString();
            }

            string message = $"TEXT|{username}|{recipient}|{TxtMessage.Text.Trim()}";
            await SendMessageAsync(message);
            TxtMessage.Clear();
        }

        private async void BtnSendImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Imágenes (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
                Title = "Seleccionar imagen para enviar",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    BtnSendImage.IsEnabled = false;
                    StatusText.Text = "Procesando imagen...";

                    // Leer imagen como bytes
                    byte[] imageBytes = await Task.Run(() => File.ReadAllBytes(dialog.FileName));

                    // Verificar tamaño (máximo 4MB)
                    if (imageBytes.Length > 4 * 1024 * 1024)
                    {
                        MessageBox.Show("La imagen es demasiado grande (máximo 4MB permitido)", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Convertir a Base64
                    string base64Image = Convert.ToBase64String(imageBytes);
                    string mimeType = GetMimeType(dialog.FileName);
                    string fileName = Path.GetFileName(dialog.FileName);

                    // Construir mensaje
                    string recipient = "ALL";
                    if (CmbRecipients.SelectedItem is ComboBoxItem selectedItem && selectedItem.Content.ToString() != "Todos")
                    {
                        recipient = selectedItem.Content.ToString();
                    }

                    string message = $"IMAGE|{username}|{recipient}|{base64Image}|{mimeType}|{fileName}";

                    DisplayLocalImage(username, fileName, base64Image, recipient);

                    await SendMessageAsync(message);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al enviar imagen: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnSendImage.IsEnabled = true;
                    StatusText.Text = "Conectado como " + username;
                }
            }
        }

        private void DisplayLocalMessage(string sender, string message, string recipient = "ALL")
        {
            Dispatcher.Invoke(() =>
            {
                var paragraph = new Paragraph();
                string displayText = $"{sender}";

                if (recipient != "ALL")
                {
                    displayText += $" (privado a {recipient})";
                }

                displayText += $": {message}";

                paragraph.Inlines.Add(new Run(displayText));
                TxtChat.Document.Blocks.Add(paragraph);
                TxtChat.ScrollToEnd();
            });
        }

        private void DisplayLocalImage(string sender, string fileName, string base64Image, string recipient = "ALL")
        {
            Dispatcher.Invoke(() =>
            {
                var paragraph = new Paragraph();

                // Construir el texto del mensaje
                string displayText = $"{sender}";

                if (recipient != "ALL")
                {
                    displayText += $" (privado a {recipient})";
                }

                displayText += $": [Imagen: {fileName}]";

                paragraph.Inlines.Add(new Run(displayText));

                try
                {
                    if (string.IsNullOrWhiteSpace(base64Image) ||
                base64Image.Length < 100) // Mínimo tamaño para ser una imagen válida
                    {
                        throw new ArgumentException("Datos de imagen no válidos");
                    }
                    // Convertir Base64 a bytes de imagen
                    byte[] imageBytes = Convert.FromBase64String(base64Image);
                    var bitmapImage = new BitmapImage();

                    using (var stream = new MemoryStream(imageBytes))
                    {
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = stream;
                        bitmapImage.EndInit();
                    }

                    // Crear control de imagen
                    var imageControl = new Image
                    {
                        Source = bitmapImage,
                        MaxWidth = 400,
                        MaxHeight = 400,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 5, 0, 5)
                    };

                    // Agregar saltos de línea y la imagen
                    paragraph.Inlines.Add(new LineBreak());
                    paragraph.Inlines.Add(new LineBreak());
                    paragraph.Inlines.Add(new InlineUIContainer(imageControl));
                }
                catch (Exception ex)
                {
                    paragraph.Inlines.Add(new LineBreak());
                    paragraph.Inlines.Add(new Run("[Error al cargar imagen: " + ex.Message + "]")
                    {
                        Foreground = Brushes.Red
                    });
                }

                // Agregar el párrafo al RichTextBox
                TxtChat.Document.Blocks.Add(paragraph);
                TxtChat.ScrollToEnd();
            });
        }

        private async Task SendMessageAsync(string message)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"Error al enviar: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        private string GetMimeType(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLower();
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                default:
                    throw new NotSupportedException("Formato de imagen no soportado");
            }
        }

        private void StartReceiving()
        {
            receiveThread = new Thread(() =>
            {
                byte[] buffer = new byte[4_194_304];
                try
                {
                    while (isConnected && client != null && client.Connected)
                    {
                        try
                        {
                            // Lectura sin timeout
                            int bytesRead = stream.Read(buffer, 0, buffer.Length);
                            if (bytesRead == 0) break; // Conexión cerrada por servidor

                            lastReceivedTime = DateTime.Now;
                            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                            // Ignorar mensajes PING si los hay
                            if (message == "PING|") continue;

                            ProcessReceivedMessage(message);
                        }
                        catch (IOException)
                        {
                            HandleDisconnection("Conexión perdida con el servidor");
                            break;
                        }
                    }
                }
                finally
                {
                    HandleDisconnection("Desconectado del servidor");
                }
            })
            {
                IsBackground = true
            };
            receiveThread.Start();
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectToServer();
                string loginMessage = $"AUTH|{username}|{TxtPassword.Password}";
                SendMessage(loginMessage);
                StatusText.Text = "Reconectando...";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al reconectar: {ex.Message}");
            }
        }

        private void HandleDisconnection(string message)
        {
            if (!isConnected) return;

            isConnected = false;

            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                MessageBox.Show(message, "Error de conexión",
                              MessageBoxButton.OK, MessageBoxImage.Warning);

                // Restablecer UI
                ChatPanel.Visibility = Visibility.Collapsed;
                LoginPanel.Visibility = Visibility.Visible;
            });

            try
            {
                receiveThread?.Join(1000);
                client?.Close();
            }
            catch { }
        }

        private void ProcessReceivedMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var messages = message.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var msg in messages)
            {
                var parts = msg.Split('|');
                if (parts.Length < 3) continue;
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (parts[0] == "USERLIST")
                        {
                            if (parts.Length >= 2 && parts[1] == "server")
                            {
                                UpdateOnlineUsersList(msg);
                            }
                        }
                        else if (parts[0] == "HISTORY" && parts.Length >= 4)
                        {
                            string displayText = $"[{parts[2]}] {parts[1]}: {parts[3]}";
                            AddMessageToChat(displayText, Brushes.Gray, true);
                        }
                        else if (parts[0] == "USERJOIN")
                        {
                            AddMessageToChat($"[Sistema] {parts[1]} se ha conectado");
                            return;
                        }
                        else if (parts[0] == "USERLEAVE")
                        {
                            AddMessageToChat($"[Sistema] {parts[1]} se ha desconectado");
                            return;
                        }
                        var paragraph = new Paragraph { Margin = new Thickness(0, 5, 0, 5) };

                        if (parts[0] == "TEXT")
                        {
                            string sender = parts[1];
                            string recipient = parts[2];
                            string content = parts[3];

                            // Mostrar solo si es público, o si soy el remitente o destinatario
                            if (recipient == "ALL" || sender == username || recipient == username)
                            {
                                string displayText = $"{sender}: {content}";
                                if (recipient != "ALL")
                                {
                                    displayText += " (privado)";
                                }
                                AddMessageToChat(displayText);
                            }
                        }
                        else if (parts[0] == "IMAGE" && parts.Length >= 6)
                        {
                            string sender = parts[1];
                            string recipient = parts[2];
                            string fileName = parts[5];
                            if (recipient == "ALL" || sender == username || recipient == username)
                            {
                                string displayText = $"{sender} envió una imagen: {fileName}";
                                if (recipient != "ALL")
                                {
                                    displayText += " (privado)";
                                }

                                try
                                {
                                    byte[] imageBytes = Convert.FromBase64String(parts[3]);
                                    var bitmapImage = new BitmapImage();
                                    using (var ms = new MemoryStream(imageBytes))
                                    {
                                        bitmapImage.BeginInit();
                                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                        bitmapImage.StreamSource = ms;
                                        bitmapImage.EndInit();
                                    }

                                    var imageControl = new Image
                                    {
                                        Source = bitmapImage,
                                        MaxWidth = 400,
                                        MaxHeight = 400,
                                        Stretch = Stretch.Uniform,
                                        Margin = new Thickness(0, 5, 0, 5)
                                    };

                                    paragraph.Inlines.Add(new LineBreak());
                                    paragraph.Inlines.Add(new InlineUIContainer(imageControl));
                                }
                                catch (Exception ex)
                                {
                                    paragraph.Inlines.Add(new LineBreak());
                                    paragraph.Inlines.Add(new Run($"[Error al cargar imagen: {ex.Message}]")
                                    {
                                        Foreground = Brushes.Red
                                    });
                                }
                            }
                            else if (parts[0] == "SYSTEM")
                            {
                                AddMessageToChat($"[Sistema] {parts[2]}", Brushes.Blue);
                            }
                            if (parts[0] != "USERLIST" && parts[0] != "USERJOIN" && parts[0] != "USERLEAVE")
                            {
                                TxtChat.Document.Blocks.Add(paragraph);
                                TxtChat.ScrollToEnd();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing message: {ex}");
                        // Opcional: Mostrar error en la UI
                        AddMessageToChat($"[Error procesando mensaje: {ex.Message}]");
                    }
                });
            }
        }

        private void AddMessageToChat(string message, Brush color = null, bool isItalic = false)
        {
            Dispatcher.Invoke(() =>
            {
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var run = new Run(message)
                {
                    Foreground = color ?? Brushes.Black,
                    FontStyle = isItalic ? FontStyles.Italic : FontStyles.Normal
                };

                paragraph.Inlines.Add(run);
                TxtChat.Document.Blocks.Add(paragraph);

                // Auto-scroll solo si está cerca del final
                if (TxtChat.VerticalOffset + TxtChat.ViewportHeight >= TxtChat.ExtentHeight - 50)
                {
                    TxtChat.ScrollToEnd();
                }
            });
        }

        private void UpdateOnlineUsersList(string message)
        {
            var parts = message.Split('|');
            if (parts.Length < 3 || parts[0] != "USERLIST" || parts[1] != "server") return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Guardar selección actual
                    var currentSelection = CmbRecipients.SelectedItem as ComboBoxItem;
                    string currentTag = currentSelection?.Tag?.ToString();

                    CmbRecipients.Items.Clear();

                    // Añadir siempre la opción "Todos"
                    var allItem = new ComboBoxItem
                    {
                        Content = "Todos",
                        Tag = "ALL",
                        IsSelected = currentTag == "ALL" || string.IsNullOrEmpty(currentTag)
                    };
                    CmbRecipients.Items.Add(allItem);

                    // Procesar lista de usuarios
                    for (int i = 2; i < parts.Length; i++)
                    {
                        string user = parts[i];
                        if (!string.IsNullOrEmpty(user) && user != username)
                        {
                            var item = new ComboBoxItem
                            {
                                Content = user,
                                Tag = user,
                                IsSelected = user == currentTag
                            };
                            CmbRecipients.Items.Add(item);
                        }
                    }

                    // Restaurar selección si no se encontró
                    if (CmbRecipients.SelectedItem == null && !string.IsNullOrEmpty(currentTag))
                    {
                        foreach (ComboBoxItem item in CmbRecipients.Items)
                        {
                            if (item.Tag.ToString() == currentTag)
                            {
                                CmbRecipients.SelectedItem = item;
                                break;
                            }
                        }
                    }

                    // Si no hay selección, seleccionar "Todos" por defecto
                    if (CmbRecipients.SelectedItem == null)
                    {
                        CmbRecipients.SelectedItem = allItem;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error actualizando lista de usuarios: {ex.Message}");
                }
            });
        }

        private void SendMessage(string message)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);

                // Mostrar estado de envío
                Dispatcher.Invoke(() => StatusText.Text = "Enviando mensaje...");

                stream.Write(buffer, 0, buffer.Length);

                Dispatcher.Invoke(() => StatusText.Text = "Conectado como " + username);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Error al enviar: {ex.Message}");
                    StatusText.Text = "Error de conexión";
                });
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (client != null && client.Connected)
                {
                    SendMessage($"SYSTEM|{username}|Ha abandonado el chat");
                }

                cancellationTokenSource?.Cancel();
                receiveThread?.Join(1000); // Esperar máximo 1 segundo
                client?.Close();
            }
            catch { }
        }

        private void TxtMessage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(TxtMessage.Text))
            {
                BtnSend_Click(sender, e);
            }
        }
    }
}
