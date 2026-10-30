using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RelayControlNetFx
{
    internal enum TransportState
    {
        Disconnected,
        Connecting,
        Listening,
        Connected
    }

    /// <summary>
    /// Manages one TCP connection; in server mode keeps listening after a client disconnects.
    /// Does not use the CH9120 configuration protocol: only TCP relay commands are sent.
    /// </summary>
    internal sealed class RelayTcpTransport : IDisposable
    {
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private TcpClient _client;
        private TcpListener _listener;
        private NetworkStream _stream;
        private CancellationTokenSource _cancellation;
        private bool _serverMode;
        private int _generation;

        public event Action<TransportState, string> StateChanged;
        public event Action<string> MessageLogged;

        public async Task ConnectAsync(string address, int port)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();
            int run = BeginRun(cancellation, false);
            TcpClient client = new TcpClient();

            lock (_sync)
            {
                if (IsCurrentUnsafe(run))
                {
                    _client = client;
                }
            }

            SetState(run, TransportState.Connecting, "Connecting to " + address + ":" + port + "...");

            try
            {
                Task connectTask = client.ConnectAsync(address, port);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(7), cancellation.Token);
                Task completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completedTask != connectTask)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    throw new TimeoutException("TCP connection timed out after 7 seconds.");
                }

                await connectTask.ConfigureAwait(false);

                lock (_sync)
                {
                    if (!IsCurrentUnsafe(run))
                    {
                        client.Close();
                        return;
                    }

                    _stream = client.GetStream();
                }

                Log("Connected to " + client.Client.RemoteEndPoint + ".");
                SetState(run, TransportState.Connected, "Connected: " + client.Client.RemoteEndPoint);
                _ = ReceiveAndCloseAsync(run, client, cancellation.Token);
            }
            catch
            {
                client.Close();

                if (IsCurrent(run))
                {
                    Stop();
                }

                if (!cancellation.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        public void Listen(IPAddress bindAddress, int port)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();
            int run = BeginRun(cancellation, true);

            try
            {
                TcpListener listener = new TcpListener(bindAddress, port);
                listener.Start();

                lock (_sync)
                {
                    if (!IsCurrentUnsafe(run))
                    {
                        listener.Stop();
                        return;
                    }

                    _listener = listener;
                }

                SetState(run, TransportState.Listening, "Listening: " + bindAddress + ":" + port);
                Log("Listening on " + bindAddress + ":" + port + ". Waiting for board TCP Client...");
                _ = AcceptLoopAsync(run, listener, cancellation.Token);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private async Task AcceptLoopAsync(int run, TcpListener listener, CancellationToken cancellation)
        {
            while (IsCurrent(run) && !cancellation.IsCancellationRequested)
            {
                TcpClient incoming = null;

                try
                {
                    incoming = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    TcpClient previous;

                    lock (_sync)
                    {
                        if (!IsCurrentUnsafe(run))
                        {
                            incoming.Close();
                            return;
                        }

                        previous = _client;
                        _client = incoming;
                        _stream = incoming.GetStream();
                    }

                    if (previous != null)
                    {
                        // Replace a stale connection after a board reboot/power loss.
                        previous.Close();
                        Log("Replaced the previous TCP client connection.");
                    }

                    Log("Incoming connection from " + incoming.Client.RemoteEndPoint + ".");
                    SetState(run, TransportState.Connected, "Connected: " + incoming.Client.RemoteEndPoint);
                    _ = ReceiveAndCloseAsync(run, incoming, cancellation);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (IsCurrent(run))
                    {
                        Log("TCP listener error: " + ex.Message);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    if (IsCurrent(run))
                    {
                        Log("TCP accept error: " + ex.Message);
                    }

                    break;
                }
            }

            if (IsCurrent(run))
            {
                Stop();
            }
        }

        private async Task ReceiveAndCloseAsync(int run, TcpClient client, CancellationToken cancellation)
        {
            byte[] buffer = new byte[4096];

            try
            {
                NetworkStream stream = client.GetStream();

                while (IsCurrent(run) && !cancellation.IsCancellationRequested)
                {
                    int received = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation)
                        .ConfigureAwait(false);

                    if (received == 0)
                    {
                        Log("Remote peer disconnected.");
                        break;
                    }

                    Log("RX  " + RelayProtocol.ToHex(buffer, received));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on Disconnect.
            }
            catch (ObjectDisposedException)
            {
                // Expected when the socket is closed.
            }
            catch (IOException ex)
            {
                if (IsCurrent(run))
                {
                    Log("TCP receive ended: " + ex.Message);
                }
            }
            catch (SocketException ex)
            {
                if (IsCurrent(run))
                {
                    Log("TCP receive ended: " + ex.Message);
                }
            }
            finally
            {
                bool activeSession;
                bool listening;

                lock (_sync)
                {
                    activeSession = IsCurrentUnsafe(run) && ReferenceEquals(_client, client);
                    listening = activeSession && _serverMode;

                    if (activeSession)
                    {
                        _client = null;
                        _stream = null;
                    }
                }

                client.Close();

                if (listening)
                {
                    SetState(run, TransportState.Listening, "Client disconnected; waiting for a new connection...");
                }
                else if (activeSession)
                {
                    Stop();
                }
            }
        }

        public async Task SendAsync(int channel, bool open)
        {
            byte[] command = RelayProtocol.CreateCommand(channel, open);
            await _sendLock.WaitAsync().ConfigureAwait(false);

            try
            {
                NetworkStream stream;

                lock (_sync)
                {
                    stream = _stream;
                }

                if (stream == null)
                {
                    throw new InvalidOperationException("No TCP client is connected.");
                }

                try
                {
                    await stream.WriteAsync(command, 0, command.Length).ConfigureAwait(false);
                    Log("TX  " + RelayProtocol.ToHex(command, command.Length) +
                        "  (" + (channel == RelayProtocol.AllChannels ? "ALL" : "CH" + channel) +
                        " " + (open ? "OPEN/ON" : "CLOSE/OFF") + ")");
                }
                catch (IOException)
                {
                    CloseCurrentClient();
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    CloseCurrentClient();
                    throw;
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void CloseCurrentClient()
        {
            TcpClient client;

            lock (_sync)
            {
                client = _client;
            }

            if (client != null)
            {
                client.Close();
            }
        }

        private int BeginRun(CancellationTokenSource cancellation, bool serverMode)
        {
            lock (_sync)
            {
                if (_cancellation != null)
                {
                    cancellation.Dispose();
                    throw new InvalidOperationException("Disconnect before starting another connection.");
                }

                _cancellation = cancellation;
                _serverMode = serverMode;
                return ++_generation;
            }
        }

        private bool IsCurrent(int run)
        {
            lock (_sync)
            {
                return IsCurrentUnsafe(run);
            }
        }

        private bool IsCurrentUnsafe(int run)
        {
            return _generation == run && _cancellation != null;
        }

        private void SetState(int run, TransportState state, string detail)
        {
            if (!IsCurrent(run))
            {
                return;
            }

            Action<TransportState, string> handler = StateChanged;

            if (handler != null)
            {
                handler(state, detail);
            }
        }

        private void Log(string message)
        {
            Action<string> handler = MessageLogged;

            if (handler != null)
            {
                handler(message);
            }
        }

        public void Stop()
        {
            TcpClient client;
            TcpListener listener;
            CancellationTokenSource cancellation;

            lock (_sync)
            {
                ++_generation;
                client = _client;
                listener = _listener;
                cancellation = _cancellation;
                _client = null;
                _listener = null;
                _stream = null;
                _cancellation = null;
            }

            if (cancellation != null)
            {
                cancellation.Cancel();
            }

            if (client != null)
            {
                client.Close();
            }

            if (listener != null)
            {
                listener.Stop();
            }

            // Do not dispose a CTS here: asynchronous reads may still be unwinding.
            Action<TransportState, string> handler = StateChanged;

            if (handler != null)
            {
                handler(TransportState.Disconnected, "Disconnected");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
