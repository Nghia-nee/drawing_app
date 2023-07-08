using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Server
{
    internal static class Server
    {
        public static string connectionString = @"Data Source=DESKTOP-SUM4876;Initial Catalog=Drawing_app;Integrated Security=True";
        static TcpListener listener;
        static List<TcpClient> clients = new List<TcpClient>();
        static Dictionary<string, List<Tuple<TcpClient, Bitmap>>> rooms = new Dictionary<string, List<Tuple<TcpClient, Bitmap>>>();

        static void Main()
        {
            string serverIP = "192.168.111.1"; // Địa chỉ IP của server
            int serverPort = 2023; // Cổng kết nối của server

            IPAddress ipAddress = IPAddress.Parse(serverIP);
            IPEndPoint ipEndPoint = new IPEndPoint(ipAddress, serverPort);

            listener = new TcpListener(ipEndPoint);
            listener.Start();

            Console.WriteLine("Server started. Waiting for connections...");

            // Tạo một luồng riêng để lắng nghe kết nối từ client
            Thread listenerThread = new Thread(ListenForClients);
            listenerThread.Start();

            while (true)
            {
                // Chờ một khoảng thời gian nhỏ trước khi chấp nhận kết nối tiếp theo
                Thread.Sleep(100);
            }
        }

        private static void ListenForClients()
        {
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                clients.Add(client);
                Console.WriteLine("Client connected.");

                // Tạo một luồng mới để xử lý kết nối từ client
                Thread clientThread = new Thread(() => HandleClient(client));
                clientThread.Start();
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                while (true)
                {
                    // Nhận dữ liệu từ client
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    Console.WriteLine("Received request: " + request);

                    // Phân tích yêu cầu từ client
                    string[] requestData = request.Split(',');
                    string requestType = requestData[0];

                    // Xử lý yêu cầu dựa vào loại
                    string response = "";

                    if (requestType == "sign_in")
                    {
                        // Xử lý đăng nhập
                        string username = requestData[1];
                        string password = requestData[2];
                        bool isAuthenticated = CheckLogin(username, password);
                        response = isAuthenticated ? "success" : "failure";
                    }
                    else if (requestType == "sign_up")
                    {
                        // Xử lý đăng ký
                        string username = requestData[1];
                        string password = requestData[2];
                        bool isRegistered = CheckRegistration(username);

                        if (isRegistered)
                        {
                            response = "failure";
                        }
                        else
                        {
                            RegisterUser(username, password);
                            response = "success";
                        }
                    }
                    else if (requestType == "create_room")
                    {
                        // Tạo phòng mới và cung cấp mã code đại diện
                        string roomCode = GenerateCode();
                        CreateRoom(roomCode, client);
                        response = roomCode;
                    }
                    else if (requestType == "join_room")
                    {
                        // Tham gia phòng
                        string roomCode = requestData[1];
                        bool joined = JoinRoom(roomCode, client);
                        response = joined ? "success" : "failure";
                    }
                    else if (requestType == "disconnect")
                    {
                        // Ngắt kết nối với client nếu yêu cầu là "disconnect"
                        break;
                    }
                    else if (requestType == "update_room")
                    {
                        // Cập nhật dữ liệu phòng
                        string roomCode = requestData[1];
                        string data = requestData[2];
                        UpdateRoomData(roomCode, data, client);
                        response = "success";
                    }
                    else
                    {
                        // Yêu cầu không hợp lệ
                        response = "invalid";
                    }

                    // Gửi phản hồi đến client
                    byte[] responseData = Encoding.UTF8.GetBytes(response);
                    stream.Write(responseData, 0, responseData.Length);

                    // Gửi sự thay đổi đến các client khác trong cùng phòng
                    string roomID = GetRoomIDByClient(client);
                    if (!string.IsNullOrEmpty(roomID))
                    {
                        SendRoomUpdate(roomID, response);
                    }
                }
            }
            catch
            {
                Console.WriteLine("Error occurred");
            }
            finally
            {
                // Xóa client đã hoàn thành kết nối khỏi danh sách clients
                clients.Remove(client);
                client.Close();

                // Kiểm tra và xóa dữ liệu khi có kết nối bị ngắt
                string roomID = GetRoomIDByClient(client);
                if (!string.IsNullOrEmpty(roomID))
                {
                    int clientCount = GetRoomClientCount(roomID);
                    if (clientCount < 1)
                    {
                        DeleteRoomData(roomID);
                    }
                }
            }
        }

        private static void CreateRoom(string roomID, TcpClient client)
        {
            rooms.Add(roomID, new List<Tuple<TcpClient, Bitmap>> { Tuple.Create(client, (Bitmap)null) });
        }

        private static bool JoinRoom(string roomID, TcpClient client)
        {
            lock (rooms)
            {
                if (rooms.ContainsKey(roomID))
                {
                    var roomClients = rooms[roomID];

                    if (!roomClients.Any(tuple => tuple.Item1 == client))
                    {
                        roomClients.Add(Tuple.Create(client, (Bitmap)null));
                    }

                    return true;
                }

                return false;
            }
        }


        private static void UpdateRoomData(string roomID, string data, TcpClient client)
        {
            lock (rooms)
            {
                if (rooms.ContainsKey(roomID))
                {
                    List<Tuple<TcpClient, Bitmap>> roomClients = rooms[roomID];
                    var clientTuple = roomClients.FirstOrDefault(tuple => tuple.Item1 == client);

                    if (clientTuple != null)
                    {
                        int clientIndex = roomClients.IndexOf(clientTuple);
                        roomClients[clientIndex] = Tuple.Create(client, ConvertToBitmap(data));
                    }
                }
            }
        }

        private static void SendRoomUpdate(string roomID, string updateData)
        {
            lock (rooms)
            {
                if (rooms.ContainsKey(roomID))
                {
                    List<Tuple<TcpClient, Bitmap>> roomClients = rooms[roomID];
                    byte[] updateBytes = Encoding.UTF8.GetBytes(updateData);

                    foreach (var clientTuple in roomClients)
                    {
                        TcpClient client = clientTuple.Item1;
                        NetworkStream stream = client.GetStream();
                        stream.Write(updateBytes, 0, updateBytes.Length);
                    }
                }
            }
        }

        private static string GetRoomIDByClient(TcpClient client)
        {
            lock (rooms)
            {
                foreach (var roomPair in rooms)
                {
                    List<Tuple<TcpClient, Bitmap>> roomClients = roomPair.Value;

                    if (roomClients.Any(tuple => tuple.Item1 == client))
                    {
                        return roomPair.Key;
                    }
                }
            }

            return null;
        }

        private static int GetRoomClientCount(string roomID)
        {
            lock (rooms)
            {
                if (rooms.ContainsKey(roomID))
                {
                    return rooms[roomID].Count;
                }
            }

            return 0;
        }

        private static void DeleteRoomData(string roomID)
        {
            lock (rooms)
            {
                if (rooms.ContainsKey(roomID))
                {
                    rooms.Remove(roomID);
                }
            }
        }

        private static bool CheckLogin(string username, string password)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM UserTable WHERE Username=@username AND Password=@password", connection);
                command.Parameters.AddWithValue("@username", username);
                command.Parameters.AddWithValue("@password", password);
                int count = (int)command.ExecuteScalar();
                return count > 0;
            }
        }

        private static bool CheckRegistration(string username)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM UserTable WHERE Username=@username", connection);
                command.Parameters.AddWithValue("@username", username);
                int count = (int)command.ExecuteScalar();   
                return count > 0;
            }
        }

        private static void RegisterUser(string username, string password)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                SqlCommand command = new SqlCommand("INSERT INTO UserTable (Username, Password) VALUES (@username, @password)", connection);
                command.Parameters.AddWithValue("@username", username);
                command.Parameters.AddWithValue("@password", password);
                command.ExecuteNonQuery();
            }
        }

        private static string GenerateCode()
        {
            Random random = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private static Bitmap ConvertToBitmap(string data)
        {
            byte[] bytes = Convert.FromBase64String(data);
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                return new Bitmap(stream);
            }
        }
    }
}
