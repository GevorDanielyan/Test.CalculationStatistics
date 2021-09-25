using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UdpPerformance;

namespace Client
{
    class Program
    {
        private const int PacketSize = 10000;

        static async Task Main(string[] args)
        {
            using var udpSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            // Get a cancel source that cancels when the user presses CTRL+C.
            var userExitSource = GetUserConsoleCancellationSource();

            var cancelToken = userExitSource.Token;

            // Discard our socket when the user cancels.
            using var cancelReg = cancelToken.Register(() => udpSocket.Dispose());

            var throughput = new ThroughputCounter();

            // Start a background task to print throughput periodically.
            var result = PrintThroughput(throughput, cancelToken);

            udpSocket.Bind(new IPEndPoint(IPAddress.Any, 8005));

            await DoReceiveAsync(udpSocket, throughput, cancelToken);

        }

        private static async Task PrintThroughput(ThroughputCounter counter, CancellationToken cancelToken)
        {
            while (!cancelToken.IsCancellationRequested)
            {
                // If you don't need delay's, you need to coment this line! I suggest you to uncomment this line!
                await Task.Delay(1000, cancelToken);

                Console.WriteLine();

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

        private static async Task DoReceiveAsync(Socket udpSocket, ThroughputCounter throughput, CancellationToken cancelToken)
        {
            // Taking advantage of pre-pinned memory here using the .NET5 POH (pinned object heap).
            var buffer = GC.AllocateArray<byte>(sizeof(int), true);
            var bufferMem = buffer.AsMemory();

            while (!cancelToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udpSocket.ReceiveFromAsync(bufferMem);

                    // The result tells me where it came from, and gives me the data.
                    if (result is SocketReceiveFromResult recvResult)
                    {
                        throughput.Add(recvResult.ReceivedBytes);

                        List<double> randomNumbers = new();

                        for (int i = 0; i < bufferMem.Span.Length; i++)
                        {
                            // Reading numbers from buffer span and add into list.
                            BinaryPrimitives.TryReadInt32LittleEndian(bufferMem.Span, out var number);
                            randomNumbers.Add(number);
                            bufferMem.Span[i]++;
                        }

                        if (Console.ReadKey().Key == ConsoleKey.Enter)
                        {
                            Console.WriteLine("The list item's mean is {0}", MathStatisticsCalculationExtensions.Mean(randomNumbers));
                            Console.WriteLine("The list item's standard deviation is {0}", MathStatisticsCalculationExtensions.StandardDeviation(randomNumbers));
                            Console.WriteLine("The list item's Variance is {0}", MathStatisticsCalculationExtensions.Variance(randomNumbers));
                        }
                    }

                    else
                    {
                        break;
                    }
                }
                catch (SocketException)
                {
                    // Socket exception means we are finished.
                    break;
                }
            }
        }
    }
}
