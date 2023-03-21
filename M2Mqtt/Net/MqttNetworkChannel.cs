/*
Copyright (c) 2013, 2014 Paolo Patierno

All rights reserved. This program and the accompanying materials
are made available under the terms of the Eclipse Public License v1.0
and Eclipse Distribution License v1.0 which accompany this distribution. 

The Eclipse Public License is available at 
   http://www.eclipse.org/legal/epl-v10.html
and the Eclipse Distribution License is available at 
   http://www.eclipse.org/org/documents/edl-v10.php.

Contributors:
   Paolo Patierno - initial API and implementation and/or initial documentation
*/


#if SSL
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
using Microsoft.SPOT.Net.Security;
#elif (COMPACT_FRAMEWORK)
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using uPLibrary.Networking.M2Mqtt.Net;
#else
using System.Net.Security;
using System.Security.Authentication;
#endif
#endif
using System.Net.Sockets;
using System.Net;
#if !(COMPACT_FRAMEWORK)
using System.Security.Cryptography.X509Certificates;
#endif
using System;
using M2Mqtt.NetStandard.Utility;

// ReSharper disable UseNullPropagation

// ReSharper disable EnforceIfStatementBraces
// ReSharper disable EnforceWhileStatementBraces

namespace uPLibrary.Networking.M2Mqtt
{
#if COMPACT_FRAMEWORK

#endif

    /// <summary>
    /// Channel to communicate over the network
    /// </summary>
    public class MqttNetworkChannel : IMqttNetworkChannel
    {
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
        private readonly RemoteCertificateValidationCallback userCertificateValidationCallback;
        private readonly LocalCertificateSelectionCallback userCertificateSelectionCallback;
#endif
        // remote host information
        private string remoteHostName;
        private IPAddress remoteIpAddress;
        private int remotePort;

#if !COMPACT_FRAMEWORK
        // socket for communication
        private Socket socket;
#else
        private TcpClient tcp;
        private TlsClient tlsClient;
#endif
        // using SSL
        private bool secure;

        // CA certificate (on client)
        private X509Certificate caCert;
        // Server certificate (on broker)
        private X509Certificate serverCert;
        // client certificate (on client)
        private X509Certificate clientCert;

        // SSL/TLS protocol version
        private MqttSslProtocols sslProtocol;

        /// <summary>
        /// Remote host name
        /// </summary>
        public string RemoteHostName { get { return this.remoteHostName; } }

        /// <summary>
        /// Remote IP address
        /// </summary>
        public IPAddress RemoteIpAddress { get { return this.remoteIpAddress; } }

        /// <summary>
        /// Remote port
        /// </summary>
        public int RemotePort { get { return this.remotePort; } }

#if SSL
        // SSL stream
#if !COMPACT_FRAMEWORK
        private SslStream sslStream;
#else
        private TlsClientProtocol tlsHandler;
#endif
#if (!MF_FRAMEWORK_VERSION_V4_2 && !MF_FRAMEWORK_VERSION_V4_3 && !COMPACT_FRAMEWORK)
        private NetworkStream netStream;
#endif
#endif
        private readonly object streamLock = new object();

        // Local TCP port, cached for log output as it's unavailable from the socket after disposed exception.
        private string localPort = "???";

        private bool _isReceiving = false;
        private readonly object _isReceivingLock = new object();

        public bool IsReceiving { get {
#if USEMONITOREDLOCK
            using (new MonitoredLock(_isReceivingLock, "MqttNetworkChannel.IsReceiving _isReceivingLock"))
#else
            lock (_isReceivingLock)
#endif
            {
                return _isReceiving;
            }
        } }

        public string LocalPort { get { return this.localPort; } }

        /// <summary>
        /// Data available on the channel
        /// </summary>
        public bool DataAvailable
        {
            get
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.DataAvailable streamLock"))
#else
                lock (this.streamLock)
#endif
                {
#if SSL
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
                    if (secure)
                        return this.sslStream.DataAvailable;
                    else
                        return (this.socket.Available > 0);
#elif COMPACT_FRAMEWORK
                    if (secure)
                        return ((NetworkStream) this.tlsHandler.Stream).DataAvailable;
                    else
                        return (this.tcp.Client.Available > 0);
#else
                    if (secure)
                        return this.netStream.DataAvailable;
                    else
                        return (this.socket.Available > 0);
#endif
#else
                    return (this.socket.Available > 0);
#endif
                }
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="socket">Socket opened with the client</param>
        public MqttNetworkChannel(Socket socket)
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            : this(socket, false, null, MqttSslProtocols.None, null, null)
#else
            : this(socket, false, null, MqttSslProtocols.None)
#endif
        {

        }
        
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="socket">Socket opened with the client</param>
        /// <param name="secure">Secure connection (SSL/TLS)</param>
        /// <param name="serverCert">Server X509 certificate for secure connection</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
        /// <param name="userCertificateSelectionCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateValidationCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttNetworkChannel(Socket socket, bool secure, X509Certificate serverCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
#else
        public MqttNetworkChannel(Socket socket, bool secure, X509Certificate serverCert, MqttSslProtocols sslProtocol)
#endif
        {
#if COMPACT_FRAMEWORK
            this.tcp = new TcpClient(socket.AddressFamily);
#else
            this.socket = socket;
#endif
            this.secure = secure;
            this.serverCert = serverCert;
            this.sslProtocol = sslProtocol;
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            this.userCertificateValidationCallback = userCertificateValidationCallback;
            this.userCertificateSelectionCallback = userCertificateSelectionCallback;
#endif
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="remoteHostName">Remote Host name</param>
        /// <param name="remotePort">Remote port</param>
        public MqttNetworkChannel(string remoteHostName, int remotePort)
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            : this(remoteHostName, remotePort, false, null, null, MqttSslProtocols.None, null, null)
#else
            : this(remoteHostName, remotePort, false, null, null, MqttSslProtocols.None, new BasicTlsClient())
#endif
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="remoteHostName">Remote Host name</param>
        /// <param name="remotePort">Remote port</param>
        /// <param name="secure">Using SSL</param>
        /// <param name="caCert">CA certificate</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
        /// <param name="userCertificateSelectionCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateValidationCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttNetworkChannel(string remoteHostName, int remotePort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
#elif (COMPACT_FRAMEWORK)
        /// <param name="tlsClient">Bouncy Castle Tls Client - set null to use default client (no client certificate checks)</param>
        public MqttNetworkChannel(string remoteHostName, int remotePort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol, TlsClient tlsClient)
#else
        public MqttNetworkChannel(string remoteHostName, int remotePort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol)
#endif
        {
            IPAddress remoteIpAddress = null;
            try
            {
                // check if remoteHostName is a valid IP address and get it
                remoteIpAddress = IPAddress.Parse(remoteHostName);
            }
            catch
            {
            }

            // in this case the parameter remoteHostName isn't a valid IP address
            if (remoteIpAddress == null)
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(remoteHostName);
                if ((hostEntry != null) && (hostEntry.AddressList.Length > 0))
                {
                    // check for the first address not null
                    // it seems that with .Net Micro Framework, the IPV6 addresses aren't supported and return "null"
                    int i = 0;
                    while (hostEntry.AddressList[i] == null) i++;
                    remoteIpAddress = hostEntry.AddressList[i];
                }
                else
                {
                    throw new Exception("No address found for the remote host name");
                }
            }

            this.remoteHostName = remoteHostName;
            this.remoteIpAddress = remoteIpAddress;
            this.remotePort = remotePort;
            this.secure = secure;
            this.caCert = caCert;
            this.clientCert = clientCert;
            this.sslProtocol = sslProtocol;
#if COMPACT_FRAMEWORK
            this.tlsClient = tlsClient;
#endif
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            this.userCertificateValidationCallback = userCertificateValidationCallback;
            this.userCertificateSelectionCallback = userCertificateSelectionCallback;
#endif
        }

        /// <summary>
        /// Connect to remote server
        /// </summary>
        public void Connect()
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.Connect streamLock"))
#else
            lock (this.streamLock)
#endif
            {
#if COMPACT_FRAMEWORK
                this.tcp = new TcpClient();
                this.tcp.Connect(new IPEndPoint(this.remoteIpAddress, this.remotePort));
#else
                this.socket = new Socket(this.remoteIpAddress.GetAddressFamily(), SocketType.Stream, ProtocolType.Tcp);

                // try connection to the broker
                this.socket.Connect(new IPEndPoint(this.remoteIpAddress, this.remotePort));
#endif

#if COMPACT_FRAMEWORK
                var localEndpoint = this.tcp.Client.LocalEndPoint;
#else
                var localEndpoint = this.socket.LocalEndPoint;
#endif
                var splitStr = localEndpoint.ToString().Split(':');
                var splitLen = splitStr.Length;
                if (splitLen > 1)
                {
                    this.localPort = splitStr[splitLen - 1];
                }

#if SSL

                // secure channel requested
                if (secure)
                {
                    // create SSL stream
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
                    this.sslStream = new SslStream(this.socket);
#elif COMPACT_FRAMEWORK
                    this.tlsHandler = new TlsClientProtocol(tcp.GetStream(), new SecureRandom());

                    this.tlsHandler.Connect(tlsClient ?? new BasicTlsClient());
#else
                    this.netStream = new NetworkStream(this.socket);
                    this.sslStream = new SslStream(this.netStream, false, this.userCertificateValidationCallback, this.userCertificateSelectionCallback);
                    this.sslStream.ReadTimeout = 10 * 1000;
                    this.sslStream.WriteTimeout = 10 * 1000;
#endif

                    // server authentication (SSL/TLS handshake)
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
                    this.sslStream.AuthenticateAsClient(this.remoteHostName,
                        this.clientCert,
                        new X509Certificate[] { this.caCert },
                        SslVerification.CertificateRequired,
                        MqttSslUtility.ToSslPlatformEnum(this.sslProtocol));
#elif COMPACT_FRAMEWORK
                    // nothing to do here - see TO DOs at top of file
#else
                    X509CertificateCollection clientCertificates = null;

                    // check if there is a client certificate to add to the collection, otherwise it's null (as empty)
                    if (this.clientCert != null)
                        clientCertificates = new X509CertificateCollection(new X509Certificate[] { this.clientCert });

                    this.sslStream.AuthenticateAsClient(this.remoteHostName,
                                                        clientCertificates,
                                                        MqttSslUtility.ToSslPlatformEnum(this.sslProtocol),
                                                        false);

#endif
                }
#endif
            }
        }

        /// <summary>
        /// Send data on the network channel
        /// </summary>
        /// <param name="buffer">Data buffer to send</param>
        /// <returns>Number of byte sent</returns>
        public int Send(byte[] buffer)
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.Send streamLock"))
#else
            lock (this.streamLock)
#endif
            {
#if SSL
                if (this.secure)
                {
                    try
                    {
#if COMPACT_FRAMEWORK
                        this.tlsHandler.Stream.Write(buffer, 0, buffer.Length);
                        this.tlsHandler.Stream.Flush();
#else

                        this.sslStream.Write(buffer, 0, buffer.Length);
                        this.sslStream.Flush();
#endif
                    }
                    catch (Exception e)
                    {
#if TRACE
                        Utility.Trace.WriteLine(Utility.TraceLevel.Warning, "{0} occurred while writing to SSL stream, port {1}.", e.GetType(), localPort);
#endif
                        throw;
                    }

                    return buffer.Length;
                }
                else
#if COMPACT_FRAMEWORK
                    return this.tcp.Client.Send(buffer, 0, buffer.Length, SocketFlags.None);
#else
                    return this.socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
#endif
#else
                return this.socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
#endif
            }
        }

        /// <summary>
        /// Receive data from the network
        /// </summary>
        /// <param name="buffer">Data buffer for receiving data</param>
        /// <returns>Number of bytes received</returns>
        public int Receive(byte[] buffer)
        {
            try
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(_isReceivingLock, "MqttNetworkChannel.Receive _isReceivingLock"))
#else
                lock (_isReceivingLock)
#endif
                {
                    _isReceiving = true;
                }

#if USEMONITOREDLOCK
                using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.Receive streamLock"))
#else
                lock (this.streamLock)
#endif
                {

#if SSL
                    if (this.secure)
                    {
                        // read all data needed (until fill buffer)
                        int idx = 0, read = 0;
                        while (idx < buffer.Length)
                        {
                            // fixed scenario with socket closed gracefully by peer/broker and
                            // Read return 0. Avoid infinite loop.
#if COMPACT_FRAMEWORK
                        read = this.tlsHandler.Stream.Read(buffer, idx, buffer.Length - idx);
#else
                            read = this.sslStream.Read(buffer, idx, buffer.Length - idx);
#endif

                            if (read == 0)
                            {
                                throw new Exception("Unable to read any byte from ssl stream");
                            }

                            idx += read;
                        }

                        return buffer.Length;
                    }
                    else
                    {
                        // read all data needed (until fill buffer)
                        int idx = 0, read = 0;
                        while (idx < buffer.Length)
                        {
                            // fixed scenario with socket closed gracefully by peer/broker and
                            // Read return 0. Avoid infinite loop.
#if COMPACT_FRAMEWORK
                        read = this.tcp.Client.Receive(buffer, idx, buffer.Length - idx, SocketFlags.None);
#else
                            read = this.socket.Receive(buffer, idx, buffer.Length - idx, SocketFlags.None);
#endif
                            if (read == 0)
                                return 0;

                            idx += read;
                        }

                        return buffer.Length;
                    }
#else
                // read all data needed (until fill buffer)
                int idx = 0, read = 0;
                while (idx < buffer.Length)
                {
                    // fixed scenario with socket closed gracefully by peer/broker and
                    // Read return 0. Avoid infinite loop.
                    read = this.socket.Receive(buffer, idx, buffer.Length - idx, SocketFlags.None);
                    if (read == 0)
                        return 0;
                    idx += read;
                }
                return buffer.Length;
#endif
                }

            }
            finally
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(_isReceivingLock, "MqttNetworkChannel.Receive1 _isReceivingLock"))
#else
                lock (_isReceivingLock)
#endif
                {
                    _isReceiving = false;
                }
            }
        }

        /// <summary>
        /// Receive data from the network channel with a specified timeout
        /// </summary>
        /// <param name="buffer">Data buffer for receiving data</param>
        /// <param name="timeout">Timeout on receiving (in milliseconds)</param>
        /// <returns>Number of bytes received</returns>
        public int Receive(byte[] buffer, int timeout)
        {
            // check data availability (timeout is in microseconds)
#if COMPACT_FRAMEWORK
            if (this.tcp.Client.Poll(timeout * 1000, SelectMode.SelectRead))
#else
            if (this.socket.Poll(timeout * 1000, SelectMode.SelectRead))
#endif
            {
                return this.Receive(buffer);
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Close the network channel
        /// </summary>
        public void Close()
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.Close streamLock"))
#else
            lock (this.streamLock)
#endif
            {
#if SSL
                if (this.secure)
                {
#if (!MF_FRAMEWORK_VERSION_V4_2 && !MF_FRAMEWORK_VERSION_V4_3 && !COMPACT_FRAMEWORK)
                    if (this.netStream != null)
                    {
                        this.netStream.Close();
                    }
#endif

#if COMPACT_FRAMEWORK
                    if(this.tlsHandler != null)
                    {
                        this.tlsHandler.Close();
                    }
#else
                    if (this.sslStream != null)
                    {
                        this.sslStream.Close();
                    }
#endif
                }

#if COMPACT_FRAMEWORK
                this.tcp.Close();
#else
                this.socket.Close();
#endif

#else
                this.socket.Close();
#endif
            }
        }

        /// <summary>
        /// Accept connection from a remote client
        /// </summary>
        public void Accept()
        {
#if SSL
            // secure channel requested
            if (secure)
            {
                // TODO: implement server part for .NET Compact (actually necessary?)
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)

#if USEMONITOREDLOCK
                using (new MonitoredLock(this.streamLock, "MqttNetworkChannel.Accept streamLock"))
#else
                lock (this.streamLock)
#endif
                {
                    this.netStream = new NetworkStream(this.socket);
                    this.sslStream = new SslStream(this.netStream, false, this.userCertificateValidationCallback, this.userCertificateSelectionCallback);

                    this.sslStream.AuthenticateAsServer(this.serverCert, false, MqttSslUtility.ToSslPlatformEnum(this.sslProtocol), false);
                }
#endif
            }

            return;
#else
            return;
#endif
        }
    }

    /// <summary>
    /// IPAddress Utility class
    /// </summary>
    public static class IPAddressUtility
    {
        /// <summary>
        /// Return AddressFamily for the IP address
        /// </summary>
        /// <param name="ipAddress">IP address to check</param>
        /// <returns>Address family</returns>
        public static AddressFamily GetAddressFamily(this IPAddress ipAddress)
        {
#if (!MF_FRAMEWORK_VERSION_V4_2 && !MF_FRAMEWORK_VERSION_V4_3)
            return ipAddress.AddressFamily;
#else
            return (ipAddress.ToString().IndexOf(':') != -1) ? 
                AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
#endif
        }
    }

    /// <summary>
    /// MQTT SSL utility class
    /// </summary>
    public static class MqttSslUtility
    {
#if (!MF_FRAMEWORK_VERSION_V4_2 && !MF_FRAMEWORK_VERSION_V4_3 && !COMPACT_FRAMEWORK)
        public static SslProtocols ToSslPlatformEnum(MqttSslProtocols mqttSslProtocol)
        {
            switch (mqttSslProtocol)
            {
                case MqttSslProtocols.None:
                    return SslProtocols.None;
                case MqttSslProtocols.SSLv3:
                    return SslProtocols.Ssl3;
                case MqttSslProtocols.TLSv1_0:
                    return SslProtocols.Tls;
                case MqttSslProtocols.TLSv1_1:
                    return SslProtocols.Tls11;
                case MqttSslProtocols.TLSv1_2:
                    return SslProtocols.Tls12;
                default:
                    throw new ArgumentException("SSL/TLS protocol version not supported");
            }
        }
#elif (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
        public static SslProtocols ToSslPlatformEnum(MqttSslProtocols mqttSslProtocol)
        {
            switch (mqttSslProtocol)
            {
                case MqttSslProtocols.None:
                    return SslProtocols.None;
                case MqttSslProtocols.SSLv3:
                    return SslProtocols.SSLv3;
                case MqttSslProtocols.TLSv1_0:
                    return SslProtocols.TLSv1;
                case MqttSslProtocols.TLSv1_1:
                case MqttSslProtocols.TLSv1_2:
                default:
                    throw new ArgumentException("SSL/TLS protocol version not supported");
            }
        }
#endif
    }
}
