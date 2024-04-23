﻿using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using System.Text;
using System.Threading;

namespace CurrensNetwork
{
    /// <summary>
    /// Represents a host for managing network connections and data communication.
    /// </summary>
    public class Host
    {
        /// <summary>
        /// Event that occurs when a client is connected.
        /// </summary>
        /// <returns><see cref="TcpClient"/> of connected client</returns>
        public delegate void _OnClientConnected(ulong ID, TcpClient client);
        /// <summary>
        /// Event that occurs when a client is disconnected.
        /// </summary>
        /// <returns><see cref="ulong"/> - ID of disconected client</returns>
        public delegate void _OnClientDisconnected(ulong ID);
        /// <summary>
        /// Event that occurs when data is received.
        /// </summary>
        /// <returns><see cref="Packet"/> object containing the received data</returns>
        public delegate void _OnDataRecieved(Packet packet);
        /// <summary>
        /// Event that occurs when a host is succesfully created.
        /// </summary>
        public delegate void _OnHostCreated();
        /// <summary>
        /// Event that occurs when host creation fails.
        /// </summary>
        /// <returns><see cref="Exception"/> - reason if Failure </returns>
        public delegate void _OnHostCreationFailure(Exception ex);
        /// <summary>
        /// Invokes on downloading data progress, returns count of readed bytes
        /// </summary>
        public delegate void _OnDataReceiveProgress(int loaded);
        /// <summary>
        /// Event that occurs when a host has stopped.
        /// </summary>
        public delegate void _OnHostStopped();

        public event _OnClientConnected OnClientConnected;
        public event _OnClientDisconnected OnClientDisconnected;
        public event _OnDataRecieved OnDataRecieved;
        public event _OnHostCreated OnHostCreated;
        public event _OnHostCreationFailure OnHostCreationFailure;
        public event _OnDataReceiveProgress OnDataReceiveProgress;
        public event _OnHostStopped OnHostStopped;

        private Thread connectionsChecker = null;
        private TcpListener listener;
        private Dictionary<EndPoint, TcpClient> connectedClients = new Dictionary<EndPoint, TcpClient>();

        /// <summary>
        /// Creates a host on the specified port to accept incoming connections.
        /// </summary>
        /// <param name="Port">The port number for hosting the server.</param>
        public async Task Create(int Port)
        {
            if (Port < 0 || Port > 65536)
                throw new ArgumentOutOfRangeException($"Port value must be between 1 and 65536");
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                Networking.SetIsHost(true);
            }
            catch (Exception ex)
            {
                OnHostCreationFailure?.Invoke(ex);
            }
            Networking.SetID(1);
            Networking.SetHost(this);
            Networking.SetClient(null);
            OnHostCreated?.Invoke();

            connectionsChecker = new Thread(ConnectionsChecker);
            connectionsChecker.Start();

            _ = Task.Run(async () => await DataReciever());
        }
        /// <summary>
        /// Stops host
        /// </summary>
        public void Stop()
        {
            listener.Stop();

            foreach (var client in connectedClients.Values)
                client.Close();

            connectedClients.Clear();
            connectionsChecker.Abort();
            OnHostStopped?.Invoke();
        }


        private void ConnectionsChecker()
        {
            var tcpClient = listener.AcceptTcpClientAsync();
            connectedClients.Add(tcpClient.Result.Client.RemoteEndPoint, tcpClient.Result);
            ulong ID = ulong.Parse(tcpClient.Result.Client.RemoteEndPoint.ToString().Replace(".", "").Replace(":", ""));
            Networking.ConnectedClients.Add(ID, tcpClient.Result);
            OnClientConnected?.Invoke(ID, tcpClient.Result);
            ConnectionsChecker();
        }

        private async Task DataReciever()
        {
            while (true)
            {
                if (connectedClients.Count != 0)
                {
                    foreach (var client in connectedClients.Keys.ToList())
                    {
                        if (connectedClients[client].Connected)
                        {
                            var stream = connectedClients[client].GetStream();
                            if (stream.DataAvailable)
                            {
                                byte[] buffer = new byte[1024];
                                int bytesRead;
                                StringBuilder stringBuilder = new StringBuilder();
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                                {
                                    OnDataReceiveProgress?.Invoke(bytesRead);
                                    stringBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                                    if (!stream.DataAvailable)
                                        break;
                                }
                                string _prepacket = stringBuilder.ToString();
                                XmlSerializer serializer = new XmlSerializer(typeof(Packet));
                                StringReader reader = new StringReader(_prepacket);
                                Packet receivedPacket = (Packet)serializer.Deserialize(reader);

                                string methodName = receivedPacket.Name;
                                object[] args = receivedPacket.Params.ToArray();
                                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                                foreach (Assembly assembly in assemblies)
                                {
                                    MethodInfo[] methods = assembly.GetTypes()
                                        .SelectMany(t => t.GetMethods())
                                        .Where(m => m.GetCustomAttributes(typeof(RPC), false).Length > 0)
                                        .ToArray();

                                    foreach (var method in methods)
                                    {
                                        if (method.Name == methodName && method.GetParameters().Length == args.Length)
                                        {
                                            if (receivedPacket.SendTo == 0 || receivedPacket.SendTo == 1)
                                            {
                                                object classInstance = Activator.CreateInstance(method.DeclaringType);
                                                method.Invoke(classInstance, args);
                                            }
                                        }
                                    }
                                }

                                OnDataRecieved?.Invoke(receivedPacket);
                                if (receivedPacket.SendTo == 0)
                                    Networking.Rpc(receivedPacket);
                                else
                                    if (receivedPacket.SendTo != 1)
                                    Networking.RpcTo(receivedPacket.SendTo, receivedPacket);
                            }
                        }
                        else
                        {
                            connectedClients.Remove(client);
                            OnClientDisconnected?.Invoke(ulong.Parse(client.ToString().Replace(".", "").Replace(":", "")));
                        }
                    }
                }
            }
        }
    }
}