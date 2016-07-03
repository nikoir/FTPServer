using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FTPServer
{
    class FTPServer
    {
        private bool _disposed = false;
        private TcpListener _listener;
        private List<ClientConnection> _activeConnections;

        public FTPServer()
        {
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, 21);
            _listener.Start();
            _activeConnections = new List<ClientConnection>();
            _listener.BeginAcceptTcpClient(HandleAcceptTcpClient, _listener);
            Console.WriteLine("Server started.");
        }

        public void Stop()
        {
            if (_listener != null)
            {
                _listener.Stop();
            }
        }

        private void HandleAcceptTcpClient(IAsyncResult result)
        {
            _listener.BeginAcceptTcpClient(HandleAcceptTcpClient, _listener);
            TcpClient client = _listener.EndAcceptTcpClient(result);

            Console.WriteLine(client.Client.RemoteEndPoint + " connected.");

            ClientConnection connection = new ClientConnection(client);
            _activeConnections.Add(connection);

            ThreadPool.QueueUserWorkItem(connection.HandleClient, client);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();

                    foreach (ClientConnection conn in _activeConnections)
                    {
                        conn.Dispose();
                    }
                }
            }

            _disposed = true;
        }
    }
}
