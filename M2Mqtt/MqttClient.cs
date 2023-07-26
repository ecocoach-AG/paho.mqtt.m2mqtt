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

using System;
using System.Net;
#if !(WINDOWS_APP || WINDOWS_PHONE_APP || COMPACT_FRAMEWORK)
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
#elif COMPACT_FRAMEWORK
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Crypto.Tls;
using uPLibrary.Networking.M2Mqtt.Net;
#endif
using System.Threading;
using uPLibrary.Networking.M2Mqtt.Exceptions;
using uPLibrary.Networking.M2Mqtt.Messages;
using uPLibrary.Networking.M2Mqtt.Session;
using uPLibrary.Networking.M2Mqtt.Utility;
using uPLibrary.Networking.M2Mqtt.Internal;
// if .Net Micro Framework
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
using Microsoft.SPOT;
#if SSL
using Microsoft.SPOT.Net.Security;
#endif
// else other frameworks (.Net, .Net Compact, Mono, Windows Phone) 
#else
using System.Collections.Generic;
#if (SSL)
#if !(WINDOWS_APP || WINDOWS_PHONE_APP || COMPACT_FRAMEWORK)
using System.Security.Authentication;
using System.Net.Security;
#elif COMPACT_FRAMEWORK
#endif
#endif
#endif

#if (WINDOWS_APP || WINDOWS_PHONE_APP)
using Windows.Networking.Sockets;
#elif COMPACT_FRAMEWORK
using System.Net.Sockets;
#endif

using System.Collections;

// alias needed due to Microsoft.SPOT.Trace in .Net Micro Framework
// (it's ambiguos with uPLibrary.Networking.M2Mqtt.Utility.Trace)
using MqttUtility = uPLibrary.Networking.M2Mqtt.Utility;
using System.IO;
using M2Mqtt.NetStandard.Utility;

// ReSharper disable EnforceIfStatementBraces
// ReSharper disable UsePatternMatching

namespace uPLibrary.Networking.M2Mqtt
{
    /// <summary>
    /// MQTT Client
    /// </summary>
    public class MqttClient
    {
        private const int SocketError_TimedOut = 0x0000274C;
#if BROKER
        #region Constants ...

        // thread names
        private const string RECEIVE_THREAD_NAME = "ReceiveThread";
        private const string RECEIVE_EVENT_THREAD_NAME = "DispatchEventThread";
        private const string PROCESS_INFLIGHT_THREAD_NAME = "ProcessInflightThread";
        private const string KEEP_ALIVE_THREAD = "KeepAliveThread";

        #endregion
#endif

        /// <summary>
        /// Delagate that defines event handler for PUBLISH message received
        /// </summary>
        public delegate void MqttMsgPublishEventHandler(object sender, MqttMsgPublishEventArgs e);

        /// <summary>
        /// Delegate that defines event handler for published message
        /// </summary>
        public delegate void MqttMsgPublishedEventHandler(object sender, MqttMsgPublishedEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for subscribed topic
        /// </summary>
        public delegate void MqttMsgSubscribedEventHandler(object sender, MqttMsgSubscribedEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for unsubscribed topic
        /// </summary>
        public delegate void MqttMsgUnsubscribedEventHandler(object sender, MqttMsgUnsubscribedEventArgs e);

#if BROKER
        /// <summary>
        /// Delagate that defines event handler for SUBSCRIBE message received
        /// </summary>
        public delegate void MqttMsgSubscribeEventHandler(object sender, MqttMsgSubscribeEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for UNSUBSCRIBE message received
        /// </summary>
        public delegate void MqttMsgUnsubscribeEventHandler(object sender, MqttMsgUnsubscribeEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for CONNECT message received
        /// </summary>
        public delegate void MqttMsgConnectEventHandler(object sender, MqttMsgConnectEventArgs e);

        /// <summary>
        /// Delegate that defines event handler for client disconnection (DISCONNECT message or not)
        /// </summary>
        public delegate void MqttMsgDisconnectEventHandler(object sender, EventArgs e);
#endif

        public delegate void MqttLibraryExceptionEventHandler(object sender, MqttLibraryExceptionEventArgs e);

        // broker hostname (or ip address) and port
        private string brokerHostName;
        private int brokerPort;

        // running status of threads
        private bool isRunning;
        // event for raising received message event
        private AutoResetEvent receiveEventWaitHandle;

        // event for starting process inflight queue asynchronously
        private AutoResetEvent inflightWaitHandle;

        // event for signaling synchronous receive
        AutoResetEvent syncEndReceiving;
        // message received
        MqttMsgBase msgReceived;

        // exeption thrown during receiving
        Exception exReceiving;

        // keep alive period (in ms)
        private int keepAlivePeriod;
        // events for signaling on keep alive thread
        private AutoResetEvent keepAliveEvent;
        // last communication time in ticks
        private int lastCommTime;

        // event for PUBLISH message received
        public event MqttMsgPublishEventHandler MqttMsgPublishReceived;
        // event for published message
        public event MqttMsgPublishedEventHandler MqttMsgPublished;
        // event for published message
        public event MqttMsgPublishedEventHandler MqttMsgSentToSocket;
        // event for subscribed topic
        public event MqttMsgSubscribedEventHandler MqttMsgSubscribed;
        // event for unsubscribed topic
        public event MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed;
#if BROKER
        // event for SUBSCRIBE message received
        public event MqttMsgSubscribeEventHandler MqttMsgSubscribeReceived;
        // event for USUBSCRIBE message received
        public event MqttMsgUnsubscribeEventHandler MqttMsgUnsubscribeReceived;
        // event for CONNECT message received
        public event MqttMsgConnectEventHandler MqttMsgConnected;
        // event for DISCONNECT message received
        public event MqttMsgDisconnectEventHandler MqttMsgDisconnected;
#endif

        public event MqttLibraryExceptionEventHandler MqttLibraryExceptionOccurred;

#if COMPACT_FRAMEWORK
        private TlsClient tlsClient;
#endif
        
        // channel to communicate over the network
        private IMqttNetworkChannel channel;

        // inflight messages queue
        private Queue inflightQueue;
        // internal queue for received messages about inflight messages
        private Queue internalQueue;
        // internal queue for dispatching events
        private Queue eventQueue;
        // session
        private MqttClientSession session;

        // reference to avoid access to singleton via property
        private MqttSettings settings;

        // current message identifier generated
        private ushort messageIdCounter = 0;

        private Thread receiveThread;

        private Thread keepAliveThread;

        private Thread dispatchEventThread;

        private Thread processInflightThread;

        private readonly object _connectionLock = new object();

        private readonly bool _isManualAcknowledge;

        private readonly object _messageIdCounterLock = new object();

        /// <summary>
        /// Connection status between client and broker
        /// </summary>
        public bool IsConnected { get; private set; }

        public bool IsReceiving
        {
            get { return this.channel.IsReceiving; }
        }

        /// <summary>
        /// Client identifier
        /// </summary>
        public string ClientId { get; private set; }

        /// <summary>
        /// Clean session flag
        /// </summary>
        public bool CleanSession { get; private set; }

        /// <summary>
        /// Will flag
        /// </summary>
        public bool WillFlag { get; private set; }

        /// <summary>
        /// Will QOS level
        /// </summary>
        public byte WillQosLevel { get; private set; }

        /// <summary>
        /// Will topic
        /// </summary>
        public string WillTopic { get; private set; }

        /// <summary>
        /// Will message
        /// </summary>
        public string WillMessage { get; private set; }

        /// <summary>
        /// MQTT protocol version
        /// </summary>
        public MqttProtocolVersion ProtocolVersion { get; set; }

#if BROKER
        /// <summary>
        /// MQTT Client Session
        /// </summary>
        public MqttClientSession Session
        {
            get { return this.session; }
            set { this.session = value; }
        }
#endif

        /// <summary>
        /// MQTT client settings
        /// </summary>
        public MqttSettings Settings
        {
            get { return this.settings; }
        }

#if !(WINDOWS_APP || WINDOWS_PHONE_APP) 
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerIpAddress">Broker IP address</param>
        /// <param name="isManualAcknowledge">Manually ack received messages</param>
        [Obsolete("Use this ctor MqttClient(string brokerHostName) insted")]
        public MqttClient(IPAddress brokerIpAddress, bool isManualAcknowledge) :
            this(brokerIpAddress, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, null, null, MqttSslProtocols.None, isManualAcknowledge)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerIpAddress">Broker IP address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="isManualAcknowledge">Manually ack received messages</param>
        [Obsolete("Use this ctor MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert) insted")]
        public MqttClient(IPAddress brokerIpAddress, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol, bool isManualAcknowledge)
        {
            _isManualAcknowledge = isManualAcknowledge;
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            this.Init(brokerIpAddress.ToString(), brokerPort, secure, caCert, clientCert, sslProtocol, null, null);
#elif COMPACT_FRAMEWORK
            this.Init(brokerIpAddress.ToString(), brokerPort, secure, caCert, clientCert, sslProtocol, new BasicTlsClient());
#else
            this.Init(brokerIpAddress.ToString(), brokerPort, secure, caCert, clientCert, sslProtocol);
#endif
        }
#endif

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="isManualAcknowledge">Manually ack received messages</param>
        public MqttClient(string brokerHostName, bool isManualAcknowledge) :
#if !(WINDOWS_APP || WINDOWS_PHONE_APP || COMPACT_FRAMEWORK)
            this(brokerHostName, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, null, null, MqttSslProtocols.None, isManualAcknowledge)
#else
            this(brokerHostName, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, null, null, MqttSslProtocols.None, null, isManualAcknowledge)
#endif
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(WINDOWS_APP || WINDOWS_PHONE_APP || COMPACT_FRAMEWORK)
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol, bool isManualAcknowledge)
#elif COMPACT_FRAMEWORK
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="tlsClient">Bouncy Castle Tls Client - set null to use default client (no client certificate checks)</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol, TlsClient tlsClient, bool isManualAcknowledge)
#else
        public MqttClient(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol, bool isManualAcknowledge)            
#endif
        {
            _isManualAcknowledge = isManualAcknowledge;
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, null, null);
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
            this.Init(brokerHostName, brokerPort, secure, sslProtocol);
#elif COMPACT_FRAMEWORK
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, tlsClient);
#else
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol);
#endif
        }


#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback, bool manualAknowledge)
            : this(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, null, manualAknowledge)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateSelectionCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol, 
            RemoteCertificateValidationCallback userCertificateValidationCallback, 
            LocalCertificateSelectionCallback userCertificateSelectionCallback, bool isManualAcknowledge)
            : this(brokerHostName, brokerPort, secure, null, null, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback, isManualAcknowledge)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateSelectionCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback, bool isManualAcknowledge)
        {
            _isManualAcknowledge = isManualAcknowledge;
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback);
        }
#endif

#if BROKER
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="channel">Network channel for communication</param>
        public MqttClient(IMqttNetworkChannel channel)
        {
            // set default MQTT protocol version (default is 3.1.1)
            this.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;

            this.channel = channel;

            // reference to MQTT settings
            this.settings = MqttSettings.Instance;

            // client not connected yet (CONNACK not send from client), some default values
            this.IsConnected = false;
            this.ClientId = null;
            this.CleanSession = true;

            this.keepAliveEvent = new AutoResetEvent(false);

            // queue for handling inflight messages (publishing and acknowledge)
            this.inflightWaitHandle = new AutoResetEvent(false);
            this.inflightQueue = new Queue();

            // queue for received message
            this.receiveEventWaitHandle = new AutoResetEvent(false);
            this.eventQueue = new Queue();
            this.internalQueue = new Queue();

            // session
            this.session = null;
        }
#endif

        /// <summary>
        /// MqttClient initialization
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">>Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
        /// <param name="userCertificateSelectionCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateValidationCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        private void Init(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
        private void Init(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol)
#else
        private void Init(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol, TlsClient tlsClient)
#endif
        {
#if USEMONITOREDLOCK
                //!!!to log the lock message uncomment following code!!!
                //!!! this will slow down the entire plc/ ecc and the file will grow fast !!!
                var lockLogTxt = "lockLogM2Mqtt.txt";
                if (File.Exists(lockLogTxt))
                {
                    File.Delete(lockLogTxt);
                }
                var locklogTxtLock = new object();
                MonitoredLock.EventLogMessage += (lvl, msg, ignoredId) =>
                                                 {
                                                     var line = string.Format("{0};{1,10};{2};{3}\n", DateTime.UtcNow.ToString("O"), lvl, msg, ignoredId);

                                                     //Console.WriteLine(line);

                                                     lock (locklogTxtLock)
                                                     {
                                                         //diskAccessProvider.WriteFile(lockLogTxt, FileMode.Append, line);
                                                         const int bufferSize = 1024;
                                                         using (var fileStream = new FileStream(lockLogTxt, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize))
                                                         {
                                                             using (var writer = new StreamWriter(fileStream))
                                                             {
                                                                 writer.AutoFlush = true;
                                                                 writer.Write(line);
                                                             }
                                                         }
                                                     }
                                                 };

#endif


            // set default MQTT protocol version (default is 3.1.1)
            this.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;
#if !SSL
            // check security parameters
            if (secure)
                throw new ArgumentException("Library compiled without SSL support");
#endif

            this.brokerHostName = brokerHostName;
            this.brokerPort = brokerPort;

            // reference to MQTT settings
            this.settings = MqttSettings.Instance;
            // set settings port based on secure connection or not
            if (!secure)
                this.settings.Port = this.brokerPort;
            else
                this.settings.SslPort = this.brokerPort;

#if COMPACT_FRAMEWORK
            this.tlsClient = tlsClient;
#endif

            this.syncEndReceiving = new AutoResetEvent(false);
            this.keepAliveEvent = new AutoResetEvent(false);

            // queue for handling inflight messages (publishing and acknowledge)
            this.inflightWaitHandle = new AutoResetEvent(false);
            this.inflightQueue = new Queue();

            // queue for received message
            this.receiveEventWaitHandle = new AutoResetEvent(false);
            this.eventQueue = new Queue();
            this.internalQueue = new Queue();

            // session
            this.session = null;

            // create network channel
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback);
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, sslProtocol);
#else
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, caCert, clientCert, sslProtocol, this.tlsClient);
#endif
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId)
        {
            return this.Connect(clientId, null, null, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, true, MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password)
        {
            return this.Connect(clientId, username, password, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, true, MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <param name="cleanSession">Clean sessione flag</param>
        /// <param name="keepAlivePeriod">Keep alive period</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password,
            bool cleanSession,
            ushort keepAlivePeriod)
        {
            return this.Connect(clientId, username, password, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, cleanSession, keepAlivePeriod);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <param name="willRetain">Will retain flag</param>
        /// <param name="willQosLevel">Will QOS level</param>
        /// <param name="willFlag">Will flag</param>
        /// <param name="willTopic">Will topic</param>
        /// <param name="willMessage">Will message</param>
        /// <param name="cleanSession">Clean sessione flag</param>
        /// <param name="keepAlivePeriod">Keep alive period</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password,
            bool willRetain,
            byte willQosLevel,
            bool willFlag,
            string willTopic,
            string willMessage,
            bool cleanSession,
            ushort keepAlivePeriod)
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this._connectionLock, "MqttClient.Connect"))
#else
            lock (this._connectionLock)
#endif
            {
                // create CONNECT message
                MqttMsgConnect connect = new MqttMsgConnect(clientId,
                                                            username,
                                                            password,
                                                            willRetain,
                                                            willQosLevel,
                                                            willFlag,
                                                            willTopic,
                                                            willMessage,
                                                            cleanSession,
                                                            keepAlivePeriod,
                                                            (byte)this.ProtocolVersion);

                try
                {
                    // connect to the broker
                    this.channel.Connect();
                }
                catch (Exception ex)
                {
                    throw new MqttConnectionException("Exception connecting to the broker", ex);
                }

                this.lastCommTime = 0;
                this.isRunning = true;
                this.ClientId = clientId;

                // start thread for receiving messages from broker
                this.receiveThread = Fx.StartThread(this.ReceiveThread);

                MqttMsgConnack connack = (MqttMsgConnack)this.SendReceive(connect);

                // if connection accepted, start keep alive timer and 
                if (connack.ReturnCode == MqttMsgConnack.CONN_ACCEPTED)
                {
                    // set all client properties
                    this.CleanSession = cleanSession;
                    this.WillFlag = willFlag;
                    this.WillTopic = willTopic;
                    this.WillMessage = willMessage;
                    this.WillQosLevel = willQosLevel;

                    this.keepAlivePeriod = keepAlivePeriod * 1000; // convert in ms

                    // restore previous session
                    this.RestoreSession();

                    // keep alive period equals zero means turning off keep alive mechanism
                    if (this.keepAlivePeriod != 0)
                    {
                        // start thread for sending keep alive message to the broker
                        this.keepAliveThread = Fx.StartThread(this.KeepAliveThread);
                    }

                    // start thread for raising received message event from broker
                    this.dispatchEventThread = Fx.StartThread(this.DispatchEventThread);

                    // start thread for handling inflight messages queue to broker asynchronously (publish and acknowledge)
                    this.processInflightThread = Fx.StartThread(this.ProcessInflightThread);

                    this.IsConnected = true;
                }

                return connack.ReturnCode;
            }
        }

        /// <summary>
        /// Disconnect from broker
        /// </summary>
        public void Disconnect()
        {
            MqttMsgDisconnect disconnect = new MqttMsgDisconnect();

            this.EnqueueInflight(disconnect, MqttMsgFlow.ToPublish);

            this.Close();
        }

#if BROKER
        /// <summary>
        /// Open client communication
        /// </summary>
        public void Open()
        {
            this.isRunning = true;

            // start thread for receiving messages from client
            Fx.StartThread(this.ReceiveThread);

            // start thread for raising received message event from client
            Fx.StartThread(this.DispatchEventThread);

            // start thread for handling inflight messages queue to client asynchronously (publish and acknowledge)
            Fx.StartThread(this.ProcessInflightThread);   
        }
#endif

        /// <summary>
        /// Close client
        /// </summary>
#if BROKER
        public void Close()
#else
        private void Close()
#endif
        {
            // stop all threads
#if USEMONITOREDLOCK
            using (new MonitoredLock(this, "MqttClient.Close"))
#else
            lock (this)
#endif
            {
                this.isRunning = false;
            }

            // notify handles so that threads can end
            if (this.receiveEventWaitHandle != null)
                this.receiveEventWaitHandle.Set();
            if (this.inflightWaitHandle != null)
                this.inflightWaitHandle.Set();
#if BROKER
            // unlock keep alive thread
            this.keepAliveEvent.Set();
#else
            this.keepAliveEvent.Set();
#endif
            // join threads
            var isReceiveThreadJoined = this.receiveThread == null || this.receiveThread.Join(20000);
            if (!isReceiveThreadJoined)
            {
                try
                {
                    this.receiveThread.Abort();
                }
                catch 
                {
#if COMPACT_FRAMEWORK
                    throw;
#else
                    this.receiveThread.Interrupt();
#endif
                }
            }
            var isKeepAliveThreadJoined = this.keepAliveThread == null || this.keepAliveThread.Join(20000);
            if (!isKeepAliveThreadJoined)
            {
                try
                {
                    this.keepAliveThread.Abort();
                }
                catch
                {
#if COMPACT_FRAMEWORK
                    throw;
#else
                    this.keepAliveThread.Interrupt();
#endif
                }
            }
            var isDispatchEventThreadJoined = this.dispatchEventThread == null || this.dispatchEventThread.Join(20000);
            if (!isDispatchEventThreadJoined)
            {
                try
                {
                    this.dispatchEventThread.Abort();
                }
                catch
                {
#if COMPACT_FRAMEWORK
                    throw;
#else
                    this.dispatchEventThread.Interrupt();
#endif
                }
            }
            var isProcessInflightThreadJoined = this.processInflightThread == null || this.processInflightThread.Join(20000);
            if (!isProcessInflightThreadJoined)
            {
                try
                {
                    this.processInflightThread.Abort();
                }
                catch
                {
#if COMPACT_FRAMEWORK
                    throw;
#else
                    this.processInflightThread.Interrupt();
#endif
                }
            }

            this.receiveThread = null;
            this.keepAliveThread = null;
            this.dispatchEventThread = null;
            this.processInflightThread = null;

            // clear all queues
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.inflightQueue, "MqttClient.Close1 inflightQueue"))
#else
            lock (this.inflightQueue)
#endif
            {
                this.inflightQueue.Clear();
            }
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.internalQueue, "MqttClient.Close2 internalQueue"))
#else
            lock (this.internalQueue)
#endif
            {
                this.internalQueue.Clear();
            }
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.eventQueue, "MqttClient.Close3 eventQueue"))
#else
            lock (this.eventQueue)
#endif
            {
                this.eventQueue.Clear();
            }

#if USEMONITOREDLOCK
            using (new MonitoredLock(this._connectionLock, "MqttClient.Close4 _connectionLock"))
#else
            lock (_connectionLock)
#endif
            {
                // close network channel
                this.channel.Close();
                this.IsConnected = false;
            }
        }

        /// <summary>
        /// Execute ping to broker for keep alive
        /// </summary>
        /// <returns>PINGRESP message from broker</returns>
        private void Ping()
        {
            MqttMsgPingReq pingreq = new MqttMsgPingReq();
            try
            {
                // broker must send PINGRESP within timeout equal to keep alive period
                this.SendReceive(pingreq, this.keepAlivePeriod);
            }
            catch (Exception e)
            {
                OnMqttLibraryExceptionOccured(e);
            }
        }

#if BROKER
        /// <summary>
        /// Send CONNACK message to the client (connection accepted or not)
        /// </summary>
        /// <param name="connect">CONNECT message with all client information</param>
        /// <param name="returnCode">Return code for CONNACK message</param>
        /// <param name="clientId">If not null, client id assigned by broker</param>
        /// <param name="sessionPresent">Session present on the broker</param>
        public void Connack(MqttMsgConnect connect, byte returnCode, string clientId, bool sessionPresent)
        {
            this.lastCommTime = 0;

            // create CONNACK message and ...
            MqttMsgConnack connack = new MqttMsgConnack();
            connack.ReturnCode = returnCode;
            // [v3.1.1] session present flag
            if (this.ProtocolVersion == MqttProtocolVersion.Version_3_1_1)
                connack.SessionPresent = sessionPresent;
            // ... send it to the client
            this.Send(connack);

            // connection accepted, start keep alive thread checking
            if (connack.ReturnCode == MqttMsgConnack.CONN_ACCEPTED)
            {
                // [v3.1.1] if client id isn't null, the CONNECT message has a cliend id with zero bytes length
                //          and broker assigned a unique identifier to the client
                this.ClientId = (clientId == null) ? connect.ClientId : clientId;
                this.CleanSession = connect.CleanSession;
                this.WillFlag = connect.WillFlag;
                this.WillTopic = connect.WillTopic;
                this.WillMessage = connect.WillMessage;
                this.WillQosLevel = connect.WillQosLevel;

                this.keepAlivePeriod = connect.KeepAlivePeriod * 1000; // convert in ms
                // broker has a tolerance of 1.5 specified keep alive period
                this.keepAlivePeriod += (this.keepAlivePeriod / 2);

                // start thread for checking keep alive period timeout
                Fx.StartThread(this.KeepAliveThread);

                this.isConnectionClosing = false;
                this.IsConnected = true;
            }
            // connection refused, close TCP/IP channel
            else
            {
                this.Close();
            }
        }

        /// <summary>
        /// Send SUBACK message to the client
        /// </summary>
        /// <param name="messageId">Message Id for the SUBSCRIBE message that is being acknowledged</param>
        /// <param name="grantedQosLevels">Granted QoS Levels</param>
        public void Suback(ushort messageId, byte[] grantedQosLevels)
        {
            MqttMsgSuback suback = new MqttMsgSuback();
            suback.MessageId = messageId;
            suback.GrantedQoSLevels = grantedQosLevels;

            this.Send(suback);
        }

        /// <summary>
        /// Send UNSUBACK message to the client
        /// </summary>
        /// <param name="messageId">Message Id for the UNSUBSCRIBE message that is being acknowledged</param>
        public void Unsuback(ushort messageId)
        {
            MqttMsgUnsuback unsuback = new MqttMsgUnsuback();
            unsuback.MessageId = messageId;

            this.Send(unsuback);
        }
#endif

        /// <summary>
        /// Subscribe for message topics
        /// </summary>
        /// <param name="topics">List of topics to subscribe</param>
        /// <param name="qosLevels">QOS levels related to topics</param>
        /// <returns>Message Id related to SUBSCRIBE message</returns>
        public ushort Subscribe(string[] topics, byte[] qosLevels)
        {
            MqttMsgSubscribe subscribe =
                new MqttMsgSubscribe(topics, qosLevels);
            subscribe.MessageId = this.GetMessageId();

            // enqueue subscribe request into the inflight queue
            this.EnqueueInflight(subscribe, MqttMsgFlow.ToPublish);

            return subscribe.MessageId;
        }

        /// <summary>
        /// Unsubscribe for message topics
        /// </summary>
        /// <param name="topics">List of topics to unsubscribe</param>
        /// <returns>Message Id in UNSUBACK message from broker</returns>
        public ushort Unsubscribe(string[] topics)
        {
            MqttMsgUnsubscribe unsubscribe =
                new MqttMsgUnsubscribe(topics);
            unsubscribe.MessageId = this.GetMessageId();

            // enqueue unsubscribe request into the inflight queue
            this.EnqueueInflight(unsubscribe, MqttMsgFlow.ToPublish);

            return unsubscribe.MessageId;
        }

        /// <summary>
        /// Publish a message asynchronously (QoS Level 0 and not retained)
        /// </summary>
        /// <param name="topic">Message topic</param>
        /// <param name="message">Message data (payload)</param>
        /// <returns>Message Id related to PUBLISH message</returns>
        public ushort Publish(string topic, byte[] message)
        {
            return this.Publish(topic, message, MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE, false);
        }

        /// <summary>
        /// Publish a message asynchronously
        /// </summary>
        /// <param name="topic">Message topic</param>
        /// <param name="message">Message data (payload)</param>
        /// <param name="qosLevel">QoS Level</param>
        /// <param name="retain">Retain flag</param>
        /// <returns>Message Id related to PUBLISH message</returns>
        public ushort Publish(string topic, byte[] message, byte qosLevel, bool retain)
        {
            MqttMsgPublish publish =
                    new MqttMsgPublish(topic, message, false, qosLevel, retain);
            publish.MessageId = this.GetMessageId();

            // enqueue message to publish into the inflight queue
            bool enqueue = this.EnqueueInflight(publish, MqttMsgFlow.ToPublish);

            // message enqueued
            if (enqueue)
                return publish.MessageId;
            // infligh queue full, message not enqueued
            else
                throw new MqttClientException(MqttClientErrorCode.InflightQueueFull);
        }

        /// <summary>
        /// Wrapper method for raising events
        /// </summary>
        /// <param name="internalEvent">Internal event</param>
        private void OnInternalEvent(InternalEvent internalEvent)
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this.eventQueue, "MqttClient.OnInternalEvent eventQueue"))
#else
            lock (this.eventQueue)
#endif
            {
                this.eventQueue.Enqueue(internalEvent);
            }

            this.receiveEventWaitHandle.Set();
        }

        /// <summary>
        /// Wrapper method for raising PUBLISH message received event
        /// </summary>
        /// <param name="publish">PUBLISH message received</param>
        private void OnMqttMsgPublishReceived(MqttMsgPublish publish)
        {
            if (this.MqttMsgPublishReceived != null)
            {
                this.MqttMsgPublishReceived(this,
                    new MqttMsgPublishEventArgs(publish.Topic, publish.Message, publish.DupFlag, publish.QosLevel, publish.Retain, publish.MessageId));
            }
        }

        /// <summary>
        /// Wrapper method for raising published message event
        /// </summary>
        /// <param name="messageId">Message identifier for published message</param>
        /// <param name="isPublished">Publish flag</param>
        private void OnMqttMsgPublished(ushort messageId, bool isPublished)
        {
            if (this.MqttMsgPublished != null)
            {
                this.MqttMsgPublished(this,
                    new MqttMsgPublishedEventArgs(messageId, isPublished));
            }
        }
        /// <summary>
        /// Wrapper method for raising published message event
        /// </summary>
        /// <param name="messageId">Message identifier for published message</param>
        /// <param name="isPublished">Publish flag</param>
        private void OnMqttMessageSentToSocket(ushort messageId, bool isPublished)
        {
            if (this.MqttMsgSentToSocket != null)
            {
                this.MqttMsgSentToSocket(this, new MqttMsgPublishedEventArgs(messageId, isPublished));
            }
        }

        /// <summary>
        /// Wrapper method for raising subscribed topic event
        /// </summary>
        /// <param name="suback">SUBACK message received</param>
        private void OnMqttMsgSubscribed(MqttMsgSuback suback)
        {
            if (this.MqttMsgSubscribed != null)
            {
                this.MqttMsgSubscribed(this,
                    new MqttMsgSubscribedEventArgs(suback.MessageId, suback.GrantedQoSLevels));
            }
        }

        /// <summary>
        /// Wrapper method for raising unsubscribed topic event
        /// </summary>
        /// <param name="messageId">Message identifier for unsubscribed topic</param>
        private void OnMqttMsgUnsubscribed(ushort messageId)
        {
            if (this.MqttMsgUnsubscribed != null)
            {
                this.MqttMsgUnsubscribed(this,
                    new MqttMsgUnsubscribedEventArgs(messageId));
            }
        }

#if BROKER
        /// <summary>
        /// Wrapper method for raising SUBSCRIBE message event
        /// </summary>
        /// <param name="messageId">Message identifier for subscribe topics request</param>
        /// <param name="topics">Topics requested to subscribe</param>
        /// <param name="qosLevels">List of QOS Levels requested</param>
        private void OnMqttMsgSubscribeReceived(ushort messageId, string[] topics, byte[] qosLevels)
        {
            if (this.MqttMsgSubscribeReceived != null)
            {
                this.MqttMsgSubscribeReceived(this,
                    new MqttMsgSubscribeEventArgs(messageId, topics, qosLevels));
            }
        }

        /// <summary>
        /// Wrapper method for raising UNSUBSCRIBE message event
        /// </summary>
        /// <param name="messageId">Message identifier for unsubscribe topics request</param>
        /// <param name="topics">Topics requested to unsubscribe</param>
        private void OnMqttMsgUnsubscribeReceived(ushort messageId, string[] topics)
        {
            if (this.MqttMsgUnsubscribeReceived != null)
            {
                this.MqttMsgUnsubscribeReceived(this,
                    new MqttMsgUnsubscribeEventArgs(messageId, topics));
            }
        }

        /// <summary>
        /// Wrapper method for raising CONNECT message event
        /// </summary>
        private void OnMqttMsgConnected(MqttMsgConnect connect)
        {
            if (this.MqttMsgConnected != null)
            {
                this.ProtocolVersion = (MqttProtocolVersion)connect.ProtocolVersion;
                this.MqttMsgConnected(this, new MqttMsgConnectEventArgs(connect));
            }
        }

        /// <summary>
        /// Wrapper method for raising DISCONNECT message event
        /// </summary>
        private void OnMqttMsgDisconnected()
        {
            if (this.MqttMsgDisconnected != null)
            {
                this.MqttMsgDisconnected(this, EventArgs.Empty);
            }
        }
#endif

        /// <summary>
        /// Wrapper method for peer/client disconnection
        /// </summary>
        private void OnMqttLibraryExceptionOccured(Exception exception)
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this._connectionLock, "MqttClient.OnMqttLibraryExceptionOccured _connectionLock"))
#else
            lock (this._connectionLock)
#endif
            {
                if (!IsConnected)
                {
                    return;
                }

                IsConnected = false;
            }

            if (this.MqttLibraryExceptionOccurred == null)
            {
                return;
            }

            var mqttLibraryExceptionEventArgs = new MqttLibraryExceptionEventArgs { Exception = exception };
            this.MqttLibraryExceptionOccurred(this, mqttLibraryExceptionEventArgs);
        }

        /// <summary>
        /// Send a message
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        private void Send(byte[] msgBytes)
        {
            try
            {
                // send message
                this.channel.Send(msgBytes);

#if !BROKER
                // update last message sent ticks
                this.lastCommTime = Environment.TickCount;
#endif
            }
            catch (Exception e)
            {
#if TRACE
                MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif

                throw new MqttCommunicationException(e);
            }
        }

        /// <summary>
        /// Send a message
        /// </summary>
        /// <param name="msg">Message</param>
        private void Send(MqttMsgBase msg)
        {
#if TRACE
            MqttUtility.Trace.WriteLine(TraceLevel.Frame, "SEND {0}", msg);
#endif
            this.Send(msg.GetBytes((byte)this.ProtocolVersion));
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(byte[] msgBytes)
        {
            return this.SendReceive(msgBytes, MqttSettings.MQTT_DEFAULT_TIMEOUT);
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        /// <param name="timeout">Timeout for receiving answer</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(byte[] msgBytes, int timeout)
        {
            // reset handle before sending
            this.syncEndReceiving.Reset();

            // send message
            this.channel.Send(msgBytes);

            // update last message sent ticks
            this.lastCommTime = Environment.TickCount;

#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            // wait for answer from broker
            if (this.syncEndReceiving.WaitOne(timeout, false))
#else
            // wait for answer from broker
            if (this.syncEndReceiving.WaitOne(timeout))
#endif
            {
                // message received without exception
                if (this.exReceiving == null)
                    return this.msgReceived;
                // receiving thread catched exception
                else
                    throw this.exReceiving;
            }
            else
            {
                // throw timeout exception
                throw new MqttCommunicationException();
            }
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msg">Message</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(MqttMsgBase msg)
        {
            return this.SendReceive(msg, MqttSettings.MQTT_DEFAULT_TIMEOUT);
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="timeout">Timeout for receiving answer</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(MqttMsgBase msg, int timeout)
        {
#if TRACE
            MqttUtility.Trace.WriteLine(TraceLevel.Frame, "SEND {0}", msg);
#endif
            return this.SendReceive(msg.GetBytes((byte)this.ProtocolVersion), timeout);
        }

        /// <summary>
        /// Enqueue a message into the inflight queue
        /// </summary>
        /// <param name="msg">Message to enqueue</param>
        /// <param name="flow">Message flow (publish, acknowledge)</param>
        /// <returns>Message enqueued or not</returns>
        private bool EnqueueInflight(MqttMsgBase msg, MqttMsgFlow flow)
        {
            // enqueue is needed (or not)
            bool enqueue = true;

            // if it is a PUBLISH message with QoS Level 2
            if ((msg.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE))
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.inflightQueue, "MqttClient.EnqueueInflight inflightQueue"))
#else
                lock (this.inflightQueue)
#endif
                {
                    // if it is a PUBLISH message already received (it is in the inflight queue), the publisher
                    // re-sent it because it didn't received the PUBREC. In this case, we have to re-send PUBREC

                    // NOTE : I need to find on message id and flow because the broker could be publish/received
                    //        to/from client and message id could be the same (one tracked by broker and the other by client)
                    MqttMsgContextFinder msgCtxFinder = new MqttMsgContextFinder(msg.MessageId, MqttMsgFlow.ToAcknowledge);
                    MqttMsgContext msgCtx = (MqttMsgContext)this.inflightQueue.Get(msgCtxFinder.Find);

                    // the PUBLISH message is alredy in the inflight queue, we don't need to re-enqueue but we need
                    // to change state to re-send PUBREC
                    if (msgCtx != null)
                    {
                        msgCtx.State = MqttMsgState.QueuedQos2;
                        msgCtx.Flow = MqttMsgFlow.ToAcknowledge;
                        enqueue = false;
                    }
                }
            }

            if (enqueue)
            {
                // set a default state
                MqttMsgState state = MqttMsgState.QueuedQos0;

                // based on QoS level, the messages flow between broker and client changes
                switch (msg.QosLevel)
                {
                    // QoS Level 0
                    case MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE:

                        state = MqttMsgState.QueuedQos0;
                        break;

                    // QoS Level 1
                    case MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE:

                        state = MqttMsgState.QueuedQos1;
                        break;

                    // QoS Level 2
                    case MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE:

                        state = MqttMsgState.QueuedQos2;
                        break;
                }

                // [v3.1.1] SUBSCRIBE and UNSUBSCRIBE aren't "officially" QOS = 1
                //          so QueuedQos1 state isn't valid for them
                if (msg.Type == MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE)
                    state = MqttMsgState.SendSubscribe;
                else if (msg.Type == MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE)
                    state = MqttMsgState.SendUnsubscribe;

                // queue message context
                MqttMsgContext msgContext = new MqttMsgContext()
                {
                    Message = msg,
                    State = state,
                    Flow = flow,
                    Attempt = 0
                };

#if USEMONITOREDLOCK
                using (new MonitoredLock(this.inflightQueue, "MqttClient.EnqueueInflight1 inflightQueue"))
#else
                lock (this.inflightQueue)
#endif
                {
                    // check number of messages inside inflight queue 
                    enqueue = (this.inflightQueue.Count < this.settings.InflightQueueSize || msg.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE);

                    if (enqueue)
                    {
                        // enqueue message and unlock send thread
                        this.inflightQueue.Enqueue(msgContext);

#if TRACE
                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "enqueued {0}", msg);
#endif

                        // PUBLISH message
                        if (msg.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE)
                        {
                            // to publish and QoS level 1 or 2
                            if ((msgContext.Flow == MqttMsgFlow.ToPublish) &&
                                ((msg.QosLevel == MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE) ||
                                 (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE)))
                            {
                                if (this.session != null)
                                    this.session.InflightMessages.Add(msgContext.Key, msgContext);
                            }
                            // to acknowledge and QoS level 2
                            else if ((msgContext.Flow == MqttMsgFlow.ToAcknowledge) &&
                                     (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE))
                            {
                                if (this.session != null)
                                    this.session.InflightMessages.Add(msgContext.Key, msgContext);
                            }
                        }
                    }
                }
            }

            this.inflightWaitHandle.Set();

            return enqueue;
        }

        /// <summary>
        /// Enqueue a message into the internal queue
        /// </summary>
        /// <param name="msg">Message to enqueue</param>
        private void EnqueueInternal(MqttMsgBase msg)
        {
            // enqueue is needed (or not)
            bool enqueue = true;

            // if it is a PUBREL message (for QoS Level 2)
            if (msg.Type == MqttMsgBase.MQTT_MSG_PUBREL_TYPE)
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.inflightQueue, "MqttClient.EnqueueInternal inflightQueue"))
#else
                lock (this.inflightQueue)
#endif
                {
                    // if it is a PUBREL but the corresponding PUBLISH isn't in the inflight queue,
                    // it means that we processed PUBLISH message and received PUBREL and we sent PUBCOMP
                    // but publisher didn't receive PUBCOMP so it re-sent PUBREL. We need only to re-send PUBCOMP.

                    // NOTE : I need to find on message id and flow because the broker could be publish/received
                    //        to/from client and message id could be the same (one tracked by broker and the other by client)
                    MqttMsgContextFinder msgCtxFinder = new MqttMsgContextFinder(msg.MessageId, MqttMsgFlow.ToAcknowledge);
                    MqttMsgContext msgCtx = (MqttMsgContext)this.inflightQueue.Get(msgCtxFinder.Find);

                    // the PUBLISH message isn't in the inflight queue, it was already processed so
                    // we need to re-send PUBCOMP only
                    if (msgCtx == null)
                    {
                        MqttMsgPubcomp pubcomp = new MqttMsgPubcomp();
                        pubcomp.MessageId = msg.MessageId;

                        this.Send(pubcomp);

                        enqueue = false;
                    }
                }
            }
            // if it is a PUBCOMP message (for QoS Level 2)
            else if (msg.Type == MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE)
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.inflightQueue, "MqttClient.EnqueueInternal1 inflightQueue"))
#else
                lock (this.inflightQueue)
#endif
                {
                    // if it is a PUBCOMP but the corresponding PUBLISH isn't in the inflight queue,
                    // it means that we sent PUBLISH message, sent PUBREL (after receiving PUBREC) and already received PUBCOMP
                    // but publisher didn't receive PUBREL so it re-sent PUBCOMP. We need only to ignore this PUBCOMP.

                    // NOTE : I need to find on message id and flow because the broker could be publish/received
                    //        to/from client and message id could be the same (one tracked by broker and the other by client)
                    MqttMsgContextFinder msgCtxFinder = new MqttMsgContextFinder(msg.MessageId, MqttMsgFlow.ToPublish);
                    MqttMsgContext msgCtx = (MqttMsgContext)this.inflightQueue.Get(msgCtxFinder.Find);

                    // the PUBLISH message isn't in the inflight queue, it was already sent so we need to ignore this PUBCOMP
                    if (msgCtx == null)
                    {
                        enqueue = false;
                    }
                }
            }
            // if it is a PUBREC message (for QoS Level 2)
            else if (msg.Type == MqttMsgBase.MQTT_MSG_PUBREC_TYPE)
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.inflightQueue, "MqttClient.EnqueueInternal2 inflightQueue"))
#else
                lock (this.inflightQueue)
#endif
                {
                    // if it is a PUBREC but the corresponding PUBLISH isn't in the inflight queue,
                    // it means that we sent PUBLISH message more times (retries) but broker didn't send PUBREC in time
                    // the publish is failed and we need only to ignore this PUBREC.

                    // NOTE : I need to find on message id and flow because the broker could be publish/received
                    //        to/from client and message id could be the same (one tracked by broker and the other by client)
                    MqttMsgContextFinder msgCtxFinder = new MqttMsgContextFinder(msg.MessageId, MqttMsgFlow.ToPublish);
                    MqttMsgContext msgCtx = (MqttMsgContext)this.inflightQueue.Get(msgCtxFinder.Find);

                    // the PUBLISH message isn't in the inflight queue, it was already sent so we need to ignore this PUBREC
                    if (msgCtx == null)
                    {
                        enqueue = false;
                    }
                }
            }

            if (enqueue)
            {
#if USEMONITOREDLOCK
                using (new MonitoredLock(this.internalQueue, "MqttClient.EnqueueInternal3 internalQueue"))
#else
                lock (this.internalQueue)
#endif
                {
                    this.internalQueue.Enqueue(msg);
#if TRACE
                    MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "enqueued {0}", msg);
#endif
                    this.inflightWaitHandle.Set();
                }
            }
        }

        /// <summary>
        /// Thread for receiving messages
        /// </summary>
        private void ReceiveThread()
        {
            Thread.CurrentThread.Name = ClientId + "ReceiveThread";
            int readBytes = 0;
            byte[] fixedHeaderFirstByte = new byte[1];
            byte msgType;

            while (this.isRunning)
            {
                try
                {
                    // read first byte (fixed header)
                    readBytes = this.channel.Receive(fixedHeaderFirstByte, 5000);

                    if (readBytes > 0)
                    {
#if BROKER
                        // update last message received ticks
                        this.lastCommTime = Environment.TickCount;
#endif

                        // extract message type from received byte
                        msgType = (byte)((fixedHeaderFirstByte[0] & MqttMsgBase.MSG_TYPE_MASK) >> MqttMsgBase.MSG_TYPE_OFFSET);

                        switch (msgType)
                        {
                            // CONNECT message received
                            case MqttMsgBase.MQTT_MSG_CONNECT_TYPE:

#if BROKER
                                MqttMsgConnect connect = MqttMsgConnect.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", connect);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(connect));
                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif
                                
                            // CONNACK message received
                            case MqttMsgBase.MQTT_MSG_CONNACK_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                this.msgReceived = MqttMsgConnack.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif
                                this.syncEndReceiving.Set();
                                break;
#endif

                            // PINGREQ message received
                            case MqttMsgBase.MQTT_MSG_PINGREQ_TYPE:

#if BROKER
                                this.msgReceived = MqttMsgPingReq.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif

                                MqttMsgPingResp pingresp = new MqttMsgPingResp();
                                this.Send(pingresp);

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            // PINGRESP message received
                            case MqttMsgBase.MQTT_MSG_PINGRESP_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                this.msgReceived = MqttMsgPingResp.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif
                                this.syncEndReceiving.Set();
                                break;
#endif

                            // SUBSCRIBE message received
                            case MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE:

#if BROKER
                                MqttMsgSubscribe subscribe = MqttMsgSubscribe.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", subscribe);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(subscribe));

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            // SUBACK message received
                            case MqttMsgBase.MQTT_MSG_SUBACK_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                // enqueue SUBACK message received (for QoS Level 1) into the internal queue
                                MqttMsgSuback suback = MqttMsgSuback.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", suback);
#endif

                                // enqueue SUBACK message into the internal queue
                                this.EnqueueInternal(suback);

                                break;
#endif

                            // PUBLISH message received
                            case MqttMsgBase.MQTT_MSG_PUBLISH_TYPE:

                                MqttMsgPublish publish = MqttMsgPublish.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", publish);
#endif

                                // enqueue PUBLISH message to acknowledge into the inflight queue
                                this.EnqueueInflight(publish, MqttMsgFlow.ToAcknowledge);

                                break;

                            // PUBACK message received
                            case MqttMsgBase.MQTT_MSG_PUBACK_TYPE:

                                // enqueue PUBACK message received (for QoS Level 1) into the internal queue
                                MqttMsgPuback puback = MqttMsgPuback.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", puback);
#endif

                                // enqueue PUBACK message into the internal queue
                                this.EnqueueInternal(puback);

                                break;

                            // PUBREC message received
                            case MqttMsgBase.MQTT_MSG_PUBREC_TYPE:

                                // enqueue PUBREC message received (for QoS Level 2) into the internal queue
                                MqttMsgPubrec pubrec = MqttMsgPubrec.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubrec);
#endif

                                // enqueue PUBREC message into the internal queue
                                this.EnqueueInternal(pubrec);

                                break;

                            // PUBREL message received
                            case MqttMsgBase.MQTT_MSG_PUBREL_TYPE:

                                // enqueue PUBREL message received (for QoS Level 2) into the internal queue
                                MqttMsgPubrel pubrel = MqttMsgPubrel.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubrel);
#endif

                                // enqueue PUBREL message into the internal queue
                                this.EnqueueInternal(pubrel);

                                break;
                                
                            // PUBCOMP message received
                            case MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE:

                                // enqueue PUBCOMP message received (for QoS Level 2) into the internal queue
                                MqttMsgPubcomp pubcomp = MqttMsgPubcomp.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubcomp);
#endif

                                // enqueue PUBCOMP message into the internal queue
                                this.EnqueueInternal(pubcomp);

                                break;

                            // UNSUBSCRIBE message received
                            case MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE:

#if BROKER
                                MqttMsgUnsubscribe unsubscribe = MqttMsgUnsubscribe.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", unsubscribe);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(unsubscribe));

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            // UNSUBACK message received
                            case MqttMsgBase.MQTT_MSG_UNSUBACK_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                // enqueue UNSUBACK message received (for QoS Level 1) into the internal queue
                                MqttMsgUnsuback unsuback = MqttMsgUnsuback.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", unsuback);
#endif

                                // enqueue UNSUBACK message into the internal queue
                                this.EnqueueInternal(unsuback);

                                break;
#endif

                            // DISCONNECT message received
                            case MqttMsgDisconnect.MQTT_MSG_DISCONNECT_TYPE:

#if BROKER
                                MqttMsgDisconnect disconnect = MqttMsgDisconnect.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", disconnect);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(disconnect));

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            default:

                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
                        }

                        this.exReceiving = null;
                    }
                    // zero bytes read, peer gracefully closed socket
                    else
                    {
                        // this happens if timeout on read elapsed and may occur if the server does not send anything....
                    }
                }
                catch (Exception e)
                {
                    OnMqttLibraryExceptionOccured(e);
                }
            }
        }

        /// <summary>
        /// Thread for handling keep alive message
        /// </summary>
        private void KeepAliveThread()
        {
            Thread.CurrentThread.Name = ClientId + "KeepAliveThread";
            int delta = 0;
            int wait = this.keepAlivePeriod;

            while (this.isRunning)
            {
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                // waiting...
                this.keepAliveEvent.WaitOne(wait, false);
#else
                // waiting...
                this.keepAliveEvent.WaitOne(wait);
#endif

                if (this.isRunning)
                {
                    delta = Environment.TickCount - this.lastCommTime;

                    // if timeout exceeded ...
                    if (delta >= this.keepAlivePeriod)
                    {
#if BROKER
                        // client must close connection
                        this.OnConnectionClosing();
#else
                        // ... send keep alive
						this.Ping();
						wait = this.keepAlivePeriod;
#endif
                    }
                    else
                    {
                        // update waiting time
                        wait = this.keepAlivePeriod - delta;
                    }
                }
            }
        }

        /// <summary>
        /// Thread for raising event
        /// </summary>
        private void DispatchEventThreadInternal()
        {
            while (this.isRunning)
            {
#if BROKER
                if ((this.eventQueue.Count == 0) && !this.isConnectionClosing)
                {
                    // broker need to receive the first message (CONNECT)
                    // within a reasonable amount of time after TCP/IP connection
                    if (!this.IsConnected)
                    {
                        // wait on receiving message from client with a connection timeout
                        if (!this.receiveEventWaitHandle.WaitOne(this.settings.TimeoutOnConnection))
                        {
                            // client must close connection
                            this.Close();

                            // client raw disconnection
                            this.OnConnectionClosed();
                        }
                    }
                    else
                    {
                        // wait on receiving message from client
                        this.receiveEventWaitHandle.WaitOne();
                    }
                }
#else
                if ((this.eventQueue.Count == 0) && this.isRunning)
                    // wait on receiving message from client
                    this.receiveEventWaitHandle.WaitOne();
#endif

                // check if it is running or we are closing client
                if (this.isRunning)
                {
                    // get event from queue
                    InternalEvent internalEvent = null;
#if USEMONITOREDLOCK
                    using (new MonitoredLock(this.eventQueue, "MqttClient.DispatchEventThreadInternal eventQueue"))
#else
                    lock (this.eventQueue)
#endif
                    {
                        if (this.eventQueue.Count > 0)
                            internalEvent = (InternalEvent)this.eventQueue.Dequeue();
                    }

                    // it's an event with a message inside
                    if (internalEvent != null)
                    {
                        MqttMsgBase msg = ((MsgInternalEvent)internalEvent).Message;

                        if (msg != null)
                        {
                            switch (msg.Type)
                            {
                                // CONNECT message received
                                case MqttMsgBase.MQTT_MSG_CONNECT_TYPE:

#if BROKER
                                    // raise connected client event (CONNECT message received)
                                    this.OnMqttMsgConnected((MqttMsgConnect)msg);
                                    break;
#else
                                    throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                                // SUBSCRIBE message received
                                case MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE:

#if BROKER
                                    MqttMsgSubscribe subscribe = (MqttMsgSubscribe)msg;
                                    // raise subscribe topic event (SUBSCRIBE message received)
                                    this.OnMqttMsgSubscribeReceived(subscribe.MessageId, subscribe.Topics, subscribe.QoSLevels);
                                    break;
#else
                                    throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                                // SUBACK message received
                                case MqttMsgBase.MQTT_MSG_SUBACK_TYPE:

                                    // raise subscribed topic event (SUBACK message received)
                                    this.OnMqttMsgSubscribed((MqttMsgSuback)msg);
                                    break;

                                // PUBLISH message received
                                case MqttMsgBase.MQTT_MSG_PUBLISH_TYPE:

                                    // PUBLISH message received in a published internal event, no publish succeeded
                                    if (internalEvent.GetType() == typeof(MsgPublishedInternalEvent))
                                        this.OnMqttMsgPublished(msg.MessageId, false);
                                    else
                                        // raise PUBLISH message received event 
                                        this.OnMqttMsgPublishReceived((MqttMsgPublish)msg);
                                    break;

                                // PUBACK message received
                                case MqttMsgBase.MQTT_MSG_PUBACK_TYPE:

                                    // raise published message event
                                    // (PUBACK received for QoS Level 1)
                                    this.OnMqttMsgPublished(msg.MessageId, true);
                                    break;

                                // PUBREL message received
                                case MqttMsgBase.MQTT_MSG_PUBREL_TYPE:

                                    // raise message received event 
                                    // (PUBREL received for QoS Level 2)
                                    this.OnMqttMsgPublishReceived((MqttMsgPublish)msg);
                                    break;

                                // PUBCOMP message received
                                case MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE:

                                    // raise published message event
                                    // (PUBCOMP received for QoS Level 2)
                                    this.OnMqttMsgPublished(msg.MessageId, true);
                                    break;

                                // UNSUBSCRIBE message received from client
                                case MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE:

#if BROKER
                                    MqttMsgUnsubscribe unsubscribe = (MqttMsgUnsubscribe)msg;
                                    // raise unsubscribe topic event (UNSUBSCRIBE message received)
                                    this.OnMqttMsgUnsubscribeReceived(unsubscribe.MessageId, unsubscribe.Topics);
                                    break;
#else
                                    throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                                // UNSUBACK message received
                                case MqttMsgBase.MQTT_MSG_UNSUBACK_TYPE:

                                    // raise unsubscribed topic event
                                    this.OnMqttMsgUnsubscribed(msg.MessageId);
                                    break;

                                // DISCONNECT message received from client
                                case MqttMsgDisconnect.MQTT_MSG_DISCONNECT_TYPE:

#if BROKER
                                    // raise disconnected client event (DISCONNECT message received)
                                    this.OnMqttMsgDisconnected();
                                    break;
#else
                                    throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif
                            }
                        }
                    }
                }
            }
        }

        private void DispatchEventThread()
        {
            Thread.CurrentThread.Name = ClientId + "DispatchEventThread";
            try
            {
                DispatchEventThreadInternal();
            }
            catch (Exception e)
            {
#if TRACE
                Trace.WriteLine(TraceLevel.Warning, "DispatchEventThread '{2}' canceled by exception: isRunning={0}, port={1}", this.isRunning, this.channel.LocalPort, Thread.CurrentThread.Name);
                Trace.WriteLine(TraceLevel.Warning, e.ToString());
#if !COMPACT_FRAMEWORK
                Trace.WriteLine(TraceLevel.Warning, "Additional call stack frames: {0}", new System.Diagnostics.StackTrace().ToString());
#endif
#endif
            }
        }

        /// <summary>
        /// Process inflight messages queue
        /// </summary>
        private void ProcessInflightThread()
        {
            Thread.CurrentThread.Name = ClientId + "ProcessInflightThread";
            MqttMsgContext msgContext = null;
            MqttMsgBase msgInflight = null;
            MqttMsgBase msgReceived = null;
            InternalEvent internalEvent = null;
            bool isSent = false;
            bool acknowledge = false;
            int timeout = Timeout.Infinite;
            int delta;
            bool msgReceivedProcessed = false;

            try
            {
                while (this.isRunning)
                {
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                    // wait on message queueud to inflight
                    this.inflightWaitHandle.WaitOne(timeout, false);
#else
                    // wait on message queueud to inflight
                    this.inflightWaitHandle.WaitOne(timeout);
#endif

                    // it could be unblocked because Close() method is joining
                    if (this.isRunning)
                    {
#if USEMONITOREDLOCK
                        using (new MonitoredLock(this.inflightQueue, "MqttClient.ProcessInflightThread inflightQueue"))
#else
                        lock (this.inflightQueue)
#endif
                        {
                            // message received and peeked from internal queue is processed
                            // NOTE : it has the corresponding message in inflight queue based on messageId
                            //        (ex. a PUBREC for a PUBLISH, a SUBACK for a SUBSCRIBE, ...)
                            //        if it's orphan we need to remove from internal queue
                            msgReceivedProcessed = false;
                            acknowledge = false;
                            msgReceived = null;

                            // set timeout tu MaxValue instead of Infinte (-1) to perform
                            // compare with calcultad current msgTimeout
                            timeout = Int32.MaxValue;

                            // a message inflight could be re-enqueued but we have to
                            // analyze it only just one time for cycle
                            int count = this.inflightQueue.Count;
                            // process all inflight queued messages
                            while (count > 0)
                            {
                                count--;
                                acknowledge = false;
                                msgReceived = null;

                                // check to be sure that client isn't closing and all queues are now empty !
                                if (!this.isRunning)
                                    break;

                                // dequeue message context from queue
                                msgContext = (MqttMsgContext)this.inflightQueue.Dequeue();

                                // get inflight message
                                msgInflight = (MqttMsgBase)msgContext.Message;

                                switch (msgContext.State)
                                {
                                    case MqttMsgState.QueuedQos0:

                                        // QoS 0, PUBLISH message to send to broker, no state change, no acknowledge
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            this.Send(msgInflight);
                                            isSent = true;
                                        }
                                        // QoS 0, no need acknowledge
                                        else if (msgContext.Flow == MqttMsgFlow.ToAcknowledge)
                                        {
                                            internalEvent = new MsgInternalEvent(msgInflight);
                                            // notify published message from broker (no need acknowledged)
                                            this.OnInternalEvent(internalEvent);
                                        }

#if TRACE
                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                        break;

                                    case MqttMsgState.QueuedQos1:
                                    // [v3.1.1] SUBSCRIBE and UNSIBSCRIBE aren't "officially" QOS = 1
                                    case MqttMsgState.SendSubscribe:
                                    case MqttMsgState.SendUnsubscribe:

                                        // QoS 1, PUBLISH or SUBSCRIBE/UNSUBSCRIBE message to send to broker, state change to wait PUBACK or SUBACK/UNSUBACK
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            msgContext.Timestamp = Environment.TickCount;
                                            msgContext.Attempt++;

                                            if (msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE)
                                            {
                                                // PUBLISH message to send, wait for PUBACK
                                                msgContext.State = MqttMsgState.WaitForPuback;
                                                // retry ? set dup flag [v3.1.1] only for PUBLISH message
                                                if (msgContext.Attempt > 1)
                                                    msgInflight.DupFlag = true;
                                            }
                                            else if (msgInflight.Type == MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE)
                                                // SUBSCRIBE message to send, wait for SUBACK
                                                msgContext.State = MqttMsgState.WaitForSuback;
                                            else if (msgInflight.Type == MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE)
                                                // UNSUBSCRIBE message to send, wait for UNSUBACK
                                                msgContext.State = MqttMsgState.WaitForUnsuback;

                                            this.Send(msgInflight);
                                            isSent = true;

                                            // update timeout : minimum between delay (based on current message sent) or current timeout
                                            timeout = (this.settings.DelayOnRetry < timeout) ? this.settings.DelayOnRetry : timeout;

                                            // re-enqueue message (I have to re-analyze for receiving PUBACK, SUBACK or UNSUBACK)
                                            this.inflightQueue.Enqueue(msgContext);
                                        }
                                        // QoS 1, PUBLISH message received from broker to acknowledge, send PUBACK
                                        else if (msgContext.Flow == MqttMsgFlow.ToAcknowledge)
                                        {
                                            if (!_isManualAcknowledge)
                                            {
                                                AcknowledgeMessage(msgInflight.MessageId);
                                            }

                                            internalEvent = new MsgInternalEvent(msgInflight);
                                            // notify published message from broker and acknowledged
                                            this.OnInternalEvent(internalEvent);

#if TRACE
                                            MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                        }
                                        break;

                                    case MqttMsgState.QueuedQos2:

                                        // QoS 2, PUBLISH message to send to broker, state change to wait PUBREC
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            msgContext.Timestamp = Environment.TickCount;
                                            msgContext.Attempt++;
                                            msgContext.State = MqttMsgState.WaitForPubrec;
                                            // retry ? set dup flag
                                            if (msgContext.Attempt > 1)
                                                msgInflight.DupFlag = true;

                                            this.Send(msgInflight);
                                            isSent = true;

                                            // update timeout : minimum between delay (based on current message sent) or current timeout
                                            timeout = (this.settings.DelayOnRetry < timeout) ? this.settings.DelayOnRetry : timeout;

                                            // re-enqueue message (I have to re-analyze for receiving PUBREC)
                                            this.inflightQueue.Enqueue(msgContext);
                                        }
                                        // QoS 2, PUBLISH message received from broker to acknowledge, send PUBREC, state change to wait PUBREL
                                        else if (msgContext.Flow == MqttMsgFlow.ToAcknowledge)
                                        {
                                            MqttMsgPubrec pubrec = new MqttMsgPubrec();
                                            pubrec.MessageId = msgInflight.MessageId;

                                            msgContext.State = MqttMsgState.WaitForPubrel;

                                            this.Send(pubrec);

                                            // re-enqueue message (I have to re-analyze for receiving PUBREL)
                                            this.inflightQueue.Enqueue(msgContext);
                                        }
                                        break;

                                    case MqttMsgState.WaitForPuback:
                                    case MqttMsgState.WaitForSuback:
                                    case MqttMsgState.WaitForUnsuback:

                                        // QoS 1, waiting for PUBACK of a PUBLISH message sent or
                                        //        waiting for SUBACK of a SUBSCRIBE message sent or
                                        //        waiting for UNSUBACK of a UNSUBSCRIBE message sent or
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            acknowledge = false;
#if USEMONITOREDLOCK
                                            using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread internalQueue"))
#else
                                            lock (this.internalQueue)
#endif
                                            {
                                                if (this.internalQueue.Count > 0)
                                                    msgReceived = (MqttMsgBase)this.internalQueue.Peek();
                                            }

                                            // it is a PUBACK message or a SUBACK/UNSUBACK message
                                            if (msgReceived != null)
                                            {
                                                // PUBACK message or SUBACK/UNSUBACK message for the current message
                                                if (((msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBACK_TYPE) && (msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) && (msgReceived.MessageId == msgInflight.MessageId)) ||
                                                    ((msgReceived.Type == MqttMsgBase.MQTT_MSG_SUBACK_TYPE) && (msgInflight.Type == MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE) && (msgReceived.MessageId == msgInflight.MessageId)) ||
                                                    ((msgReceived.Type == MqttMsgBase.MQTT_MSG_UNSUBACK_TYPE) && (msgInflight.Type == MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE) && (msgReceived.MessageId == msgInflight.MessageId)))
                                                {
#if USEMONITOREDLOCK
                                                    using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread1 internalQueue"))
#else
                                                    lock (this.internalQueue)
#endif
                                                    {
                                                        // received message processed
                                                        this.internalQueue.Dequeue();
                                                        acknowledge = true;
                                                        msgReceivedProcessed = true;
#if TRACE
                                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0}", msgReceived);
#endif
                                                    }

                                                    // if PUBACK received, confirm published with flag
                                                    if (msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBACK_TYPE)
                                                        internalEvent = new MsgPublishedInternalEvent(msgReceived, true);
                                                    else
                                                        internalEvent = new MsgInternalEvent(msgReceived);

                                                    // notify received acknowledge from broker of a published message or subscribe/unsubscribe message
                                                    this.OnInternalEvent(internalEvent);

                                                    // PUBACK received for PUBLISH message with QoS Level 1, remove from session state
                                                    if ((msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                                                        (this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                        (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                        (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                    {
                                                        this.session.InflightMessages.Remove(msgContext.Key);
                                                    }

#if TRACE
                                                    MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                                }
                                            }

                                            // current message not acknowledged, no PUBACK or SUBACK/UNSUBACK or not equal messageid 
                                            if (!acknowledge)
                                            {
                                                delta = Environment.TickCount - msgContext.Timestamp;
                                                // check timeout for receiving PUBACK since PUBLISH was sent or
                                                // for receiving SUBACK since SUBSCRIBE was sent or
                                                // for receiving UNSUBACK since UNSUBSCRIBE was sent
                                                if (delta >= this.settings.DelayOnRetry)
                                                {
                                                    // max retry not reached, resend
                                                    if (msgContext.Attempt < this.settings.AttemptsOnRetry)
                                                    {
                                                        msgContext.State = MqttMsgState.QueuedQos1;

                                                        // re-enqueue message
                                                        this.inflightQueue.Enqueue(msgContext);

                                                        // update timeout (0 -> reanalyze queue immediately)
                                                        timeout = 0;
                                                    }
                                                    else
                                                    {
                                                        // if PUBACK for a PUBLISH message not received after retries, raise event for not published
                                                        if (msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE)
                                                        {
                                                            // PUBACK not received in time, PUBLISH retries failed, need to remove from session inflight messages too
                                                            if ((this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                                (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                                (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                            {
                                                                this.session.InflightMessages.Remove(msgContext.Key);
                                                            }

                                                            internalEvent = new MsgPublishedInternalEvent(msgInflight, false);

                                                            // notify not received acknowledge from broker and message not published
                                                            this.OnInternalEvent(internalEvent);
                                                        }
                                                        // NOTE : not raise events for SUBACK or UNSUBACK not received
                                                        //        for the user no event raised means subscribe/unsubscribe failed
                                                    }
                                                }
                                                else
                                                {
                                                    // re-enqueue message (I have to re-analyze for receiving PUBACK, SUBACK or UNSUBACK)
                                                    this.inflightQueue.Enqueue(msgContext);

                                                    // update timeout
                                                    int msgTimeout = (this.settings.DelayOnRetry - delta);
                                                    timeout = (msgTimeout < timeout) ? msgTimeout : timeout;
                                                }
                                            }
                                        }
                                        break;

                                    case MqttMsgState.WaitForPubrec:

                                        // QoS 2, waiting for PUBREC of a PUBLISH message sent
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            acknowledge = false;
#if USEMONITOREDLOCK
                                            using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread2 internalQueue"))
#else
                                            lock (this.internalQueue)
#endif
                                            {
                                                if (this.internalQueue.Count > 0)
                                                    msgReceived = (MqttMsgBase)this.internalQueue.Peek();
                                            }

                                            // it is a PUBREC message
                                            if ((msgReceived != null) && (msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBREC_TYPE))
                                            {
                                                // PUBREC message for the current PUBLISH message, send PUBREL, wait for PUBCOMP
                                                if (msgReceived.MessageId == msgInflight.MessageId)
                                                {
#if USEMONITOREDLOCK
                                                    using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread3 internalQueue"))
#else
                                                    lock (this.internalQueue)
#endif
                                                    {
                                                        // received message processed
                                                        this.internalQueue.Dequeue();
                                                        acknowledge = true;
                                                        msgReceivedProcessed = true;
#if TRACE
                                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0}", msgReceived);
#endif
                                                    }

                                                    MqttMsgPubrel pubrel = new MqttMsgPubrel();
                                                    pubrel.MessageId = msgInflight.MessageId;

                                                    msgContext.State = MqttMsgState.WaitForPubcomp;
                                                    msgContext.Timestamp = Environment.TickCount;
                                                    msgContext.Attempt = 1;

                                                    this.Send(pubrel);

                                                    // update timeout : minimum between delay (based on current message sent) or current timeout
                                                    timeout = (this.settings.DelayOnRetry < timeout) ? this.settings.DelayOnRetry : timeout;

                                                    // re-enqueue message
                                                    this.inflightQueue.Enqueue(msgContext);
                                                }
                                            }

                                            // current message not acknowledged
                                            if (!acknowledge)
                                            {
                                                delta = Environment.TickCount - msgContext.Timestamp;
                                                // check timeout for receiving PUBREC since PUBLISH was sent
                                                if (delta >= this.settings.DelayOnRetry)
                                                {
                                                    // max retry not reached, resend
                                                    if (msgContext.Attempt < this.settings.AttemptsOnRetry)
                                                    {
                                                        msgContext.State = MqttMsgState.QueuedQos2;

                                                        // re-enqueue message
                                                        this.inflightQueue.Enqueue(msgContext);

                                                        // update timeout (0 -> reanalyze queue immediately)
                                                        timeout = 0;
                                                    }
                                                    else
                                                    {
                                                        // PUBREC not received in time, PUBLISH retries failed, need to remove from session inflight messages too
                                                        if ((this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                            (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                            (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                        {
                                                            this.session.InflightMessages.Remove(msgContext.Key);
                                                        }

                                                        // if PUBREC for a PUBLISH message not received after retries, raise event for not published
                                                        internalEvent = new MsgPublishedInternalEvent(msgInflight, false);
                                                        // notify not received acknowledge from broker and message not published
                                                        this.OnInternalEvent(internalEvent);
                                                    }
                                                }
                                                else
                                                {
                                                    // re-enqueue message
                                                    this.inflightQueue.Enqueue(msgContext);

                                                    // update timeout
                                                    int msgTimeout = (this.settings.DelayOnRetry - delta);
                                                    timeout = (msgTimeout < timeout) ? msgTimeout : timeout;
                                                }
                                            }
                                        }
                                        break;

                                    case MqttMsgState.WaitForPubrel:

                                        // QoS 2, waiting for PUBREL of a PUBREC message sent
                                        if (msgContext.Flow == MqttMsgFlow.ToAcknowledge)
                                        {
#if USEMONITOREDLOCK
                                            using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread4 internalQueue"))
#else
                                            lock (this.internalQueue)
#endif
                                            {
                                                if (this.internalQueue.Count > 0)
                                                    msgReceived = (MqttMsgBase)this.internalQueue.Peek();
                                            }

                                            // it is a PUBREL message
                                            if ((msgReceived != null) && (msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBREL_TYPE))
                                            {
                                                // PUBREL message for the current message, send PUBCOMP
                                                if (msgReceived.MessageId == msgInflight.MessageId)
                                                {
#if USEMONITOREDLOCK
                                                    using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread5 internalQueue"))
#else
                                                    lock (this.internalQueue)
#endif
                                                    {
                                                        // received message processed
                                                        this.internalQueue.Dequeue();
                                                        msgReceivedProcessed = true;
#if TRACE
                                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0}", msgReceived);
#endif
                                                    }

                                                    MqttMsgPubcomp pubcomp = new MqttMsgPubcomp();
                                                    pubcomp.MessageId = msgInflight.MessageId;

                                                    this.Send(pubcomp);

                                                    internalEvent = new MsgInternalEvent(msgInflight);
                                                    // notify published message from broker and acknowledged
                                                    this.OnInternalEvent(internalEvent);

                                                    // PUBREL received (and PUBCOMP sent) for PUBLISH message with QoS Level 2, remove from session state
                                                    if ((msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                                                        (this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                        (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                        (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                    {
                                                        this.session.InflightMessages.Remove(msgContext.Key);
                                                    }

#if TRACE
                                                    MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                                }
                                                else
                                                {
                                                    // re-enqueue message
                                                    this.inflightQueue.Enqueue(msgContext);
                                                }
                                            }
                                            else
                                            {
                                                // re-enqueue message
                                                this.inflightQueue.Enqueue(msgContext);
                                            }
                                        }
                                        break;

                                    case MqttMsgState.WaitForPubcomp:

                                        // QoS 2, waiting for PUBCOMP of a PUBREL message sent
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            acknowledge = false;
#if USEMONITOREDLOCK
                                            using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread6 internalQueue"))
#else
                                            lock (this.internalQueue)
#endif
                                            {
                                                if (this.internalQueue.Count > 0)
                                                    msgReceived = (MqttMsgBase)this.internalQueue.Peek();
                                            }

                                            // it is a PUBCOMP message
                                            if ((msgReceived != null) && (msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE))
                                            {
                                                // PUBCOMP message for the current message
                                                if (msgReceived.MessageId == msgInflight.MessageId)
                                                {
#if USEMONITOREDLOCK
                                                    using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread7 internalQueue"))
#else
                                                    lock (this.internalQueue)
#endif
                                                    {
                                                        // received message processed
                                                        this.internalQueue.Dequeue();
                                                        acknowledge = true;
                                                        msgReceivedProcessed = true;
#if TRACE
                                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0}", msgReceived);
#endif
                                                    }

                                                    internalEvent = new MsgPublishedInternalEvent(msgReceived, true);
                                                    // notify received acknowledge from broker of a published message
                                                    this.OnInternalEvent(internalEvent);

                                                    // PUBCOMP received for PUBLISH message with QoS Level 2, remove from session state
                                                    if ((msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                                                        (this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                        (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                        (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                    {
                                                        this.session.InflightMessages.Remove(msgContext.Key);
                                                    }

#if TRACE
                                                    MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                                }
                                            }
                                            // it is a PUBREC message
                                            else if ((msgReceived != null) && (msgReceived.Type == MqttMsgBase.MQTT_MSG_PUBREC_TYPE))
                                            {
                                                // another PUBREC message for the current message due to a retransmitted PUBLISH
                                                // I'm in waiting for PUBCOMP, so I can discard this PUBREC
                                                if (msgReceived.MessageId == msgInflight.MessageId)
                                                {
#if USEMONITOREDLOCK
                                                    using (new MonitoredLock(this.internalQueue, "MqttClient.ProcessInflightThread8 internalQueue"))
#else
                                                    lock (this.internalQueue)
#endif
                                                    {
                                                        // received message processed
                                                        this.internalQueue.Dequeue();
                                                        acknowledge = true;
                                                        msgReceivedProcessed = true;
#if TRACE
                                                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0}", msgReceived);
#endif

                                                        // re-enqueue message
                                                        this.inflightQueue.Enqueue(msgContext);
                                                    }
                                                }
                                            }

                                            // current message not acknowledged
                                            if (!acknowledge)
                                            {
                                                delta = Environment.TickCount - msgContext.Timestamp; 
                                                // check timeout for receiving PUBCOMP since PUBREL was sent
                                                if (delta >= this.settings.DelayOnRetry)
                                                {
                                                    // max retry not reached, resend
                                                    if (msgContext.Attempt < this.settings.AttemptsOnRetry)
                                                    {
                                                        msgContext.State = MqttMsgState.SendPubrel;

                                                        // re-enqueue message
                                                        this.inflightQueue.Enqueue(msgContext);

                                                        // update timeout (0 -> reanalyze queue immediately)
                                                        timeout = 0;
                                                    }
                                                    else
                                                    {
                                                        // PUBCOMP not received, PUBREL retries failed, need to remove from session inflight messages too
                                                        if ((this.session != null) &&
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                                                            (this.session.InflightMessages.Contains(msgContext.Key)))
#else
                                                            (this.session.InflightMessages.ContainsKey(msgContext.Key)))
#endif
                                                        {
                                                            this.session.InflightMessages.Remove(msgContext.Key);
                                                        }

                                                        // if PUBCOMP for a PUBLISH message not received after retries, raise event for not published
                                                        internalEvent = new MsgPublishedInternalEvent(msgInflight, false);
                                                        // notify not received acknowledge from broker and message not published
                                                        this.OnInternalEvent(internalEvent);
                                                    }
                                                }
                                                else
                                                {
                                                    // re-enqueue message
                                                    this.inflightQueue.Enqueue(msgContext);

                                                    // update timeout
                                                    int msgTimeout = (this.settings.DelayOnRetry - delta);
                                                    timeout = (msgTimeout < timeout) ? msgTimeout : timeout;
                                                }
                                            }
                                        }
                                        break;

                                    case MqttMsgState.SendPubrec:

                                        // TODO : impossible ? --> QueuedQos2 ToAcknowledge
                                        break;

                                    case MqttMsgState.SendPubrel:

                                        // QoS 2, PUBREL message to send to broker, state change to wait PUBCOMP
                                        if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                        {
                                            MqttMsgPubrel pubrel = new MqttMsgPubrel();
                                            pubrel.MessageId = msgInflight.MessageId;

                                            msgContext.State = MqttMsgState.WaitForPubcomp;
                                            msgContext.Timestamp = Environment.TickCount;
                                            msgContext.Attempt++;
                                            // retry ? set dup flag [v3.1.1] no needed
                                            if (this.ProtocolVersion == MqttProtocolVersion.Version_3_1)
                                            {
                                                if (msgContext.Attempt > 1)
                                                    pubrel.DupFlag = true;
                                            }
                                            
                                            this.Send(pubrel);

                                            // update timeout : minimum between delay (based on current message sent) or current timeout
                                            timeout = (this.settings.DelayOnRetry < timeout) ? this.settings.DelayOnRetry : timeout;

                                            // re-enqueue message
                                            this.inflightQueue.Enqueue(msgContext);
                                        }
                                        break;

                                    case MqttMsgState.SendPubcomp:
                                        // TODO : impossible ?
                                        break;
                                    case MqttMsgState.SendPuback:
                                        // TODO : impossible ? --> QueuedQos1 ToAcknowledge
                                        break;
                                    default:
                                        break;
                                }
                            }

                            // if calculated timeout is MaxValue, it means that must be Infinite (-1)
                            if (timeout == Int32.MaxValue)
                                timeout = Timeout.Infinite;

                            // if message received is orphan, no corresponding message in inflight queue
                            // based on messageId, we need to remove from the queue
                            if ((msgReceived != null) && !msgReceivedProcessed)
                            {
                                this.internalQueue.Dequeue();
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "dequeued {0} orphan", msgReceived);
#endif
                            }
                        }
                    }

                    if (isSent)
                    {
                        OnMqttMessageSentToSocket(msgInflight.MessageId, true);
                    }
                }
            }
            catch (Exception e)
            {
                OnMqttLibraryExceptionOccured(e);
            }
        }

        public void AcknowledgeMessage(ushort messageId)
        {
            try
            {
                var mqttMsgPuback = new MqttMsgPuback { MessageId = messageId };

                this.Send(mqttMsgPuback);
            }
            catch (Exception e)
            {
                OnMqttLibraryExceptionOccured(e);
            }
        }

        /// <summary>
        /// Restore session
        /// </summary>
        private void RestoreSession()
        {
            // if not clean session
            if (!this.CleanSession)
            {
                // there is a previous session
                if (this.session != null)
                {
#if USEMONITOREDLOCK
                    using (new MonitoredLock(this.inflightQueue, "MqttClient.RestoreSession inflightQueue"))
#else
                    lock (this.inflightQueue)
#endif
                    {
                        foreach (MqttMsgContext msgContext in this.session.InflightMessages.Values)
                        {
                            this.inflightQueue.Enqueue(msgContext);

                            // if it is a PUBLISH message to publish
                            if ((msgContext.Message.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                                (msgContext.Flow == MqttMsgFlow.ToPublish))
                            {
                                // it's QoS 1 and we haven't received PUBACK
                                if ((msgContext.Message.QosLevel == MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE) &&
                                    (msgContext.State == MqttMsgState.WaitForPuback))
                                {
                                    // we haven't received PUBACK, we need to resend PUBLISH message
                                    msgContext.State = MqttMsgState.QueuedQos1;
                                }
                                // it's QoS 2
                                else if (msgContext.Message.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE)
                                {
                                    // we haven't received PUBREC, we need to resend PUBLISH message
                                    if (msgContext.State == MqttMsgState.WaitForPubrec)
                                    {
                                        msgContext.State = MqttMsgState.QueuedQos2;
                                    }
                                    // we haven't received PUBCOMP, we need to resend PUBREL for it
                                    else if (msgContext.State == MqttMsgState.WaitForPubcomp)
                                    {
                                        msgContext.State = MqttMsgState.SendPubrel;
                                    }
                                }
                            }
                        }
                    }

                    // unlock process inflight queue
                    this.inflightWaitHandle.Set();
                }
                else
                {
                    // create new session
                    this.session = new MqttClientSession(this.ClientId);
                }
            }
            // clean any previous session
            else
            {
                if (this.session != null)
                    this.session.Clear();
            }
        }

#if BROKER

        /// <summary>
        /// Load a given session
        /// </summary>
        /// <param name="session">MQTT Client session to load</param>
        public void LoadSession(MqttClientSession session)
        {
            // if not clean session
            if (!this.CleanSession)
            {
                // set the session ...
                this.session = session;
                // ... and restore it
                this.RestoreSession();
            }
        }
#endif

        /// <summary>
        /// Generate the next message identifier
        /// </summary>
        /// <returns>Message identifier</returns>
        private ushort GetMessageId()
        {
#if USEMONITOREDLOCK
            using (new MonitoredLock(this._messageIdCounterLock, "MqttClient.GetMessageId _messageIdCounterLock"))
#else
            lock (this._messageIdCounterLock)
#endif
            {
                // if 0 or max UInt16, it becomes 1 (first valid messageId)
                this.messageIdCounter = ((this.messageIdCounter % UInt16.MaxValue) != 0) ? (ushort)(this.messageIdCounter + 1) : (ushort)1;
                return this.messageIdCounter;
            }
        }

        /// <summary>
        /// Finder class for PUBLISH message inside a queue
        /// </summary>
        internal class MqttMsgContextFinder
        {
            // PUBLISH message id
            internal ushort MessageId { get; set; }
            // message flow into inflight queue
            internal MqttMsgFlow Flow { get; set; }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="messageId">Message Id</param>
            /// <param name="flow">Message flow inside inflight queue</param>
            internal MqttMsgContextFinder(ushort messageId, MqttMsgFlow flow)
            {
                this.MessageId = messageId;
                this.Flow = flow;
            }

            internal bool Find(object item)
            {
                MqttMsgContext msgCtx = (MqttMsgContext)item;
                return ((msgCtx.Message.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                        (msgCtx.Message.MessageId == this.MessageId) &&
                        msgCtx.Flow == this.Flow);

            }
        }
    }

    /// <summary>
    /// MQTT protocol version
    /// </summary>
    public enum MqttProtocolVersion
    {
        Version_3_1 = MqttMsgConnect.PROTOCOL_VERSION_V3_1,
        Version_3_1_1 = MqttMsgConnect.PROTOCOL_VERSION_V3_1_1
    }
}
