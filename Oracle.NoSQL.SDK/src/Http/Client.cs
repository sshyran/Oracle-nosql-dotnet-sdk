/*-
 * Copyright (c) 2020, 2022 Oracle and/or its affiliates. All rights reserved.
 *
 * Licensed under the Universal Permissive License v 1.0 as shown at
 *  https://oss.oracle.com/licenses/upl/
 */

namespace Oracle.NoSQL.SDK.Http
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
    using static HttpConstants;
    using static HttpRequestUtils;
    using static X509Utils;

    internal sealed class Client : IDisposable
    {
        private readonly Uri dataPathUri = new Uri(NoSQLDataPath,
            UriKind.Relative);

        private readonly NoSQLConfig config;
        private readonly HttpClient client;
        private int requestId;

        internal static HttpMessageHandler CreateHandler(
            ConnectionOptions connectionOptions)
        {
            var handler = new HttpClientHandler();
            if (connectionOptions?.TrustedRootCertificates != null)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (request, certificate, chain, errors) =>
                        ValidateCertificate(certificate, chain, errors,
                            connectionOptions.TrustedRootCertificates);
            }

            return handler;
        }

        internal bool IsRetryableNetworkException(Exception ex)
        {
            return ex is HttpRequestException httpEx &&
                   IsHttpRequestExceptionRetryable(httpEx);
        }

        internal Client(NoSQLConfig config)
        {
            this.config = config;
            client = new HttpClient(CreateHandler(config.ConnectionOptions),
                true)
            {
                BaseAddress = config.Uri
            };

            client.DefaultRequestHeaders.Host = config.Uri.Host;
            client.DefaultRequestHeaders.Connection.Add("keep-alive");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    config.serializer.ContentType));
            // Disable default timeout since we use our own timeout mechanism
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        internal async Task<object> ExecuteRequestAsync(Request request,
            CancellationToken cancellationToken)
        {
            var message = new HttpRequestMessage(HttpMethod.Post,
                dataPathUri);

            var stream = new MemoryStream();
            request.Serialize(config.serializer, stream);

            message.Content = new ByteArrayContent(stream.GetBuffer(), 0,
                (int)stream.Position);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(
                config.serializer.ContentType);
            message.Content.Headers.ContentLength = stream.Position;

            message.Headers.Add(RequestId, Convert.ToString(
                Interlocked.Increment(ref requestId)));

            // Add authorization headers
            if (config.AuthorizationProvider != null)
            {
                await config.AuthorizationProvider.ApplyAuthorizationAsync(
                    request, message.Headers, cancellationToken);
            }

            var response = await SendWithTimeoutAsync(client, message,
                request.RequestTimeoutMillis, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw await CreateServiceResponseExceptionAsync(response);
            }

            // The stream returned by ReadAsStreamAsync(), even though it is
            // usually a MemoryStream, doesn't not allow access to the buffer
            // via MemoryStream.GetBuffer() which is needed for
            // deserialization, so we have to use ReadAsByteArrayAsync().
            var buffer = await response.Content.ReadAsByteArrayAsync();
            stream = new MemoryStream(buffer, 0, buffer.Length, false, true);
            config.serializer.ReadAndCheckError(stream);
            return request.Deserialize(config.serializer,
                stream);
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
