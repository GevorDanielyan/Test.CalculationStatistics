using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UdpPerformance;

namespace Server
{
    class Program
    {
        private const int PacketSize = 10000;
        const int port = 8005;

        static async Task Main(string[] args)
        {
            IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);

            using var udpSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            // Get a cancel source that cancels when the user presses CTRL+C.
            var userExitSource = GetUserConsoleCancellationSource();

            var cancelToken = userExitSource.Token;

            // Discard our socket when the user cancels.
            using var cancelReg = cancelToken.Register(() => udpSocket.Dispose());

            var throughput = new ThroughputCounter();

            // Start a background task to print throughput periodically.
            var throughputResult = PrintThroughput(throughput, cancelToken);

            IPAddress.TryParse(ipEndPoint.ToString(), out var destination);
            Console.WriteLine($"Sending to {ipEndPoint}:{port}");

            await DoSendAsync(udpSocket, ipEndPoint, throughput, cancelToken);
        }

        private static async Task DoSendAsync(Socket udpSocket, IPEndPoint destination, ThroughputCounter throughput, CancellationToken cancelToken)
        {
            using var secureRandom = RandomNumberGenerator.Create();

            // Taking advantage of pre-pinned memory here using the .NET 5 POH (pinned object heap).            
            var buffer = GC.AllocateArray<byte>(sizeof(int), true);
            var bufferMem = buffer.AsMemory();

            while (!cancelToken.IsCancellationRequested)
            {
                secureRandom.GetBytes(bufferMem.Span);

                await udpSocket.SendToAsync(destination, bufferMem);

                throughput.Add(bufferMem.Length);
            }
        }

        private static async Task PrintThroughput(ThroughputCounter counter, CancellationToken cancelToken)
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // If you don't need delay's, you need to coment this line! I suggest you to uncomment this line!
                await Task.Delay(1000, cancelToken);

                var count = counter.SampleAndReset();

                var megabytes = count / 1024d / 1024d;

                double pps = count / PacketSize;

                Console.WriteLine("{0:0.00}MBps ({1:0.00}Mbps) - {2:0.00}pps", megabytes, megabytes * 8, pps);
            }
        }

        private static CancellationTokenSource GetUserConsoleCancellationSource()
        {
            var cancellationSource = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, args) =>
            {
                args.Cancel = true;
                cancellationSource.Cancel();
            };

            return cancellationSource;
        }
    }
}
