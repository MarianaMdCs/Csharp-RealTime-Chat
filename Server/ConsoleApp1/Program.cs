using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Data.SQLite;
using System.Security.Cryptography;

class ChatServer
{
    private static List<TcpClient> clients = new List<TcpClient>();
    private static object lockObject = new object();
    private static string dbFile = "chat.db";
    private static Dictionary<TcpClient, string> clientUsers = new Dictionary<TcpClient, string>();
    private static object usersLock = new object();
    private static readonly TimeSpan ConnectionCheckInterval = TimeSpan.FromSeconds(10);
    private static volatile bool isRunning = true;

    static void Main()
    {
        InitializeDatabase();
        TcpListener server = new TcpListener(IPAddress.Any, 5000);
        server.Start();
        Console.WriteLine("Chat Server started...");

        Thread monitorThread = new Thread(MonitorConnections)
        {
            IsBackground = true
        };
        monitorThread.Start();

        while (isRunning)
        {
            try
            {
                TcpClient client = server.AcceptTcpClient();
                client.ReceiveTimeout = 30000; // 30 segundos
                client.SendTimeout = 30000;

                lock (lockObject)
                {
                    clients.Add(client);
                }

                Thread clientThread = new Thread(HandleClient)
                {
                    IsBackground = true
                };
                clientThread.Start(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accepting client: {ex.Message}");
            }
        }
    }

    static void MonitorConnections()
    {
        while (isRunning)
        {
            Thread.Sleep(ConnectionCheckInterval);

            lock (lockObject)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    if (!IsClientConnected(clients[i]))
                    {
                        Console.WriteLine($"Limpiando cliente desconectado");
                        clients[i].Close();
                        clients.RemoveAt(i);
                    }
                }
            }
        }
    }

    static bool IsClientConnected(TcpClient client)
    {
        try
        {
            if (client == null || !client.Connected) return false;

            // Prueba de conexión con un ping
            return !(client.Client.Poll(1, SelectMode.SelectRead) &&
                   client.Client.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    static void InitializeDatabase()
    {
        if (!File.Exists(dbFile))
        {
            SQLiteConnection.CreateFile(dbFile);
        }
        using (var conn = new SQLiteConnection("Data Source=" + dbFile))
        {
            conn.Open();
            string createUsersTable = "CREATE TABLE IF NOT EXISTS Users (Id INTEGER PRIMARY KEY, Username TEXT, Password TEXT);";
            string createMessagesTable = "CREATE TABLE IF NOT EXISTS Messages (Id INTEGER PRIMARY KEY, Sender TEXT, Message TEXT, Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP);";
            using (var cmd = new SQLiteCommand(createUsersTable, conn))
            {
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new SQLiteCommand(createMessagesTable, conn))
            {
                cmd.ExecuteNonQuery();
            }
        }
    }

    static void HandleClient(object obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));

        TcpClient client = (TcpClient)obj;
        client.ReceiveTimeout = 0; // Deshabilitar timeout
        client.SendTimeout = 0;
        NetworkStream stream = client.GetStream();
        string currentUser = null;
        byte[] buffer = new byte[4 * 1024 * 1024]; // Buffer de 4MB

        try
        {
            // Configurar KeepAlive
            SetKeepAlive(client.Client, 0, 0);

            // Fase de autenticación
            int authBytesRead = stream.Read(buffer, 0, buffer.Length);
            if (authBytesRead == 0) return;

            string authData = Encoding.UTF8.GetString(buffer, 0, authBytesRead);
            var authParts = authData.Split('|');

            if (authParts.Length >= 3 && authParts[0] == "AUTH")
            {
                string username = authParts[1];
                string password = authParts[2];

                if (AuthenticateUser(username, password) || RegisterUser(username, password))
                {
                    lock (usersLock)
                    {
                        clientUsers[client] = username;
                    }
                    currentUser = username;
                    SendMessage(client, "AUTH|SUCCESS");
                    SendHistory(client);
                    SendUserList();
                    BroadcastMessage($"SYSTEM|server|{username} se ha unido al chat", null);
                }
                else
                {
                    SendMessage(client, "AUTH|FAILURE|Credenciales inválidas o usuario ya existe");
                    client.Close();
                    return;
                }
            }

            // Fase de chat principal
            while (true)
            {
                try
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // Conexión cerrada por cliente

                    ProcessClientMessage(buffer, bytesRead, client);
                }
                catch (IOException)
                {
                    break; ; // Reintentar si es timeout
                }
            }
        }
        finally
        {
            CleanupClient(client, currentUser);
        }
    }

    static void ProcessClientMessage(byte[] buffer, int bytesRead, TcpClient sender)
    {
        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        Console.WriteLine($"Mensaje recibido: {message.Substring(0, Math.Min(50, message.Length))}...");

        var parts = message.Split('|');
        if (parts.Length < 4) return;

        string type = parts[0];
        string senderName = parts[1];
        string recipient = parts[2];

        if (type == "TEXT" && parts.Length >= 4)
        {
            string text = parts[3];
            SaveMessage(senderName, recipient == "ALL" ? text : $"(privado a {recipient}) {text}");
            BroadcastMessage(message, sender);
        }
        else if (type == "IMAGE" && parts.Length >= 6)
        {
            if (parts[3].Length > 4 * 1024 * 1024)
            {
                SendMessage(sender, "ERROR|server|Imagen demasiado grande (máximo 4MB)");
                return;
            }

            string fullMessage = $"IMAGE|{senderName}|{recipient}|{parts[3]}|{parts[4]}|{parts[5]}";
            SaveMessage(senderName, recipient == "ALL" ? $"[Imagen:{parts[5]}]" : $"(privado a {recipient}) [Imagen: {parts[5]}]");
            BroadcastMessage(fullMessage, sender);
        }
    }

    static void SetKeepAlive(Socket socket, int keepAliveTime, int keepAliveInterval)
    {
        try
        {
            // Configuración mínima para mantener conexión activa
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // Solo configurar valores si son mayores que 0
            if (keepAliveTime > 0 && keepAliveInterval > 0)
            {
                byte[] inValue = new byte[12];
                BitConverter.GetBytes(1).CopyTo(inValue, 0);
                BitConverter.GetBytes(keepAliveTime).CopyTo(inValue, 4);
                BitConverter.GetBytes(keepAliveInterval).CopyTo(inValue, 8);
                socket.IOControl(IOControlCode.KeepAliveValues, inValue, null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"KeepAlive config error: {ex.Message}");
        }
    }


    static void CleanupClient(TcpClient client, string username)
    {
        try
        {
            if (username != null)
            {
                lock (usersLock)
                {
                    clientUsers.Remove(client);
                }
                BroadcastMessage($"SYSTEM|server|{username} ha abandonado el chat", null);

                // Enviar lista de usuarios actualizada
                SendUserList();
            }

            lock (lockObject)
            {
                clients.Remove(client);
            }

            client.Close();
            Console.WriteLine($"Conexión cerrada correctamente para {username ?? "usuario desconocido"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en limpieza de cliente: {ex.Message}");
        }
    }


    static void SendUserList()
    {
        lock (usersLock)
        {
            List<string> validUsers = new List<string>();
            lock (lockObject)
            {
                // Obtener todos los usuarios conectados
                foreach (var userEntry in clientUsers)
                {
                    string user = userEntry.Value;
                    if (!string.IsNullOrEmpty(user) &&
                        !user.Contains("|") &&
                        !user.Contains(":"))
                    {
                        validUsers.Add(user);
                    }
                }
            }

            // Crear mensaje de lista de usuarios
            string userListMessage = "USERLIST|server|" + string.Join("|", validUsers);

            // Enviar a todos los clientes conectados
            lock (lockObject)
            {
                foreach (var client in clients.ToList())
                {
                    try
                    {
                        if (client.Connected)
                        {
                            SendMessage(client, userListMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error enviando lista de usuarios: {ex.Message}");
                    }
                }
            }
        }
    }



    static bool RegisterUser(string username, string password)
    {
        if (UserExists(username))
        {
            Console.WriteLine($"[Servidor] El usuario {username} ya existe.");
            return false;  // El usuario ya existe
        }

        string hashedPassword = HashPassword(password);  // Se debe hashear la contraseña
        Console.WriteLine($"[Servidor] Registrando usuario con contraseña hasheada: {hashedPassword}");

        try
        {
            using (var conn = new SQLiteConnection("Data Source=" + dbFile))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("INSERT INTO Users (Username, Password) VALUES (@username, @password)", conn))
                {
                    cmd.Parameters.AddWithValue("@username", username);
                    cmd.Parameters.AddWithValue("@password", hashedPassword);
                    cmd.ExecuteNonQuery();
                    Console.WriteLine($"[Servidor] Usuario {username} registrado con éxito.");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error al registrar el usuario: " + ex.Message);
            return false;
        }
    }


    static bool UserExists(string username)
    {
        using (var conn = new SQLiteConnection("Data Source=" + dbFile))
        {
            conn.Open();
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM Users WHERE Username = @username", conn))
            {
                cmd.Parameters.AddWithValue("@username", username);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }
    }

    static void SendMessage(TcpClient client, string message)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        stream.Write(buffer, 0, buffer.Length);
    }



    static bool AuthenticateUser(string username, string password)
    {
        string hashedPassword = HashPassword(password);  // Hash de la contraseña ingresada
        Console.WriteLine($"[Servidor] Hash de la contraseña ingresada: {hashedPassword}");

        using (var conn = new SQLiteConnection("Data Source=" + dbFile))
        {
            conn.Open();
            using (var cmd = new SQLiteCommand("SELECT Password FROM Users WHERE Username = @username", conn))
            {
                cmd.Parameters.AddWithValue("@username", username);
                var storedPasswordHash = cmd.ExecuteScalar()?.ToString();

                Console.WriteLine($"[Servidor] Contraseña almacenada en la base de datos: {storedPasswordHash}");

                return storedPasswordHash != null && storedPasswordHash == hashedPassword;  // Comparar los hashes
            }
        }
    }



    static string HashPassword(string password)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }

    static void SaveMessage(string sender, string message)
    {
        try
        {
            using (var conn = new SQLiteConnection("Data Source=" + dbFile))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand(
                    "INSERT INTO Messages (Sender, Message, Timestamp) VALUES (@sender, @message, datetime('now'))",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@sender", sender);
                    cmd.Parameters.AddWithValue("@message", message);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving message: {ex.Message}");
        }
    }

    static void SendHistory(TcpClient client)
    {
        try
        {
            string currentUser = clientUsers[client];

            using (var conn = new SQLiteConnection("Data Source=" + dbFile))
            {
                conn.Open();
                // Solo enviar mensajes públicos o privados donde el usuario actual sea remitente o destinatario
                string query = @"SELECT Sender, Message, Timestamp FROM Messages 
                            WHERE Message NOT LIKE '(privado a %' 
                            OR Sender = @currentUser
                            OR (Message LIKE '(privado a %' AND 
                                (Sender = @currentUser OR 
                                 Message LIKE '(privado a ' || @currentUser || ')%'))
                            ORDER BY Timestamp ASC";

                using (var cmd = new SQLiteCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@currentUser", currentUser);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string sender = reader["Sender"].ToString();
                            string message = reader["Message"].ToString();
                            string timestamp = Convert.ToDateTime(reader["Timestamp"]).ToString("HH:mm:ss");

                            string historyMessage = $"HISTORY|{sender}|{timestamp}|{message}";
                            SendMessage(client, historyMessage + "\n");
                            Thread.Sleep(10);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending history: {ex.Message}");
        }
    }

    static void BroadcastMessage(string message, TcpClient sender, bool forceSendToAll = false)
    {
        var parts = message.Split('|');
        if (parts.Length < 4) return;

        string type = parts[0];
        string senderName = parts[1];
        string recipient = parts[2];
        string content = parts[3];

        byte[] buffer = Encoding.UTF8.GetBytes(message);

        lock (lockObject)
        {
            foreach (var client in clients.ToList())
            {
                try
                {
                    bool shouldSend = false;

                    // Si es mensaje público o forzado para todos
                    if (forceSendToAll || recipient == "ALL")
                    {
                        shouldSend = true;
                    }
                    // Si es mensaje privado
                    else
                    {
                        // Verificar si el cliente actual es el remitente o el destinatario
                        if (clientUsers.TryGetValue(client, out string username))
                        {
                            shouldSend = username == recipient || username == senderName;
                        }
                    }

                    if (shouldSend && client.Connected)
                    {
                        client.GetStream().Write(buffer, 0, buffer.Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error en broadcast: {ex.Message}");
                    CleanupClient(client, clientUsers.ContainsKey(client) ? clientUsers[client] : null);
                }
            }
        }
    }

    static void BroadcastImage(byte[] imageBytes, TcpClient sender)
    {
        lock (lockObject)
        {
            foreach (var client in clients)
            {
                if (client != sender)
                {
                    NetworkStream stream = client.GetStream();
                    stream.Write(imageBytes, 0, imageBytes.Length);
                }
            }
        }
    }


}
