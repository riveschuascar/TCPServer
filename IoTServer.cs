using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;
using System.Net.NetworkInformation;

class IoTServer
{
    // TCP/IP
    private readonly string? host;
    private readonly ushort port;
    private TcpListener? server;

    // mDNS
    private MulticastService? mdns;
    private ServiceDiscovery? serviceDiscovery;

    // Sincronización y estado
    private readonly object lockObject = new();
    private byte currentInterval;
    private readonly List<TcpClient> actuators;

    // Variables compartidas
    private readonly int[] intervals = new int[] { 0, 50, 100 };

    public IoTServer(string? host = null, ushort port = 10000)
    {
        this.host = host;
        this.port = port;
        currentInterval = 2;
        actuators = new List<TcpClient>();
        Console.WriteLine($"[SERVER] Initialized IoT binary server at {host ?? "0.0.0.0"}:{port}");
    }

    string GetLocalIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var props = nic.GetIPProperties();

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }
        return "0.0.0.0"; // fallback
    }

    public void Start()
    {
        try
        {
            IPAddress ipAddress = host != null ? IPAddress.Parse(host) : IPAddress.Any;
            server = new TcpListener(ipAddress, port);
            server.Start();

            string localIp = GetLocalIPv4();
            Console.WriteLine($"[SERVER] Listening at {localIp}:{port}");

            // Iniciar mDNS
            StartMdns(localIp);

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                string clientAddr = ((IPEndPoint)client.Client.RemoteEndPoint!).ToString();
                Console.WriteLine($"[SERVER] Connection from {clientAddr}");

                ThreadPool.QueueUserWorkItem(_ => HandleClient(client, clientAddr));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SERVER] Error: {e.Message}");
        }
        finally
        {
            StopMdns();
            Close();
        }
    }

    private void HandleClient(TcpClient client, string address)
    {
        string clientType = "UNKNOWN";
        NetworkStream stream = client.GetStream();
        byte[] header = new byte[2]; // CMD + LEN

        try
        {
            while (true)
            {
                int read = stream.Read(header, 0, 2);
                if (read == 0) break;

                byte cmd = header[0];
                int len = header[1];
                byte[] data = new byte[len];
                if (len > 0) stream.Read(data, 0, len);

                switch (cmd)
                {
                    case 0x01: // PUT interval
                        clientType = "SENSOR";
                        int interval = data[0];
                        HandleInterval(interval);
                        NotifyActuators();
                        break;

                    case 0x02: // GET variable
                        clientType = "SENSOR";
                        byte varId = data[0];
                        HandleGet(varId, stream);
                        break;

                    case 0x03: // REGISTER actuator
                        clientType = "ACTUATOR";
                        RegisterActuator(client, address);
                        break;

                    default:
                        Console.WriteLine($"[{address}] Unknown CMD: {cmd:X2}");
                        break;
                }
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"[{address}] Connection closed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{address}] Error: {ex.Message}");
        }
        finally
        {
            client.Close();
            lock (lockObject)
            {
                actuators.Remove(client);
            }
        }
    }

    private void HandleGet(byte varId, NetworkStream stream)
    {
        switch (varId)
        {
            case 1: // "intervals"
                byte[] response = new byte[2 + intervals.Length * 4]; // 1 byte CMD, 1 byte LEN, data (int32 each)
                response[0] = 0x02; // respuesta tipo GET
                response[1] = (byte)(intervals.Length * 4);
                Buffer.BlockCopy(intervals, 0, response, 2, intervals.Length * 4);
                stream.Write(response, 0, response.Length);
                Console.WriteLine("[SERVER] Sent intervals array to sensor.");
                break;

            default:
                Console.WriteLine($"[SERVER] Unknown variable ID: {varId}");
                break;
        }
    }

    private void HandleInterval(int interval)
    {
        if (interval < 0 || interval > 2)
        {
            Console.WriteLine($"[SERVER] Invalid interval: {interval}");
            return;
        }

        lock (lockObject)
        {
            currentInterval = (byte)interval;
        }

        string range = interval switch
        {
            0 => "0-30",
            1 => "30-60",
            _ => ">60"
        };
        Console.WriteLine($"[SERVER] Interval updated to {interval} ({range})");
    }

    private void RegisterActuator(TcpClient client, string address)
    {
        lock (lockObject)
        {
            if (!actuators.Contains(client))
            {
                actuators.Add(client);
                Console.WriteLine($"[SERVER] Actuator registered from {address}. Total: {actuators.Count}");
            }
        }
    }

    private void NotifyActuators()
    {
        lock (lockObject)
        {
            var (led, action) = currentInterval switch
            {
                0 => (1, 1), // oscilate fast
                1 => (2, 1), // oscilate slow
                _ => (3, 2), // stop
            };

            byte[] packet = { 0x04, 0x02, (byte)led, (byte)action };
            foreach (var actuator in actuators.ToList())
            {
                try
                {
                    actuator.GetStream().Write(packet, 0, packet.Length);
                }
                catch
                {
                    actuators.Remove(actuator);
                }
            }
        }
    }

    private void StartMdns(string localIp)
    {
        try
        {
            mdns = new MulticastService();
            serviceDiscovery = new ServiceDiscovery(mdns);

            // Convertimos la IP string a objeto IPAddress
            var ipAddress = IPAddress.Parse(localIp);

            // Incluir la IP en el anuncio
            var service = new ServiceProfile("iot", "_server._tcp", (ushort)port, new[] { IPAddress.Parse(localIp) });

            serviceDiscovery.Advertise(service);
            mdns.Start();

            Console.WriteLine($"[MDNS] Advertise as 'iot-server.utp.local' in port {port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MDNS] Error: {ex.Message}");
        }
    }

    private void StopMdns()
    {
        try
        {
            serviceDiscovery?.Dispose();
            mdns?.Stop();
            Console.WriteLine("[MDNS] Service stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MDNS] Stop error: {ex.Message}");
        }
    }

    public void Close()
    {
        server?.Stop();
        Console.WriteLine("[SERVER] Server closed");
    }
}
