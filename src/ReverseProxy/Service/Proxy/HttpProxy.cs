// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Microsoft.ReverseProxy.Telemetry;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.Service.Proxy
{
    /// <summary>
    /// Default implementation of <see cref="IHttpProxy"/>.
    /// </summary>
    internal class HttpProxy : IHttpProxy
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);
        private static readonly Version DefaultVersion = HttpVersion.Version20;
#if NET
        private static readonly HttpVersionPolicy DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
#endif

        private static readonly HashSet<string> _responseHeadersToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HeaderNames.TransferEncoding
        };

        private readonly ILogger _logger;
        private readonly IClock _clock;

        public HttpProxy(ILogger<HttpProxy> logger, IClock clock)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(_clock));
        }

        /// <summary>
        /// Proxies the incoming request to the destination server, and the response back to the client.
        /// </summary>
        /// <remarks>
        /// In what follows, as well as throughout in Reverse Proxy, we consider
        /// the following picture as illustrative of the Proxy.
        /// <code>
        ///      +-------------------+
        ///      |  Destination      +
        ///      +-------------------+
        ///            ▲       |
        ///        (b) |       | (c)
        ///            |       ▼
        ///      +-------------------+
        ///      |      Proxy        +
        ///      +-------------------+
        ///            ▲       |
        ///        (a) |       | (d)
        ///            |       ▼
        ///      +-------------------+
        ///      | Client            +
        ///      +-------------------+
        /// </code>
        ///
        /// (a) and (b) show the *request* path, going from the client to the target.
        /// (c) and (d) show the *response* path, going from the destination back to the client.
        ///
        /// Normal proxying comprises the following steps:
        ///    (0) Disable ASP .NET Core limits for streaming requests
        ///    (1) Create outgoing HttpRequestMessage
        ///    (2) Setup copy of request body (background)             Client --► Proxy --► Destination
        ///    (3) Copy request headers                                Client --► Proxy --► Destination
        ///    (4) Send the outgoing request using HttpMessageInvoker  Client --► Proxy --► Destination
        ///    (5) Copy response status line                           Client ◄-- Proxy ◄-- Destination
        ///    (6) Copy response headers                               Client ◄-- Proxy ◄-- Destination
        ///    (7-A) Check for a 101 upgrade response, this takes care of WebSockets as well as any other upgradeable protocol.
        ///        (7-A-1)  Upgrade client channel                     Client ◄--- Proxy ◄--- Destination
        ///        (7-A-2)  Copy duplex streams and return             Client ◄--► Proxy ◄--► Destination
        ///    (7-B) Copy (normal) response body                       Client ◄-- Proxy ◄-- Destination
        ///    (8) Copy response trailer headers and finish response   Client ◄-- Proxy ◄-- Destination
        ///    (9) Wait for completion of step 2: copying request body Client --► Proxy --► Destination
        ///
        /// ASP .NET Core (Kestrel) will finally send response trailers (if any)
        /// after we complete the steps above and relinquish control.
        /// </remarks>
        public async Task ProxyAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            HttpTransforms transforms,
            RequestProxyOptions requestOptions)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = destinationPrefix ?? throw new ArgumentNullException(nameof(destinationPrefix));
            _ = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            transforms ??= HttpTransforms.Empty;

            // HttpClient overload for SendAsync changes response behavior to fully buffered which impacts performance
            // See discussion in https://github.com/microsoft/reverse-proxy/issues/458
            if (httpClient is HttpClient)
            {
                throw new ArgumentException($"The http client must be of type HttpMessageInvoker, not HttpClient", nameof(httpClient));
            }

            ProxyTelemetry.Log.ProxyStart(destinationPrefix);
            try
            {
                var requestAborted = context.RequestAborted;

                var isClientHttp2 = ProtocolHelper.IsHttp2(context.Request.Protocol);

                // NOTE: We heuristically assume gRPC-looking requests may require streaming semantics.
                // See https://github.com/microsoft/reverse-proxy/issues/118 for design discussion.
                var isStreamingRequest = isClientHttp2 && ProtocolHelper.IsGrpcContentType(context.Request.ContentType);

                // :: Step 1: Create outgoing HttpRequestMessage
                var (destinationRequest, requestContent) = await CreateRequestMessageAsync(
                    context, destinationPrefix, transforms, requestOptions, isStreamingRequest, requestAborted);

                // :: Step 4: Send the outgoing request using HttpClient
                HttpResponseMessage destinationResponse;
                var requestTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                requestTimeoutSource.CancelAfter(requestOptions.Timeout ?? DefaultTimeout);
                var requestTimeoutToken = requestTimeoutSource.Token;
                try
                {
                    ProxyTelemetry.Log.ProxyStage(ProxyStage.SendAsyncStart);
                    destinationResponse = await httpClient.SendAsync(destinationRequest, requestTimeoutToken);
                    ProxyTelemetry.Log.ProxyStage(ProxyStage.SendAsyncStop);
                }
                catch (OperationCanceledException canceledException)
                {
                    if (!requestAborted.IsCancellationRequested && requestTimeoutToken.IsCancellationRequested)
                    {
                        ReportProxyError(context, ProxyError.RequestTimedOut, canceledException);
                        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                        return;
                    }

                    ReportProxyError(context, ProxyError.RequestCanceled, canceledException);
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    return;
                }
                catch (Exception requestException)
                {
                    await HandleRequestFailureAsync(context, requestContent, requestException);
                    return;
                }
                finally
                {
                    requestTimeoutSource.Dispose();
                }

                // Detect connection downgrade, which may be problematic for e.g. gRPC.
                if (isClientHttp2 && destinationResponse.Version.Major != 2)
                {
                    // TODO: Do something on connection downgrade...
                    Log.HttpDowngradeDetected(_logger);
                }

                // Assert that, if we are proxying content to the destination, it must have started by now
                // (since HttpClient.SendAsync has already completed asynchronously).
                // If this check fails, there is a coding defect which would otherwise
                // cause us to wait forever in step 9, so fail fast here.
                if (requestContent != null && !requestContent.Started)
                {
                    // TODO: HttpClient might not need to read the body in some scenarios, such as an early auth failure with Expect: 100-continue.
                    throw new InvalidOperationException("Proxying the Client request body to the Destination server hasn't started. This is a coding defect.");
                }

                // :: Step 5: Copy response status line Client ◄-- Proxy ◄-- Destination
                // :: Step 6: Copy response headers Client ◄-- Proxy ◄-- Destination
                await CopyResponseStatusAndHeadersAsync(destinationResponse, context, transforms);

                // :: Step 7-A: Check for a 101 upgrade response, this takes care of WebSockets as well as any other upgradeable protocol.
                if (destinationResponse.StatusCode == HttpStatusCode.SwitchingProtocols)
                {
                    await HandleUpgradedResponse(context, destinationResponse, requestAborted);
                    return;
                }

                // NOTE: it may *seem* wise to call `context.Response.StartAsync()` at this point
                // since it looks like we are ready to send back response headers
                // (and this might help reduce extra delays while we wait to receive the body from the destination).
                // HOWEVER, this would produce the wrong result if it turns out that there is no content
                // from the destination -- instead of sending headers and terminating the stream at once,
                // we would send headers thinking a body may be coming, and there is none.
                // This is problematic on gRPC connections when the destination server encounters an error,
                // in which case it immediately returns the response headers and trailing headers, but no content,
                // and clients misbehave if the initial headers response does not indicate stream end.

                // :: Step 7-B: Copy response body Client ◄-- Proxy ◄-- Destination
                var (responseBodyCopyResult, responseBodyException) = await CopyResponseBodyAsync(destinationResponse.Content, context.Response.Body, requestAborted);

                if (responseBodyCopyResult != StreamCopyResult.Success)
                {
                    await HandleResponseBodyErrorAsync(context, requestContent, responseBodyCopyResult, responseBodyException);
                    return;
                }

                // :: Step 8: Copy response trailer headers and finish response Client ◄-- Proxy ◄-- Destination
                await CopyResponseTrailingHeadersAsync(destinationResponse, context, transforms);

                if (isStreamingRequest)
                {
                    // NOTE: We must call `CompleteAsync` so that Kestrel will flush all bytes to the client.
                    // In the case where there was no response body,
                    // this is also when headers and trailing headers are sent to the client.
                    // Without this, the client might wait forever waiting for response bytes,
                    // while we might wait forever waiting for request bytes,
                    // leading to a stuck connection and no way to make progress.
                    await context.Response.CompleteAsync();
                }

                // :: Step 9: Wait for completion of step 2: copying request body Client --► Proxy --► Destination
                if (requestContent != null)
                {
                    var (requestBodyCopyResult, requestBodyException) = await requestContent.ConsumptionTask;

                    if (requestBodyCopyResult != StreamCopyResult.Success)
                    {
                        // The response succeeded. If there was a request body error then it was probably because the client or destination decided
                        // to cancel it. Report as low severity.

                        var error = requestBodyCopyResult switch
                        {
                            StreamCopyResult.InputError => ProxyError.RequestBodyClient,
                            StreamCopyResult.OutputError => ProxyError.RequestBodyDestination,
                            StreamCopyResult.Canceled => ProxyError.RequestBodyCanceled,
                            _ => throw new NotImplementedException(requestBodyCopyResult.ToString())
                        };
                        ReportProxyError(context, error, requestBodyException);
                    }
                }
            }
            finally
            {
                ProxyTelemetry.Log.ProxyStop(context.Response.StatusCode);
            }
        }

        private async Task<(HttpRequestMessage, StreamCopyHttpContent)> CreateRequestMessageAsync(HttpContext context, string destinationAddress,
            HttpTransforms transforms, RequestProxyOptions requestOptions, bool isStreamingRequest, CancellationToken requestAborted)
        {
            // "http://a".Length = 8
            if (destinationAddress == null || destinationAddress.Length < 8)
            {
                throw new ArgumentException(nameof(destinationAddress));
            }

            var destinationRequest = new HttpRequestMessage();

            destinationRequest.Method = HttpUtilities.GetHttpMethod(context.Request.Method);

            var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();
            var upgradeHeader = context.Request.Headers[HeaderNames.Upgrade].ToString();
            var isUpgradeRequest = (upgradeFeature?.IsUpgradableRequest ?? false)
                // Mitigate https://github.com/microsoft/reverse-proxy/issues/255, IIS considers all requests upgradeable.
                && (string.Equals("WebSocket", upgradeHeader, StringComparison.OrdinalIgnoreCase)
                    // https://github.com/microsoft/reverse-proxy/issues/467 for kubernetes APIs
                    || upgradeHeader.StartsWith("SPDY/", StringComparison.OrdinalIgnoreCase));

            // Default to HTTP/1.1 for proxying upgradeable requests. This is already the default as of .NET Core 3.1
            // Otherwise request what's set in proxyOptions (e.g. default HTTP/2) and let HttpClient negotiate the protocol
            // based on VersionPolicy (for .NET 5 and higher). For example, downgrading to HTTP/1.1 if it cannot establish HTTP/2 with the target.
            // This is done without extra round-trips thanks to ALPN. We can detect a downgrade after calling HttpClient.SendAsync
            // (see Step 3 below). TBD how this will change when HTTP/3 is supported.
            destinationRequest.Version = isUpgradeRequest ? ProtocolHelper.Http11Version : (requestOptions.Version ?? DefaultVersion);
#if NET
            destinationRequest.VersionPolicy = isUpgradeRequest ? HttpVersionPolicy.RequestVersionOrLower : (requestOptions.VersionPolicy ?? DefaultVersionPolicy);
#endif

            // :: Step 2: Setup copy of request body (background) Client --► Proxy --► Destination
            // Note that we must do this before step (3) because step (3) may also add headers to the HttpContent that we set up here.
            var requestContent = SetupRequestBodyCopy(context.Request, isStreamingRequest, requestAborted);
            destinationRequest.Content = requestContent;

            if (transforms.CopyRequestHeaders)
            {
                // :: Step 3: Copy request headers Client --► Proxy --► Destination
                CopyRequestHeaders(context, destinationRequest);
            }

            // TODO Perf: We could probably avoid splitting this and just append the final path and query
            UriHelper.FromAbsolute(destinationAddress, out var destinationScheme, out var destinationHost, out var destinationPathBase, out _, out _); // Query and Fragment are not supported here.

            var request = context.Request;
            if (transforms.OnRequest == null)
            {
                var url = UriHelper.BuildAbsolute(destinationScheme, destinationHost, destinationPathBase, request.Path, request.QueryString);
                Log.Proxying(_logger, url);
                destinationRequest.RequestUri = new Uri(url, UriKind.Absolute);
                return (destinationRequest, requestContent);
            }

            await transforms.OnRequest(context, destinationRequest, destinationAddress);

            // TODO: What if they replace the HttpContent object?

            // Allow someone to custom build the request uri, otherwise provide it for them.
            destinationRequest.RequestUri ??= new Uri(
                UriHelper.BuildAbsolute(destinationScheme, destinationHost, destinationPathBase, request.Path, request.QueryString),
                UriKind.Absolute);
            
            Log.Proxying(_logger, destinationRequest.RequestUri.AbsoluteUri);
            return (destinationRequest, requestContent);
        }

        private StreamCopyHttpContent SetupRequestBodyCopy(HttpRequest request, bool isStreamingRequest, CancellationToken cancellation)
        {
            // If we generate an HttpContent without a Content-Length then for HTTP/1.1 HttpClient will add a Transfer-Encoding: chunked header
            // even if it's a GET request. Some servers reject requests containing a Transfer-Encoding header if they're not expecting a body.
            // Try to be as specific as possible about the client's intent to send a body. The one thing we don't want to do is to start
            // reading the body early because that has side-effects like 100-continue.
            var hasBody = true;
            var contentLength = request.Headers.ContentLength;
            var method = request.Method;

#if NET
            var canHaveBodyFeature = request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>();
            if (canHaveBodyFeature != null)
            {
                // 5.0 servers provide a definitive answer for us.
                hasBody = canHaveBodyFeature.CanHaveBody;
            }
            else
#endif
            // https://tools.ietf.org/html/rfc7230#section-3.3.3
            // All HTTP/1.1 requests should have Transfer-Encoding or Content-Length.
            // Http.Sys/IIS will even add a Transfer-Encoding header to HTTP/2 requests with bodies for back-compat.
            // HTTP/1.0 Connection: close bodies are only allowed on responses, not requests.
            // https://tools.ietf.org/html/rfc1945#section-7.2.2
            //
            // Transfer-Encoding overrides Content-Length per spec
            if (request.Headers.TryGetValue(HeaderNames.TransferEncoding, out var transferEncoding)
                && transferEncoding.Count == 1
                && string.Equals("chunked", transferEncoding.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                hasBody = true;
            }
            else if (contentLength.HasValue)
            {
                hasBody = contentLength > 0;
            }
            // Kestrel HTTP/2: There are no required headers that indicate if there is a request body so we need to sniff other fields.
            else if (!ProtocolHelper.IsHttp2OrGreater(request.Protocol))
            {
                hasBody = false;
            }
            // https://tools.ietf.org/html/rfc7231#section-4.3.1
            // A payload within a GET/HEAD/DELETE/CONNECT request message has no defined semantics; sending a payload body on a
            // GET/HEAD/DELETE/CONNECT request might cause some existing implementations to reject the request.
            // https://tools.ietf.org/html/rfc7231#section-4.3.8
            // A client MUST NOT send a message body in a TRACE request.
            else if (HttpMethods.IsGet(method)
                || HttpMethods.IsHead(method)
                || HttpMethods.IsDelete(method)
                || HttpMethods.IsConnect(method)
                || HttpMethods.IsTrace(method))
            {
                hasBody = false;
            }
            // else hasBody defaults to true

            StreamCopyHttpContent requestContent = null;
            if (hasBody)
            {
                if (isStreamingRequest)
                {
                    DisableMinRequestBodyDataRateAndMaxRequestBodySize(request.HttpContext);
                }

                // Note on `autoFlushHttpClientOutgoingStream: isStreamingRequest`:
                // The.NET Core HttpClient stack keeps its own buffers on top of the underlying outgoing connection socket.
                // We flush those buffers down to the socket on every write when this is set,
                // but it does NOT result in calls to flush on the underlying socket.
                // This is necessary because we proxy http2 transparently,
                // and we are deliberately unaware of packet structure used e.g. in gRPC duplex channels.
                // Because the sockets aren't flushed, the perf impact of this choice is expected to be small.
                // Future: It may be wise to set this to true for *all* http2 incoming requests,
                // but for now, out of an abundance of caution, we only do it for requests that look like gRPC.
                requestContent = new StreamCopyHttpContent(
                    source: request.Body,
                    autoFlushHttpClientOutgoingStream: isStreamingRequest,
                    clock: _clock,
                    cancellation: cancellation);
            }

            return requestContent;
        }

        private static void CopyRequestHeaders(HttpContext context, HttpRequestMessage destination)
        {
            foreach (var header in context.Request.Headers)
            {
                var headerName = header.Key;
                var headerValue = header.Value;
                if (StringValues.IsNullOrEmpty(headerValue))
                {
                    continue;
                }

                // Filter out HTTP/2 pseudo headers like ":method" and ":path", those go into other fields.
                if (headerName.Length > 0 && headerName[0] == ':')
                {
                    continue;
                }

                AddHeader(destination, headerName, headerValue);
            }

            // Note: HttpClient.SendAsync will end up sending the union of
            // HttpRequestMessage.Headers and HttpRequestMessage.Content.Headers.
            // We don't really care where the proxied headers appear among those 2,
            // as long as they appear in one (and only one, otherwise they would be duplicated).
            static void AddHeader(HttpRequestMessage request, string headerName, StringValues value)
            {
                // HttpClient wrongly uses comma (",") instead of semi-colon (";") as a separator for Cookie headers.
                // To mitigate this, we concatenate them manually and put them back as a single header value.
                // A multi-header cookie header is invalid, but we get one because of
                // https://github.com/dotnet/aspnetcore/issues/26461
                if (string.Equals(headerName, HeaderNames.Cookie, StringComparison.OrdinalIgnoreCase) && value.Count > 1)
                {
                    value = string.Join("; ", value);
                }

                if (value.Count == 1)
                {
                    string headerValue = value;
                    if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
                    {
                        request.Content?.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }
                }
                else
                {
                    string[] headerValues = value;
                    if (!request.Headers.TryAddWithoutValidation(headerName, headerValues))
                    {
                        request.Content?.Headers.TryAddWithoutValidation(headerName, headerValues);
                    }
                }
            }
        }

        private void HandleRequestBodyFailure(HttpContext context, StreamCopyResult requestBodyCopyResult, Exception requestBodyException, Exception additionalException)
        {
            ProxyError requestBodyError;
            int statusCode;
            switch (requestBodyCopyResult)
            {
                // Failed while trying to copy the request body from the client. It's ambiguous if the request or response failed first.
                case StreamCopyResult.InputError:
                    requestBodyError = ProxyError.RequestBodyClient;
                    statusCode = StatusCodes.Status400BadRequest;
                    break;
                // Failed while trying to copy the request body to the destination. It's ambiguous if the request or response failed first.
                case StreamCopyResult.OutputError:
                    requestBodyError = ProxyError.RequestBodyDestination;
                    statusCode = StatusCodes.Status502BadGateway;
                    break;
                // Canceled while trying to copy the request body, either due to a client disconnect or a timeout. This probably caused the response to fail as a secondary error.
                case StreamCopyResult.Canceled:
                    requestBodyError = ProxyError.RequestBodyCanceled;
                    // Timeouts (504s) are handled at the SendAsync call site.
                    // The request body should only be canceled by the RequestAborted token.
                    statusCode = StatusCodes.Status502BadGateway;
                    break;
                default:
                    throw new NotImplementedException(requestBodyCopyResult.ToString());
            }

            ReportProxyError(context, requestBodyError, new AggregateException(requestBodyException, additionalException));

            // We don't know if the client is still around to see this error, but set it for diagnostics to see.
            if (!context.Response.HasStarted)
            {
                // Nothing has been sent to the client yet, we can still send a good error response.
                context.Response.Clear();
                context.Response.StatusCode = statusCode;
                return;
            }

            ResetOrAbort(context, isCancelled: requestBodyCopyResult == StreamCopyResult.Canceled);
        }

        private async Task HandleRequestFailureAsync(HttpContext context, StreamCopyHttpContent requestContent, Exception requestException)
        {
            // Check for request body errors, these may have triggered the response error.
            if (requestContent?.ConsumptionTask.IsCompleted == true)
            {
                var (requestBodyCopyResult, requestBodyException) = await requestContent.ConsumptionTask;

                if (requestBodyCopyResult != StreamCopyResult.Success)
                {
                    HandleRequestBodyFailure(context, requestBodyCopyResult, requestBodyException, requestException);
                    return;
                }
            }

            // We couldn't communicate with the destination.
            ReportProxyError(context, ProxyError.Request, requestException);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }

        private static Task CopyResponseStatusAndHeadersAsync(HttpResponseMessage source, HttpContext context, HttpTransforms transforms)
        {
            context.Response.StatusCode = (int)source.StatusCode;
            context.Features.Get<IHttpResponseFeature>().ReasonPhrase = source.ReasonPhrase;

            var responseHeaders = context.Response.Headers;
            if (transforms.CopyResponseHeaders)
            {
                CopyHeaders(source.Headers, responseHeaders);
                if (source.Content != null)
                {
                    CopyHeaders(source.Content.Headers, responseHeaders);
                }
            }
            if (transforms.OnResponse != null)
            {
                return transforms.OnResponse(context, source);
            }

            return Task.CompletedTask;
        }

        private async Task HandleUpgradedResponse(HttpContext context, HttpResponseMessage destinationResponse,
            CancellationToken longCancellation)
        {
            ProxyTelemetry.Log.ProxyStage(ProxyStage.ResponseUpgrade);

            // SocketHttpHandler and similar transports always provide an HttpContent object, even if it's empty.
            // Note as of 5.0 HttpResponse.Content never returns null.
            // https://github.com/dotnet/runtime/blame/8fc68f626a11d646109a758cb0fc70a0aa7826f1/src/libraries/System.Net.Http/src/System/Net/Http/HttpResponseMessage.cs#L46
            if (destinationResponse.Content == null)
            {
                throw new InvalidOperationException("A response content is required for upgrades.");
            }

            // :: Step 7-A-1: Upgrade the client channel. This will also send response headers.
            var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();
            Stream upgradeResult;
            try
            {
                upgradeResult = await upgradeFeature.UpgradeAsync();
            }
            catch (Exception ex)
            {
                destinationResponse.Dispose();
                ReportProxyError(context, ProxyError.UpgradeResponseClient, ex);
                return;
            }
            using var clientStream = upgradeResult;

            // :: Step 7-A-2: Copy duplex streams
            using var destinationStream = await destinationResponse.Content.ReadAsStreamAsync();

            using var abortTokenSource = CancellationTokenSource.CreateLinkedTokenSource(longCancellation);

            var requestTask = StreamCopier.CopyAsync(isRequest: true, clientStream, destinationStream, _clock, abortTokenSource.Token);
            var responseTask = StreamCopier.CopyAsync(isRequest: false, destinationStream, clientStream, _clock, abortTokenSource.Token);

            // Make sure we report the first failure.
            var firstTask = await Task.WhenAny(requestTask, responseTask);
            var requestFinishedFirst = firstTask == requestTask;
            var secondTask = requestFinishedFirst ? responseTask : requestTask;

            var (firstResult, firstException) = await firstTask;
            if (firstResult != StreamCopyResult.Success)
            {
                ReportResult(context, requestFinishedFirst, firstResult, firstException);
                // Cancel the other direction
                abortTokenSource.Cancel();
                // Wait for this to finish before exiting so the resources get cleaned up properly.
                await secondTask;
            }
            else
            {
                var (secondResult, secondException) = await secondTask;
                if (secondResult != StreamCopyResult.Success)
                {
                    ReportResult(context, requestFinishedFirst, secondResult, secondException);
                }
            }

            void ReportResult(HttpContext context, bool reqeuest, StreamCopyResult result, Exception exception)
            {
                var error = result switch
                {
                    StreamCopyResult.InputError => reqeuest ? ProxyError.UpgradeRequestClient : ProxyError.UpgradeResponseDestination,
                    StreamCopyResult.OutputError => reqeuest ? ProxyError.UpgradeRequestDestination : ProxyError.UpgradeResponseClient,
                    StreamCopyResult.Canceled => reqeuest ? ProxyError.UpgradeRequestCanceled : ProxyError.UpgradeResponseCanceled,
                    _ => throw new NotImplementedException(result.ToString()),
                };
                ReportProxyError(context, error, exception);
            }
        }

        private async Task<(StreamCopyResult, Exception)> CopyResponseBodyAsync(HttpContent destinationResponseContent, Stream clientResponseStream,
            CancellationToken cancellation)
        {
            // SocketHttpHandler and similar transports always provide an HttpContent object, even if it's empty.
            // In 3.1 this is only likely to return null in tests.
            // As of 5.0 HttpResponse.Content never returns null.
            // https://github.com/dotnet/runtime/blame/8fc68f626a11d646109a758cb0fc70a0aa7826f1/src/libraries/System.Net.Http/src/System/Net/Http/HttpResponseMessage.cs#L46
            if (destinationResponseContent != null)
            {
                using var destinationResponseStream = await destinationResponseContent.ReadAsStreamAsync();
                return await StreamCopier.CopyAsync(isRequest: false, destinationResponseStream, clientResponseStream, _clock, cancellation);
            }

            return (StreamCopyResult.Success, null);
        }

        private async Task HandleResponseBodyErrorAsync(HttpContext context, StreamCopyHttpContent requestContent, StreamCopyResult responseBodyCopyResult, Exception responseBodyException)
        {
            if (requestContent?.ConsumptionTask.IsCompleted == true)
            {
                var (requestBodyCopyResult, requestBodyError) = await requestContent.ConsumptionTask;

                // Check for request body errors, these may have triggered the response error.
                if (requestBodyCopyResult != StreamCopyResult.Success)
                {
                    HandleRequestBodyFailure(context, requestBodyCopyResult, requestBodyError, responseBodyException);
                    return;
                }
            }

            var error = responseBodyCopyResult switch
            {
                StreamCopyResult.InputError => ProxyError.ResponseBodyDestination,
                StreamCopyResult.OutputError => ProxyError.ResponseBodyClient,
                StreamCopyResult.Canceled => ProxyError.ResponseBodyCanceled,
                _ => throw new NotImplementedException(responseBodyCopyResult.ToString()),
            };
            ReportProxyError(context, error, responseBodyException);

            if (!context.Response.HasStarted)
            {
                // Nothing has been sent to the client yet, we can still send a good error response.
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            // The response has already started, we must forcefully terminate it so the client doesn't get the
            // the mistaken impression that the truncated response is complete.
            ResetOrAbort(context, isCancelled: responseBodyCopyResult == StreamCopyResult.Canceled);
        }

        private static Task CopyResponseTrailingHeadersAsync(HttpResponseMessage source, HttpContext context, HttpTransforms transforms)
        {
            // NOTE: Deliberately not using `context.Response.SupportsTrailers()`, `context.Response.AppendTrailer(...)`
            // because they lookup `IHttpResponseTrailersFeature` for every call. Here we do it just once instead.
            var responseTrailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
            var outgoingTrailers = responseTrailersFeature?.Trailers;
            if (outgoingTrailers != null && !outgoingTrailers.IsReadOnly)
            {
                // Note that trailers, if any, should already have been declared in Proxy's response
                // by virtue of us having proxied all response headers in step 6.
                if (transforms.CopyResponseTrailers)
                {
                    CopyHeaders(source.TrailingHeaders, outgoingTrailers);
                }

                if (transforms.OnResponseTrailers != null)
                {
                    return transforms.OnResponseTrailers.Invoke(context, source);
                }
            }

            return Task.CompletedTask;
        }

        private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
        {
            foreach (var header in source)
            {
                var headerName = header.Key;
                // TODO: this list only contains "Transfer-Encoding" because that messes up Kestrel. If we don't need to add any more here then it would be more efficient to
                // check for the single value directly.
                if (_responseHeadersToSkip.Contains(headerName))
                {
                    continue;
                }
                destination.Append(headerName, header.Value.ToArray());
            }
        }

        /// <summary>
        /// Disable some ASP .NET Core server limits so that we can handle long-running gRPC requests unconstrained.
        /// Note that the gRPC server implementation on ASP .NET Core does the same for client-streaming and duplex methods.
        /// Since in Gateway we have no way to determine if the current request requires client-streaming or duplex comm,
        /// we do this for *all* incoming requests that look like they might be gRPC.
        /// </summary>
        /// <remarks>
        /// Inspired on
        /// <see href="https://github.com/grpc/grpc-dotnet/blob/3ce9b104524a4929f5014c13cd99ba9a1c2431d4/src/Grpc.AspNetCore.Server/Internal/CallHandlers/ServerCallHandlerBase.cs#L127"/>.
        /// </remarks>
        private void DisableMinRequestBodyDataRateAndMaxRequestBodySize(HttpContext httpContext)
        {
            var minRequestBodyDataRateFeature = httpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();
            if (minRequestBodyDataRateFeature != null)
            {
                minRequestBodyDataRateFeature.MinDataRate = null;
            }

            var maxRequestBodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxRequestBodySizeFeature != null)
            {
                if (!maxRequestBodySizeFeature.IsReadOnly)
                {
                    maxRequestBodySizeFeature.MaxRequestBodySize = null;
                }
                else
                {
                    // IsReadOnly could be true if middleware has already started reading the request body
                    // In that case we can't disable the max request body size for the request stream
                    _logger.LogWarning("Unable to disable max request body size.");
                }
            }
        }

        private void ReportProxyError(HttpContext context, ProxyError error, Exception ex)
        {
            context.Features.Set<IProxyErrorFeature>(new ProxyErrorFeature(error, ex));
            Log.ErrorProxying(_logger, error, ex);
            ProxyTelemetry.Log.ProxyFailed(error);
        }

        private static void ResetOrAbort(HttpContext context, bool isCancelled)
        {
            var resetFeature = context.Features.Get<IHttpResetFeature>();
            if (resetFeature != null)
            {
                // https://tools.ietf.org/html/rfc7540#section-7
                const int Cancelled = 2;
                const int InternalError = 8;
                resetFeature.Reset(isCancelled ? Cancelled : InternalError);
                return;
            }

            context.Abort();
        }

        private static class Log
        {
            private static readonly Action<ILogger, Exception> _httpDowngradeDetected = LoggerMessage.Define(
                LogLevel.Debug,
                EventIds.HttpDowngradeDetected,
                "The request was downgraded from HTTP/2.");

            private static readonly Action<ILogger, string, Exception> _proxying = LoggerMessage.Define<string>(
                LogLevel.Information,
                EventIds.Proxying,
                "Proxying to {targetUrl}");

            private static readonly Action<ILogger, ProxyError, string, Exception> _proxyError = LoggerMessage.Define<ProxyError, string>(
                LogLevel.Information,
                EventIds.ProxyError,
                "{error}: {message}");

            public static void HttpDowngradeDetected(ILogger logger)
            {
                _httpDowngradeDetected(logger, null);
            }

            public static void Proxying(ILogger logger, string targetUrl)
            {
                _proxying(logger, targetUrl, null);
            }

            public static void ErrorProxying(ILogger logger, ProxyError error, Exception ex)
            {
                _proxyError(logger, error, GetMessage(error), ex);
            }

            private static string GetMessage(ProxyError error)
            {
                return error switch
                {
                    ProxyError.None => throw new NotSupportedException("A more specific error must be used"),
                    ProxyError.Request => "An error was encountered before receiving a response.",
                    ProxyError.RequestTimedOut => "The request timed out before receiving a response.",
                    ProxyError.RequestCanceled => "The request was canceled before receiving a response.",
                    ProxyError.RequestBodyCanceled => "Copying the request body was canceled.",
                    ProxyError.RequestBodyClient => "The client reported an error when copying the request body.",
                    ProxyError.RequestBodyDestination => "The destination reported an error when copying the request body.",
                    ProxyError.ResponseBodyCanceled => "Copying the response body was canceled.",
                    ProxyError.ResponseBodyClient => "The client reported an error when copying the response body.",
                    ProxyError.ResponseBodyDestination => "The destination reported an error when copying the response body.",
                    ProxyError.UpgradeRequestCanceled => "Copying the upgraded request body was canceled.",
                    ProxyError.UpgradeRequestClient => "The client reported an error when copying the upgraded request body.",
                    ProxyError.UpgradeRequestDestination => "The destination reported an error when copying the upgraded request body.",
                    ProxyError.UpgradeResponseCanceled => "Copying the upgraded response body was canceled.",
                    ProxyError.UpgradeResponseClient => "The client reported an error when copying the upgraded response body.",
                    ProxyError.UpgradeResponseDestination => "The destination reported an error when copying the upgraded response body.",
                    ProxyError.NoAvailableDestinations => throw new NotImplementedException(), // Not used in this class
                    _ => throw new NotImplementedException(error.ToString()),
                };
            }
        }
    }
}
