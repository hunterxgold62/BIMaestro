using System;
using System.Net;
using System.Net.Http;

namespace Licensing
{
    /// <summary>HttpClient “proxy-friendly” à utiliser partout.</summary>
    public static class NetSupport
    {
        public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                UseDefaultCredentials = true,
                PreAuthenticate = true
            };

            // certains proxys ignorent UseDefaultCredentials
            if (handler.Proxy != null && handler.Proxy.Credentials == null)
                handler.Proxy.Credentials = CredentialCache.DefaultCredentials;

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.ExpectContinue = false;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BIMaestro/1.0 (+Revit)");
            return client;
        }
    }
}
