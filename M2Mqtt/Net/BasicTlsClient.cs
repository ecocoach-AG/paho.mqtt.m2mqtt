#if COMPACT_FRAMEWORK
using Org.BouncyCastle.Crypto.Tls;

namespace uPLibrary.Networking.M2Mqtt.Net
{
    internal class BasicTlsClient : DefaultTlsClient
    {
        public override TlsAuthentication GetAuthentication()
        {
            return new NoTlsAuthentication();
        }
    }
}
#endif