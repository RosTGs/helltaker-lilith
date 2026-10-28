using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelltakerAILab
{
    internal sealed class Request
    {
        public JObject Body;
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public JObject Result;
        public volatile bool Cancelled;
        public void Complete(JObject result) { Result = result; Done.Set(); }
    }

    internal sealed class BridgeServer : IDisposable
    {
        public readonly ConcurrentQueue<Request> Queue = new ConcurrentQueue<Request>();
        readonly TcpListener listener;
        readonly string token;
        volatile bool running = true;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public BridgeServer(int port, string token)
        {
            this.token = token;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(8);
            new Thread(Accept) { IsBackground = true, Name = "HelltakerAILab bridge" }.Start();
        }
        void Accept()
        {
            while (running)
            {
                try { var client = listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(_ => Handle(client)); }
                catch (SocketException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                try
                {
                    var stream = client.GetStream();
                    var bytes = new MemoryStream();
                    int b;
                    while ((b = stream.ReadByte()) >= 0 && b != 10)
                    {
                        if (bytes.Length >= 65536) throw new InvalidDataException("Request limit: 64 KiB");
                        bytes.WriteByte((byte)b);
                    }
                    var body = JObject.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
                    JObject response;
                    if ((string)body["token"] != token) response = Error("authentication", "Use the token from this game instance's connection.json");
                    else if (Queue.Count >= 8) response = Error("busy", "Retry after the current operation completes");
                    else
                    {
                        var request = new Request { Body = body };
                        Queue.Enqueue(request);
                        if (request.Done.WaitOne(25000)) response = request.Result;
                        else { request.Cancelled = true; response = Error("timeout", "Observe the game state before submitting another action"); }
                    }
                    var output = Encoding.UTF8.GetBytes(response.ToString(Formatting.None) + "\n");
                    stream.Write(output, 0, output.Length);
                }
                catch (Exception) { /* The game continues while disconnected clients release their sockets. */ }
            }
        }
        public static JObject Error(string code, string message) => new JObject { ["ok"] = false, ["error"] = code, ["message"] = message };
        public void Dispose() { running = false; listener.Stop(); }
    }
}
