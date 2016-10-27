using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PolarisClientSample
{
    /// <summary>
    /// This application connects to the Point One Navigtion Polaris service, authenticates and sends in a rough receiver position (in SF).
    /// It also accepts connections from local clients who will receive raw RTCM3 messages when the program is connected to Polaris.
    /// 
    /// </summary>
    class Program
    {
        const int VERSION_MAJOR = 1;
        const int VERSION_MINOR = 0;

        const string POLARIS_SERVER = "polaris.pointonenav.com";
        const int POLARIS_PORT = 8088;

        //Replace this with your issued serial number.
        const string SERIAL_NUMBER = "D00000000S1";

        //This is the Point One Navigation office. In a real system you'd get this periodically from your GPS receiver.
        const double ECEF_X = -2702922.85;
        const double ECEF_Y = -4259728.48;
        const double ECEF_Z = 3889319.38;

        /// <summary>
        /// Main entry point of the sample application. Simply spools up the threads and waits for the user to exit.
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Console.WriteLine("Point One Navigation Polaris Demo Application");
            Console.WriteLine("Version " + VERSION_MAJOR + "." + VERSION_MINOR);            
            StartPolarisClient();
            StartServer();

            while (Console.ReadKey().KeyChar != 'q')
                continue;

            running = false;            
            polarisThread.Join();
            server.Stop();
            serverThread.Join();
        }

        /// <summary>
        /// Connects and authorizes with the Polaris correction service, then forwards the raw corrections to any connected clients.
        /// </summary>
        private static void StartPolarisClient()
        {
            Console.WriteLine("Connecting to Point One Polaris Server...");
            TcpClient polarisClient = new TcpClient();
            try
            {
                polarisClient.Connect(POLARIS_SERVER, POLARIS_PORT);
            }
            catch (SocketException ex)
            {
                Console.WriteLine("Failed to connect to server. " + ex.Message + " Retrying...");
                Thread.Sleep(1000);
                StartPolarisClient();
            }
            Console.WriteLine("Sending Auth....");
            SendAuth(polarisClient);
            Console.WriteLine("\nSending Position:");
            Console.WriteLine(ECEF_X + "," + ECEF_Y + "," + ECEF_Z + " (ECEF)");
            SendPosition(ECEF_X, ECEF_Y, ECEF_Z, polarisClient);

            //start a thread to manage the rx from Polaris. This is where data is then forwarded to all connected clients.
            polarisThread = new Thread(new ThreadStart(delegate {
                Stream polarisStream = polarisClient.GetStream();
                while (running)
                {
                    //Read a chunk of data from the server and then forward it
                    byte[] buffer = new byte[1024]; 
                    int numBytesRead = polarisStream.Read(buffer, 0, 1024);
                    lock (clients)
                    {
                        clients.RemoveAll(c => c.Connected == false);
                        foreach (TcpClient client in clients)
                        {
                            try
                            {
                                client.GetStream().Write(buffer, 0, numBytesRead);
                            }
                            catch (Exception)
                            {
                                client.Close();
                            }
                        }
                    }
                }
            }));
            polarisThread.Start();

        }

        /// <summary>
        /// Starts a simple TCP socket listener on which receivers will get RTCM3 corrections
        /// </summary>
        static void StartServer()
        {
            serverThread = new Thread(new ThreadStart(delegate {
                try
                {
                    server = new TcpListener(IPAddress.Any, 9000);
                    server.Start();
                    Console.WriteLine("\nServer listening on port " + ((IPEndPoint)server.LocalEndpoint).Port.ToString());
                    Console.WriteLine("\nConnect in uCenter using Reciever->Differential GNSS Interface");
                    Console.WriteLine("Select 'Internet Connection' setting Server to 127.0.0.1 and port to " + ((IPEndPoint)server.LocalEndpoint).Port.ToString());
                    Console.WriteLine("\nEnter q to quit.");


                    while (running)
                    {                        
                        //wait for incoming connections
                        TcpClient client = server.AcceptTcpClient();
                        Console.WriteLine("Client Connected!");
                        //Add the client to the list of connected clients who will receive corrections in the other thread                        
                        lock (clients)
                        {
                            clients.Add(client);
                        }                        
                    }
                }
                catch (Exception e)
                {
                    if (e is SocketException && (e as SocketException).SocketErrorCode == SocketError.Interrupted)
                        Console.WriteLine("\nShutting down server for exit.");
                    else
                        Console.WriteLine("Server socket error. " + e.ToString());
                    return;
                }
                finally
                {
                    if (server!=null)
                        server.Stop();
                }
            }));
            serverThread.Start();
        }

        /// <summary>
        /// Encodes an authentication message and sends it via the polaris client. Note the serial number is used as the auth token.
        /// </summary>
        /// <param name="polarisClient"></param>
        static void SendAuth(TcpClient polarisClient)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            WritePacketHeader(0xe0, 0x01, (ushort)SERIAL_NUMBER.Length, bw);
            bw.Write(SERIAL_NUMBER.ToCharArray());
            WriteChecksum(ms.ToArray(), bw);
            polarisClient.GetStream().Write(ms.ToArray(), 0, ms.ToArray().Length);            
        }

        /// <summary>
        /// Encodes a position message and send it via the polaris client. The xyz are ECEF in meters.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="polarisClient"></param>
        static void SendPosition(double x, double y, double z, TcpClient polarisClient)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            WritePacketHeader(0xe0, 0x03, 12, bw);
            bw.Write((Int32)(Math.Round(x * 100.0)));
            bw.Write((Int32)(Math.Round(y * 100.0)));
            bw.Write((Int32)(Math.Round(z * 100.0)));
            WriteChecksum(ms.ToArray(), bw);
            polarisClient.GetStream().Write(ms.ToArray(), 0, ms.ToArray().Length);
        }


        /// <summary>
        /// Writes the header part of the encapsulated message to the underlying binary writer
        /// </summary>
        /// <param name="clazz"></param>
        /// <param name="id"></param>
        /// <param name="length"></param>
        /// <param name="bw"></param>
        static void WritePacketHeader(byte clazz, byte id, ushort length, BinaryWriter bw)
        {
            bw.Write((byte)0xb5);
            bw.Write((byte)0x62);
            bw.Write(clazz);
            bw.Write(id);
            bw.Write((length));
        }

        /// <summary>
        /// Calculates the checksum of the byte array and then writes the two bytes to the binary writer
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="bw"></param>
        static void WriteChecksum(byte[] bytes, BinaryWriter bw)
        {
            byte ckA = 0;
            byte ckB = 0;

            for (int i = 2; i < bytes.Length; i++)
            {
                ckA = (byte)(ckA + bytes[i]);
                ckB = (byte)(ckB + ckA);
            }

            bw.Write(ckA);
            bw.Write(ckB);
        }



        //Internal variables
        volatile static bool running = true;
        static TcpListener server = null;
        static Thread serverThread;
        static Thread polarisThread;
        static List<TcpClient> clients = new List<TcpClient>();


    }
}
