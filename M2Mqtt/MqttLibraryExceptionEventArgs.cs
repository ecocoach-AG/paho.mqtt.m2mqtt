using System;

namespace uPLibrary.Networking.M2Mqtt
{
    public class MqttLibraryExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
    }
}