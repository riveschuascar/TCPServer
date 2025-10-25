using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

class IoTServer
{
    private string host;
    private int port;
    private TcpListener? server;
    private List<TcpClient> actuators;
    private object lockObject;
    
    // System state
    private int[] intervals;
    private int currentInterval; // 0: 0-50, 1: 50-100, 2: >100
    
    public IoTServer(string host = "0.0.0.0", int port = 10000)
    {
        this.host = host;
        this.port = port;
        this.actuators = new List<TcpClient>();
        this.lockObject = new object();
        
        this.intervals = new int[] { 0, 50, 100 };
        this.currentInterval = 0;
        
        Console.WriteLine($"[SERVER] Initializing IoT server at {host}:{port}");
    }
    
    public void Start()
    {
        try
        {
            IPAddress ipAddress = IPAddress.Parse(host);
            server = new TcpListener(ipAddress, port);
            server.Start();
            
            Console.WriteLine($"[SERVER] Server listening at {host}:{port}");
            Console.WriteLine($"[SERVER] Waiting for sensor and actuator connections...\n");
            
            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                string clientAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).ToString();
                Console.WriteLine($"[SERVER] New connection from {clientAddress}");
                
                // Create thread to handle each client
                Thread clientThread = new Thread(() => HandleClient(client, clientAddress));
                clientThread.IsBackground = true;
                clientThread.Start();
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine($"[SERVER] Socket error: {e.Message}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SERVER] Error: {e.Message}");
        }
        finally
        {
            Close();
        }
    }
    
    private void HandleClient(TcpClient client, string address)
    {
        string clientType = "UNKNOWN";
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        
        try
        {
            while (true)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                
                string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"[{address}] Received: {data}");
                
                // Parse message
                try
                {
                    string[] parts = data.Trim().Split(new[] { ' ' }, 2);
                    string command = parts[0];
                    string payload = parts.Length > 1 ? parts[1] : "{}";
                    
                    string response;
                    switch (command)
                    {
                        case "GET":
                            clientType = "SENSOR";
                            response = HandleGet(payload);
                            SendResponse(stream, response);
                            Console.WriteLine($"[{address}] Sent: {response}");
                            break;
                            
                        case "PUT" when clientType != "ACTUATOR":
                            clientType = "SENSOR";
                            response = HandlePutSensor(payload);
                            NotifyActuators();
                            break;
                            
                        case "REGISTER":
                            clientType = "ACTUATOR";
                            RegisterActuator(client, address);
                            response = JsonSerializer.Serialize(new { status = "registered" });
                            SendResponse(stream, response);
                            Console.WriteLine($"[{address}] Actuator registered successfully");
                            break;
                            
                        default:
                            response = JsonSerializer.Serialize(new { error = "Invalid command" });
                            SendResponse(stream, response);
                            break;
                    }
                }
                catch (JsonException)
                {
                    string errorResponse = JsonSerializer.Serialize(new { error = "Invalid JSON format" });
                    SendResponse(stream, errorResponse);
                }
                catch (Exception e)
                {
                    string errorResponse = JsonSerializer.Serialize(new { error = e.Message });
                    SendResponse(stream, errorResponse);
                }
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"[{address}] Connection closed by client");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{address}] Error: {e.Message}");
        }
        finally
        {
            if (clientType == "ACTUATOR")
            {
                UnregisterActuator(client);
            }
            client.Close();
            Console.WriteLine($"[{address}] Connection closed");
        }
    }
    
    private string HandleGet(string jsonData)
    {
        try
        {
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData);
            if (request != null && request.TryGetValue("var", out JsonElement varElement))
            {
                string var = varElement.GetString() ?? "";
                if (var == "intervals")
                {
                    return JsonSerializer.Serialize(new { intervals = intervals });
                }
                return JsonSerializer.Serialize(new { error = $"Unknown variable: {var}" });
            }
            return JsonSerializer.Serialize(new { error = "Invalid request format" });
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message });
        }
    }
    
    private string HandlePutSensor(string jsonData)
    {
        try
        {
            var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData);
            if (request != null && request.TryGetValue("interval", out JsonElement intervalElement))
            {
                int interval = intervalElement.GetInt32();
                if (interval >= 0 && interval <= 2)
                {
                    lock (lockObject)
                    {
                        currentInterval = interval;
                    }
                    Console.WriteLine($"[SERVER] Interval updated: {interval}");
                    
                    string range = interval switch
                    {
                        0 => "0-50",
                        1 => "50-100",
                        _ => ">100"
                    };
                    Console.WriteLine($"[SERVER] Range: {range}");
                    
                    return JsonSerializer.Serialize(new { status = "ok" });
                }
            }
            return JsonSerializer.Serialize(new { error = "Invalid interval. Must be 0, 1, or 2" });
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message });
        }
    }
    
    private void RegisterActuator(TcpClient client, string address)
    {
        lock (lockObject)
        {
            if (!actuators.Contains(client))
            {
                actuators.Add(client);
                Console.WriteLine($"[SERVER] Total registered actuators: {actuators.Count}");
            }
        }
    }
    
    private void UnregisterActuator(TcpClient client)
    {
        lock (lockObject)
        {
            if (actuators.Contains(client))
            {
                actuators.Remove(client);
                Console.WriteLine($"[SERVER] Actuator unregistered. Total: {actuators.Count}");
            }
        }
    }
    
    private void NotifyActuators()
    {
        lock (lockObject)
        {
            int interval = currentInterval;
            
            // Determine which LED to turn on based on interval
            var (led, action) = interval switch
            {
                0 => ("green", "on"),    // 0-50
                1 => ("yellow", "on"),   // 50-100
                _ => ("red", "blink")    // >100
            };
            
            string command = $"PUT {JsonSerializer.Serialize(new { led, action })}";
            
            // Send command to all actuators
            List<TcpClient> disconnected = new List<TcpClient>();
            foreach (var actuator in actuators)
            {
                try
                {
                    var stream = actuator.GetStream();
                    byte[] data = Encoding.UTF8.GetBytes(command);
                    stream.Write(data, 0, data.Length);
                    Console.WriteLine($"[SERVER] Command sent to actuator: {command}");
                }
                catch
                {
                    disconnected.Add(actuator);
                }
            }
            
            // Remove disconnected actuators
            foreach (var actuator in disconnected)
            {
                if (actuators.Contains(actuator))
                {
                    actuators.Remove(actuator);
                }
            }
        }
    }
    
    private void SendResponse(NetworkStream stream, string response)
    {
        byte[] data = Encoding.UTF8.GetBytes(response);
        stream.Write(data, 0, data.Length);
    }
    
    public void SendCommandToActuators(string led, string action)
    {
        string command = $"PUT {JsonSerializer.Serialize(new { led, action })}";
        
        lock (lockObject)
        {
            List<TcpClient> disconnected = new List<TcpClient>();
            foreach (var actuator in actuators)
            {
                try
                {
                    var stream = actuator.GetStream();
                    byte[] data = Encoding.UTF8.GetBytes(command);
                    stream.Write(data, 0, data.Length);
                    Console.WriteLine($"[SERVER] Manual command sent: {command}");
                }
                catch
                {
                    disconnected.Add(actuator);
                }
            }
            
            foreach (var actuator in disconnected)
            {
                if (actuators.Contains(actuator))
                {
                    actuators.Remove(actuator);
                }
            }
        }
    }
    
    public void Close()
    {
        server?.Stop();
        Console.WriteLine("[SERVER] Server closed");
    }
}

class Program
{
    static void Main(string[] args)
    {
        var server = new IoTServer(host: "0.0.0.0", port: 5000);
        server.Start();
    }
}
