using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace M2Mqtt.NetStandard.Utility
{
    // based on: https://gist.github.com/nathanpjones/64c139ba4a909d30940a
    [SuppressMessage("ReSharper", "UseStringInterpolation")]
    internal class MonitoredLock : IDisposable
    {
        public delegate void MonitorLogMessage(int logLevel, string message, int ignoreId);

        private readonly object _lockObject;

        private readonly string _info;

        private readonly int _ignoreLogId;

        private bool _disposed;


        public MonitoredLock(object lockObject, string info) : this(lockObject, info, 0)
        {

        }

        public MonitoredLock(object lockObject, string info, int ignoreLogId)
        {
            _lockObject = lockObject;
            _info = info;
            _ignoreLogId = ignoreLogId;
            LogMessage("WAIT", false, false);
            Monitor.Enter(_lockObject);
            LogMessage("ENTER", false, false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            LogMessage("EXIT", true, disposing);
            try
            {
                Monitor.Exit(_lockObject);
            }
            catch (Exception e) // in newer this is probably a SynchronizationLockException
            {
                Console.WriteLine("MonitorLock: {0} | {1}", _info, e);
                Console.WriteLine(e.StackTrace);
            }

            if (disposing)
            {
            }
        }

        /// <inheritdoc />
        ~MonitoredLock()
        {
            Dispose(false);
        }

        private void LogMessage(string message, bool isExit, bool isDisposing)
        {
            //Console.WriteLine("LogMessage: {0} isExit: {1} isDisposing: {2} EventLogMessage!=null: {3} ", message, isExit, isDisposing, EventLogMessage!=null);
            if (EventLogMessage != null)
            {
                var threadId = Thread.CurrentThread.ManagedThreadId;
                var threadName = Thread.CurrentThread.Name;

                var enrichedMessage = string.Format("MonitoredLock:{0,5};{1, 10};{2, 10};{3};{4}", message, _lockObject.GetHashCode(), threadId, threadName, _info);
                var logLevel = 3; //int.Information;
                if (isExit && !isDisposing)
                {
                    logLevel = 2; //int.Warning;
                }

                EventLogMessage.Invoke(logLevel, enrichedMessage, _ignoreLogId);
            }
        }

        public static event MonitorLogMessage EventLogMessage;

        public static void LogEventWaitHandle(string action, int resetId, string message)
        {
            if (EventLogMessage != null)
            {
                var threadId = Thread.CurrentThread.ManagedThreadId;
                var threadName = Thread.CurrentThread.Name;

                var enrichedMessage = string.Format("EventWaitHandle:{0,5};{1, 10};{2, 10};{3};{4}", action, resetId, threadId, threadName, message);
                var logLevel = 3; //int.Information;

                EventLogMessage.Invoke(logLevel, enrichedMessage, -1);
            }
        }
    }
}