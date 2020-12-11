// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.ReverseProxy.Common.Tests;
using Microsoft.ReverseProxy.RuntimeModel;
using Microsoft.ReverseProxy.Service.RuntimeModel.Transforms;
using Microsoft.ReverseProxy.Telemetry;
using Microsoft.ReverseProxy.Utilities;
using Moq;
using Xunit;

namespace Microsoft.ReverseProxy.Service.Proxy.Tests
{
    public class HttpProxyTests
    {
        private IHttpProxy CreateProxy()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpProxy();
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IHttpProxy>();
        }

        [Fact]
        public void Constructor_Works()
        {
            Assert.NotNull(CreateProxy());
        }

        // Tests normal (as opposed to upgradeable) request proxying.
        [Fact]
        public async Task ProxyAsync_NormalRequest_Works()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Path = "/path/base/dropped";
            httpContext.Request.Path = "/api/test";
            httpContext.Request.QueryString = new QueryString("?a=b&c=d");
            httpContext.Request.Headers.Add(":authority", "example.com:3456");
            httpContext.Request.Headers.Add("x-ms-request-test", "request");
            httpContext.Request.Headers.Add("Content-Language", "requestLanguage");
            httpContext.Request.Headers.Add("Content-Length", "1");
            httpContext.Request.Body = StringToStream("request content");
            httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/a/b/";
            var targetUri = "https://localhost:123/a/b/api/test?a=b&c=d";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    await Task.Yield();

                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(targetUri, request.RequestUri.AbsoluteUri);
                    Assert.Contains("request", request.Headers.GetValues("x-ms-request-test"));
                    Assert.Equal("example.com:3456", request.Headers.Host);
                    Assert.False(request.Headers.TryGetValues(":authority", out var value));

                    Assert.NotNull(request.Content);
                    Assert.Contains("requestLanguage", request.Content.Headers.GetValues("Content-Language"));

                    var capturedRequestContent = new MemoryStream();

                    // Use CopyToAsync as this is what HttpClient and friends use internally
                    await request.Content.CopyToAsync(capturedRequestContent);
                    capturedRequestContent.Position = 0;
                    var capturedContentText = StreamToString(capturedRequestContent);
                    Assert.Equal("request content", capturedContentText);

                    var response = new HttpResponseMessage((HttpStatusCode)234);
                    response.ReasonPhrase = "Test Reason Phrase";
                    response.Headers.TryAddWithoutValidation("x-ms-response-test", "response");
                    response.Content = new StreamContent(StringToStream("response content"));
                    response.Content.Headers.TryAddWithoutValidation("Content-Language", "responseLanguage");
                    return response;
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(234, httpContext.Response.StatusCode);
            var reasonPhrase = httpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase;
            Assert.Equal("Test Reason Phrase", reasonPhrase);
            Assert.Contains("response", httpContext.Response.Headers["x-ms-response-test"].ToArray());
            Assert.Contains("responseLanguage", httpContext.Response.Headers["Content-Language"].ToArray());

            proxyResponseStream.Position = 0;
            var proxyResponseText = StreamToString(proxyResponseStream);
            Assert.Equal("response content", proxyResponseText);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages();
        }

        [Fact]
        public async Task ProxyAsync_NormalRequestWithTransforms_Works()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Protocol = "http/2";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Path = "/path/base/dropped";
            httpContext.Request.Path = "/api/test";
            httpContext.Request.QueryString = new QueryString("?a=b&c=d");
            httpContext.Request.Headers.Add(":authority", "example.com:3456");
            httpContext.Request.Headers.Add("x-ms-request-test", "request");
            httpContext.Request.Headers.Add("Content-Language", "requestLanguage");
            httpContext.Request.Body = StringToStream("request content");
            httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            httpContext.Features.Set<IHttpResponseTrailersFeature>(new TestTrailersFeature());

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/a/b/";
            var transforms = new HttpTransforms()
            {
                OnRequest = (context, request, destination) =>
                {
                    request.RequestUri = new Uri(destination + "prefix"
                        + context.Request.Path + context.Request.QueryString);
                    request.Headers.Remove("transformHeader");
                    request.Headers.TryAddWithoutValidation("transformHeader", "value");
                    request.Headers.TryAddWithoutValidation("x-ms-request-test", "transformValue");
                    request.Headers.Host = null;
                    return Task.CompletedTask;
                },
                OnResponse = (context, response) =>
                {
                    context.Response.Headers["transformHeader"] = "value";
                    context.Response.Headers.Append("x-ms-response-test", "value");
                    return Task.CompletedTask;
                },
                OnResponseTrailers = (context, response) =>
                {
                    context.Response.AppendTrailer("trailerTransform", "value");
                    return Task.CompletedTask;
                }
            };

            var targetUri = "https://localhost:123/a/b/prefix/api/test?a=b&c=d";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    await Task.Yield();

                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(targetUri, request.RequestUri.AbsoluteUri);
                    Assert.Equal(new[] { "value" }, request.Headers.GetValues("transformHeader"));
                    Assert.Equal(new[] { "request", "transformValue" }, request.Headers.GetValues("x-ms-request-test"));
                    Assert.Null(request.Headers.Host);
                    Assert.False(request.Headers.TryGetValues(":authority", out var value));

                    Assert.NotNull(request.Content);
                    Assert.Contains("requestLanguage", request.Content.Headers.GetValues("Content-Language"));

                    var capturedRequestContent = new MemoryStream();

                    // Use CopyToAsync as this is what HttpClient and friends use internally
                    await request.Content.CopyToAsync(capturedRequestContent);
                    capturedRequestContent.Position = 0;
                    var capturedContentText = StreamToString(capturedRequestContent);
                    Assert.Equal("request content", capturedContentText);

                    var response = new HttpResponseMessage((HttpStatusCode)234);
                    response.ReasonPhrase = "Test Reason Phrase";
                    response.Headers.TryAddWithoutValidation("x-ms-response-test", "response");
                    response.Content = new StreamContent(StringToStream("response content"));
                    response.Content.Headers.TryAddWithoutValidation("Content-Language", "responseLanguage");
                    return response;
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, transforms, default);

            Assert.Equal(234, httpContext.Response.StatusCode);
            var reasonPhrase = httpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase;
            Assert.Equal("Test Reason Phrase", reasonPhrase);
            Assert.Equal(new[] { "response", "value" }, httpContext.Response.Headers["x-ms-response-test"].ToArray());
            Assert.Contains("responseLanguage", httpContext.Response.Headers["Content-Language"].ToArray());
            Assert.Contains("value", httpContext.Response.Headers["transformHeader"].ToArray());
            Assert.Equal(new[] { "value" }, httpContext.Features.Get<IHttpResponseTrailersFeature>().Trailers?["trailerTransform"].ToArray());

            proxyResponseStream.Position = 0;
            var proxyResponseText = StreamToString(proxyResponseStream);
            Assert.Equal("response content", proxyResponseText);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages();
        }

        [Fact]
        public async Task ProxyAsync_NormalRequestWithCopyRequestHeadersDisabled_Works()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.PathBase = "/api";
            httpContext.Request.Path = "/test";
            httpContext.Request.QueryString = new QueryString("?a=b&c=d");
            httpContext.Request.Headers.Add(":authority", "example.com:3456");
            httpContext.Request.Headers.Add("x-ms-request-test", "request");
            httpContext.Request.Headers.Add("Content-Language", "requestLanguage");
            httpContext.Request.Headers.Add("Transfer-Encoding", "chunked");
            httpContext.Request.Body = StringToStream("request content");
            httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/a/b/";
            var transforms = new HttpTransforms()
            {
                CopyRequestHeaders = false,
                OnRequest = (context, request, destination) =>
                {
                    request.Headers.TryAddWithoutValidation("x-ms-request-test", "transformValue");
                    return Task.CompletedTask;
                }
            };
            var targetUri = "https://localhost:123/a/b/test?a=b&c=d";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    await Task.Yield();

                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(targetUri, request.RequestUri.AbsoluteUri);
                    Assert.Equal(new[] { "transformValue" }, request.Headers.GetValues("x-ms-request-test"));
                    Assert.Null(request.Headers.Host);
                    Assert.False(request.Headers.TryGetValues(":authority", out var _));

                    Assert.NotNull(request.Content);
                    Assert.False(request.Content.Headers.TryGetValues("Content-Language", out var _));

                    var capturedRequestContent = new MemoryStream();

                    // Use CopyToAsync as this is what HttpClient and friends use internally
                    await request.Content.CopyToAsync(capturedRequestContent);
                    capturedRequestContent.Position = 0;
                    var capturedContentText = StreamToString(capturedRequestContent);
                    Assert.Equal("request content", capturedContentText);

                    var response = new HttpResponseMessage((HttpStatusCode)234);
                    response.ReasonPhrase = "Test Reason Phrase";
                    response.Headers.TryAddWithoutValidation("x-ms-response-test", "response");
                    response.Content = new StreamContent(StringToStream("response content"));
                    response.Content.Headers.TryAddWithoutValidation("Content-Language", "responseLanguage");
                    return response;
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, transforms, default);

            Assert.Equal(234, httpContext.Response.StatusCode);
            var reasonPhrase = httpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase;
            Assert.Equal("Test Reason Phrase", reasonPhrase);
            Assert.Contains("response", httpContext.Response.Headers["x-ms-response-test"].ToArray());
            Assert.Contains("responseLanguage", httpContext.Response.Headers["Content-Language"].ToArray());

            proxyResponseStream.Position = 0;
            var proxyResponseText = StreamToString(proxyResponseStream);
            Assert.Equal("response content", proxyResponseText);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages();
        }

        // Tests proxying an upgradeable request.
        [Theory]
        [InlineData("WebSocket")]
        [InlineData("SPDY/3.1")]
        public async Task ProxyAsync_UpgradableRequest_Works(string upgradeHeader)
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Path = "/api/test";
            httpContext.Request.QueryString = new QueryString("?a=b&c=d");
            httpContext.Request.Headers.Add(":authority", "example.com:3456");
            httpContext.Request.Headers.Add("x-ms-request-test", "request");
            httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

            // TODO: https://github.com/microsoft/reverse-proxy/issues/255
            // https://github.com/microsoft/reverse-proxy/issues/467
            httpContext.Request.Headers.Add("Upgrade", upgradeHeader);

            var downstreamStream = new DuplexStream(
                readStream: StringToStream("request content"),
                writeStream: new MemoryStream());
            DuplexStream upstreamStream = null;

            var upgradeFeatureMock = new Mock<IHttpUpgradeFeature>();
            upgradeFeatureMock.SetupGet(u => u.IsUpgradableRequest).Returns(true);
            upgradeFeatureMock.Setup(u => u.UpgradeAsync()).ReturnsAsync(downstreamStream);
            httpContext.Features.Set(upgradeFeatureMock.Object);

            var destinationPrefix = "https://localhost:123/a/b/";
            var targetUri = "https://localhost:123/a/b/api/test?a=b&c=d";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    await Task.Yield();

                    Assert.Equal(new Version(1, 1), request.Version);
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal(targetUri, request.RequestUri.AbsoluteUri);
                    Assert.Contains("request", request.Headers.GetValues("x-ms-request-test"));
                    Assert.Equal("example.com:3456", request.Headers.Host);
                    Assert.False(request.Headers.TryGetValues(":authority", out var value));

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols);
                    response.Headers.TryAddWithoutValidation("x-ms-response-test", "response");
                    upstreamStream = new DuplexStream(
                        readStream: StringToStream("response content"),
                        writeStream: new MemoryStream());
                    response.Content = new RawStreamContent(upstreamStream);
                    return response;
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status101SwitchingProtocols, httpContext.Response.StatusCode);
            Assert.Contains("response", httpContext.Response.Headers["x-ms-response-test"].ToArray());

            downstreamStream.WriteStream.Position = 0;
            var returnedToDownstream = StreamToString(downstreamStream.WriteStream);
            Assert.Equal("response content", returnedToDownstream);

            Assert.NotNull(upstreamStream);
            upstreamStream.WriteStream.Position = 0;
            var sentToUpstream = StreamToString(upstreamStream.WriteStream);
            Assert.Equal("request content", sentToUpstream);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(upgrade: true);
        }

        // Tests proxying an upgradeable request where the destination refused to upgrade.
        // We should still proxy back the response.
        [Fact]
        public async Task ProxyAsync_UpgradableRequestFailsToUpgrade_ProxiesResponse()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("example.com");
            httpContext.Request.Path = "/api/test";
            httpContext.Request.QueryString = new QueryString("?a=b&c=d");
            httpContext.Request.Headers.Add(":host", "example.com");
            httpContext.Request.Headers.Add("x-ms-request-test", "request");

            // TODO: https://github.com/microsoft/reverse-proxy/issues/255
            httpContext.Request.Headers.Add("Upgrade", "WebSocket");

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var upgradeFeatureMock = new Mock<IHttpUpgradeFeature>(MockBehavior.Strict);
            upgradeFeatureMock.SetupGet(u => u.IsUpgradableRequest).Returns(true);
            httpContext.Features.Set(upgradeFeatureMock.Object);

            var destinationPrefix = "https://localhost:123/a/b/";
            var targetUri = "https://localhost:123/a/b/api/test?a=b&c=d";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    await Task.Yield();

                    Assert.Equal(new Version(1, 1), request.Version);
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal(targetUri, request.RequestUri.AbsoluteUri);
                    Assert.Contains("request", request.Headers.GetValues("x-ms-request-test"));

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage((HttpStatusCode)234);
                    response.ReasonPhrase = "Test Reason Phrase";
                    response.Headers.TryAddWithoutValidation("x-ms-response-test", "response");
                    response.Content = new StreamContent(StringToStream("response content"));
                    response.Content.Headers.TryAddWithoutValidation("Content-Language", "responseLanguage");
                    return response;
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(234, httpContext.Response.StatusCode);
            var reasonPhrase = httpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase;
            Assert.Equal("Test Reason Phrase", reasonPhrase);
            Assert.Contains("response", httpContext.Response.Headers["x-ms-response-test"].ToArray());
            Assert.Contains("responseLanguage", httpContext.Response.Headers["Content-Language"].ToArray());

            proxyResponseStream.Position = 0;
            var proxyResponseText = StreamToString(proxyResponseStream);
            Assert.Equal("response content", proxyResponseText);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false, upgrade: false);
        }

        [Theory]
        [InlineData("TRACE", "HTTP/1.1", "")]
        [InlineData("TRACE", "HTTP/2", "")]
        [InlineData("GET", "HTTP/1.1", "")]
        [InlineData("GET", "HTTP/2", "")]
        [InlineData("GET", "HTTP/1.1", "Content-Length:0")]
        [InlineData("HEAD", "HTTP/1.1", "")]
        [InlineData("POST", "HTTP/1.1", "")]
        [InlineData("POST", "HTTP/1.1", "Content-Length:0")]
        [InlineData("POST", "HTTP/2", "Content-Length:0")]
        [InlineData("PATCH", "HTTP/1.1", "")]
        [InlineData("DELETE", "HTTP/1.1", "")]
        [InlineData("Unknown", "HTTP/1.1", "")]
        // [InlineData("CONNECT", "HTTP/1.1", "")] Blocked in HttpUtilities.GetHttpMethod
        public async Task ProxyAsync_RequetsWithoutBodies_NoHttpContent(string method, string protocol, string headers)
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = method;
            httpContext.Request.Protocol = protocol;
            foreach (var header in headers.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = header.Split(':');
                var key = parts[0];
                var value = parts[1];
                httpContext.Request.Headers[key] = value;
            }

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal(method, request.Method.Method, StringComparer.OrdinalIgnoreCase);

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    return Task.FromResult(response);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Theory]
        [InlineData("POST", "HTTP/2", "")]
        [InlineData("PATCH", "HTTP/2", "")]
        [InlineData("UNKNOWN", "HTTP/2", "")]
        [InlineData("UNKNOWN", "HTTP/1.1", "Content-Length:10")]
        [InlineData("UNKNOWN", "HTTP/1.1", "transfer-encoding:Chunked")]
        [InlineData("GET", "HTTP/1.1", "Content-Length:10")]
        [InlineData("GET", "HTTP/2", "Content-Length:10")]
        [InlineData("HEAD", "HTTP/1.1", "transfer-encoding:Chunked")]
        [InlineData("HEAD", "HTTP/2", "transfer-encoding:Chunked")]
        public async Task ProxyAsync_RequetsWithBodies_HasHttpContent(string method, string protocol, string headers)
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = method;
            httpContext.Request.Protocol = protocol;
            foreach (var header in headers.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = header.Split(':');
                var key = parts[0];
                var value = parts[1];
                httpContext.Request.Headers[key] = value;
            }

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal(method, request.Method.Method, StringComparer.OrdinalIgnoreCase);

                    Assert.NotNull(request.Content);

                    // Must consume the body
                    await request.Content.CopyToAsync(Stream.Null);

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages();
        }
#if NET
        [Fact]
        public async Task ProxyAsync_BodyDetectionFeatureSaysNo_NoHttpContent()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new TestBodyDetector() { CanHaveBody = false });

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(2, 0), request.Version);

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    return Task.FromResult(response);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_BodyDetectionFeatureSaysYes_HasHttpContent()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new TestBodyDetector() { CanHaveBody = true });

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(2, 0), request.Version);

                    Assert.NotNull(request.Content);

                    // Must consume the body
                    await request.Content.CopyToAsync(Stream.Null);

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages();
        }

        private class TestBodyDetector : IHttpRequestBodyDetectionFeature
        {
            public bool CanHaveBody { get; set; }
        }
#endif
        [Fact]
        public async Task ProxyAsync_RequestWithCookieHeaders()
        {
            var events = TestEventListener.Collect();

            // This is an invalid format per spec but may happen due to https://github.com/dotnet/aspnetcore/issues/26461
            var cookies = new [] { "testA=A_Cookie", "testB=B_Cookie", "testC=C_Cookie" };
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Headers.Add(HeaderNames.Cookie, cookies);

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // "testA=A_Cookie; testB=B_Cookie; testC=C_Cookie"
                    var expectedCookieString = string.Join("; ", cookies);

                    Assert.Equal(new Version(2, 0), request.Version);
                    Assert.Equal("GET", request.Method.Method, StringComparer.OrdinalIgnoreCase);
                    Assert.Null(request.Content);
                    Assert.True(request.Headers.TryGetValues(HeaderNames.Cookie, out var cookieHeaders));
                    Assert.NotNull(cookieHeaders);
                    var cookie = Assert.Single(cookieHeaders);
                    Assert.Equal(expectedCookieString, cookie);

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    return Task.FromResult(response);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Null(httpContext.Features.Get<IProxyErrorFeature>());
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_OptionsWithVersion()
        {
            var events = TestEventListener.Collect();

            // Use any non-default value
            var version = new Version(5, 5);
#if NET
            var versionPolicy = HttpVersionPolicy.RequestVersionExact;
#endif

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(version, request.Version);
#if NET
                    Assert.Equal(versionPolicy, request.VersionPolicy);
#endif
                    Assert.Equal("GET", request.Method.Method, StringComparer.OrdinalIgnoreCase);
                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    return Task.FromResult(response);
                });

#if NET
            var options = new RequestProxyOptions(null, version, versionPolicy);
#else
            var options = new RequestProxyOptions(null, version);
#endif
            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, options);

            Assert.Null(httpContext.Features.Get<IProxyErrorFeature>());
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_OptionsWithVersion_Transformed()
        {
            var events = TestEventListener.Collect();

            // Use any non-default value
            var version = new Version(5, 5);
            var transformedVersion = new Version(6, 6);
#if NET
            var versionPolicy = HttpVersionPolicy.RequestVersionExact;
            var transformedVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
#endif

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(transformedVersion, request.Version);
#if NET
                    Assert.Equal(transformedVersionPolicy, request.VersionPolicy);
#endif
                    Assert.Equal("GET", request.Method.Method, StringComparer.OrdinalIgnoreCase);
                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    return Task.FromResult(response);
                });

            var transforms = new HttpTransforms()
            {
                CopyRequestHeaders = false,
                OnRequest = (context, request, destination) =>
                {
                    Assert.Equal(version, request.Version);
                    request.Version = transformedVersion;
#if NET
                    Assert.Equal(versionPolicy, request.VersionPolicy);
                    request.VersionPolicy = transformedVersionPolicy;
#endif
                    return Task.CompletedTask;
                }
            };
#if NET
            var requestOptions = new RequestProxyOptions(null, version, versionPolicy);
#else
            var requestOptions = new RequestProxyOptions(null, version);
#endif
            await sut.ProxyAsync(httpContext, destinationPrefix, client, transforms, requestOptions);

            Assert.Null(httpContext.Features.Get<IProxyErrorFeature>());
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

            AssertProxyStartStop(events, destinationPrefix, httpContext.Response.StatusCode);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_UnableToConnect_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.Request, errorFeature.Error);
            Assert.IsType<HttpRequestException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_UnableToConnectWithBody_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.Request, errorFeature.Error);
            Assert.IsType<HttpRequestException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_RequestTimedOut_Returns504()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new HttpResponseMessage());
                });

            // Time out immediately
            var requestOptions = new RequestProxyOptions(TimeSpan.FromTicks(1), null);

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, requestOptions);

            Assert.Equal(StatusCodes.Status504GatewayTimeout, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestTimedOut, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_RequestCanceled_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.RequestAborted = new CancellationToken(canceled: true);

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new HttpResponseMessage());
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestCanceled, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_RequestWithBodyTimedOut_Returns504()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new HttpResponseMessage());
                });

            // Time out immediately
            var requestOptions = new RequestProxyOptions(TimeSpan.FromTicks(1), null);

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, requestOptions);

            Assert.Equal(StatusCodes.Status504GatewayTimeout, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestTimedOut, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_RequestWithBodyCanceled_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;
            httpContext.RequestAborted = new CancellationToken(canceled: true);

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new HttpResponseMessage());
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestCanceled, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyClientErrorBeforeResponseError_Returns400()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new ThrowStream(throwOnFirstRead: true);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // Should throw.
                    await request.Content.CopyToAsync(Stream.Null);
                    return new HttpResponseMessage();
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyClient, errorFeature.Error);
            Assert.IsType<AggregateException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] {
                ProxyStage.SendAsyncStart,
                ProxyStage.RequestContentTransferStart
            });
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyDestinationErrorBeforeResponseError_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // Doesn't throw for destination errors
                    await request.Content.CopyToAsync(new ThrowStream());
                    throw new HttpRequestException();
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyDestination, errorFeature.Error);
            Assert.IsType<AggregateException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] {
                ProxyStage.SendAsyncStart,
                ProxyStage.RequestContentTransferStart
            });
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyCanceledBeforeResponseError_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;
            httpContext.RequestAborted = new CancellationToken(canceled: true);

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                async (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // should throw
                    try
                    {
                        await request.Content.CopyToAsync(new MemoryStream());
                    }
                    catch (OperationCanceledException) { }
                    throw new HttpRequestException();
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyCanceled, errorFeature.Error);
            Assert.IsType<AggregateException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(new[] { ProxyStage.SendAsyncStart });
        }

        [Fact]
        public async Task ProxyAsync_ResponseBodyDestionationErrorFirstRead_Returns502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    var message = new HttpResponseMessage()
                    {
                        Content = new StreamContent(new ThrowStream(throwOnFirstRead: true))
                    };
                    message.Headers.AcceptRanges.Add("bytes");
                    return Task.FromResult(message);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            Assert.Empty(httpContext.Response.Headers);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.ResponseBodyDestination, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_ResponseBodyDestionationErrorSecondRead_Aborted()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");
            var responseBody = new TestResponseBody();
            httpContext.Features.Set<IHttpResponseFeature>(responseBody);
            httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);
            httpContext.Features.Set<IHttpRequestLifetimeFeature>(responseBody);

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    var message = new HttpResponseMessage()
                    {
                        Content = new StreamContent(new ThrowStream(throwOnFirstRead: false))
                    };
                    message.Headers.AcceptRanges.Add("bytes");
                    return Task.FromResult(message);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(1, responseBody.InnerStream.Length);
            Assert.True(responseBody.Aborted);
            Assert.Equal("bytes", httpContext.Response.Headers[HeaderNames.AcceptRanges]);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.ResponseBodyDestination, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_ResponseBodyClientError_Aborted()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");
            var responseBody = new TestResponseBody(new ThrowStream());
            httpContext.Features.Set<IHttpResponseFeature>(responseBody);
            httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);
            httpContext.Features.Set<IHttpRequestLifetimeFeature>(responseBody);

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    var message = new HttpResponseMessage()
                    {
                        Content = new StreamContent(new MemoryStream(new byte[1]))
                    };
                    message.Headers.AcceptRanges.Add("bytes");
                    return Task.FromResult(message);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.True(responseBody.Aborted);
            Assert.Equal("bytes", httpContext.Response.Headers[HeaderNames.AcceptRanges]);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.ResponseBodyClient, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_ResponseBodyCancelled_502()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");
            var responseBody = new TestResponseBody();
            httpContext.Features.Set<IHttpResponseFeature>(responseBody);
            httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);
            httpContext.Features.Set<IHttpRequestLifetimeFeature>(responseBody);
            httpContext.RequestAborted = new CancellationToken(canceled: true);

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    var message = new HttpResponseMessage()
                    {
                        Content = new StreamContent(new MemoryStream(new byte[1]))
                    };
                    message.Headers.AcceptRanges.Add("bytes");
                    return Task.FromResult(message);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
            Assert.False(responseBody.Aborted);
            Assert.Empty(httpContext.Response.Headers);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.ResponseBodyCanceled, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_ResponseBodyCancelledAfterStart_Aborted()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Host = new HostString("example.com:3456");
            var responseBody = new TestResponseBody() { HasStarted = true };
            httpContext.Features.Set<IHttpResponseFeature>(responseBody);
            httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);
            httpContext.Features.Set<IHttpRequestLifetimeFeature>(responseBody);
            httpContext.RequestAborted = new CancellationToken(canceled: true);

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    var message = new HttpResponseMessage()
                    {
                        Content = new StreamContent(new MemoryStream(new byte[1]))
                    };
                    message.Headers.AcceptRanges.Add("bytes");
                    return Task.FromResult(message);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.True(responseBody.Aborted);
            Assert.Equal("bytes", httpContext.Response.Headers[HeaderNames.AcceptRanges]);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.ResponseBodyCanceled, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(hasRequestContent: false);
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyCanceledAfterResponse_Reported()
        {
            var events = TestEventListener.Collect();

            var waitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new StallStream(waitTcs.Task);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            using var longTokenSource = new CancellationTokenSource();
            httpContext.RequestAborted = longTokenSource.Token;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // Background copy
                    _ = request.Content.CopyToAsync(new MemoryStream());
                    // Make sure the request isn't canceled until the response finishes copying.
                    return Task.FromResult(new HttpResponseMessage()
                    {
                        Content = new StreamContent(new OnCompletedReadStream(() =>
                        {
                            longTokenSource.Cancel();
                            waitTcs.SetResult(0);
                        }))
                    });
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyCanceled, errorFeature.Error);
            Assert.IsType<OperationCanceledException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages();
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyClientErrorAfterResponse_Reported()
        {
            var events = TestEventListener.Collect();

            var waitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new StallStream(waitTcs.Task);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // Background copy
                    _ = request.Content.CopyToAsync(new MemoryStream());
                    // Make sure the request isn't canceled until the response finishes copying.
                    return Task.FromResult(new HttpResponseMessage()
                    {
                        Content = new StreamContent(new OnCompletedReadStream(() => waitTcs.SetResult(0)))
                    });
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyClient, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages();
        }

        [Fact]
        public async Task ProxyAsync_RequestBodyDestinationErrorAfterResponse_Reported()
        {
            var events = TestEventListener.Collect();

            var waitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "POST";
            httpContext.Request.Host = new HostString("example.com:3456");
            httpContext.Request.Body = new MemoryStream(new byte[1]);
            httpContext.Request.ContentLength = 1;

            var proxyResponseStream = new MemoryStream();
            httpContext.Response.Body = proxyResponseStream;

            var destinationPrefix = "https://localhost:123/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    // Background copy
                    _ = request.Content.CopyToAsync(new StallStream(waitTcs.Task));
                    // Make sure the request isn't canceled until the response finishes copying.
                    return Task.FromResult(new HttpResponseMessage()
                    {
                        Content = new StreamContent(new OnCompletedReadStream(() => waitTcs.SetResult(0)))
                    });
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(0, proxyResponseStream.Length);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.RequestBodyDestination, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages();
        }

        [Fact]
        public async Task ProxyAsync_UpgradableRequest_RequestBodyCopyError_CancelsResponseBody()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            // TODO: https://github.com/microsoft/reverse-proxy/issues/255
            httpContext.Request.Headers.Add("Upgrade", "WebSocket");

            var downstreamStream = new DuplexStream(
                readStream: new ThrowStream(),
                writeStream: new MemoryStream());
            DuplexStream upstreamStream = null;

            var upgradeFeatureMock = new Mock<IHttpUpgradeFeature>();
            upgradeFeatureMock.SetupGet(u => u.IsUpgradableRequest).Returns(true);
            upgradeFeatureMock.Setup(u => u.UpgradeAsync()).ReturnsAsync(downstreamStream);
            httpContext.Features.Set(upgradeFeatureMock.Object);

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(1, 1), request.Version);
                    Assert.Equal(HttpMethod.Get, request.Method);

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols);
                    upstreamStream = new DuplexStream(
                        readStream: new StallStream(ct =>
                        {
                            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                            ct.Register(() => tcs.SetResult(0));
                            return tcs.Task.DefaultTimeout();
                        }),
                        writeStream: new MemoryStream());
                    response.Content = new RawStreamContent(upstreamStream);
                    return Task.FromResult(response);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status101SwitchingProtocols, httpContext.Response.StatusCode);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.UpgradeRequestClient, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(upgrade: true);
        }

        [Fact]
        public async Task ProxyAsync_UpgradableRequest_ResponseBodyCopyError_CancelsRequestBody()
        {
            var events = TestEventListener.Collect();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "GET";
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("example.com:3456");
            // TODO: https://github.com/microsoft/reverse-proxy/issues/255
            httpContext.Request.Headers.Add("Upgrade", "WebSocket");

            var downstreamStream = new DuplexStream(
                readStream: new StallStream(ct =>
                {
                    var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.SetResult(0));
                    return tcs.Task.DefaultTimeout();
                }),
                writeStream: new MemoryStream());
            DuplexStream upstreamStream = null;

            var upgradeFeatureMock = new Mock<IHttpUpgradeFeature>();
            upgradeFeatureMock.SetupGet(u => u.IsUpgradableRequest).Returns(true);
            upgradeFeatureMock.Setup(u => u.UpgradeAsync()).ReturnsAsync(downstreamStream);
            httpContext.Features.Set(upgradeFeatureMock.Object);

            var destinationPrefix = "https://localhost/";
            var sut = CreateProxy();
            var client = MockHttpHandler.CreateClient(
                (HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    Assert.Equal(new Version(1, 1), request.Version);
                    Assert.Equal(HttpMethod.Get, request.Method);

                    Assert.Null(request.Content);

                    var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols);
                    upstreamStream = new DuplexStream(
                        readStream: new ThrowStream(),
                        writeStream: new MemoryStream());
                    response.Content = new RawStreamContent(upstreamStream);
                    return Task.FromResult(response);
                });

            await sut.ProxyAsync(httpContext, destinationPrefix, client, null, default);

            Assert.Equal(StatusCodes.Status101SwitchingProtocols, httpContext.Response.StatusCode);
            var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
            Assert.Equal(ProxyError.UpgradeResponseDestination, errorFeature.Error);
            Assert.IsType<IOException>(errorFeature.Exception);

            AssertProxyStartFailedStop(events, destinationPrefix, httpContext.Response.StatusCode, errorFeature.Error);
            events.AssertContainProxyStages(upgrade: true);
        }

        [Fact]
        public async Task ProxyAsync_WithHttpClient_Fails()
        {
            var httpClient = new HttpClient();
            var httpContext = new DefaultHttpContext();
            var destinationPrefix = "";
            var transforms = HttpTransforms.Empty;
            var requestOptions = default(RequestProxyOptions);
            var proxy = CreateProxy();

            await Assert.ThrowsAsync<ArgumentException>(() => proxy.ProxyAsync(httpContext,
                destinationPrefix, httpClient, transforms, requestOptions));
        }

        private static void AssertProxyStartStop(List<EventWrittenEventArgs> events, string destinationPrefix, int statusCode)
        {
            AssertProxyStartFailedStop(events, destinationPrefix, statusCode, error: null);
        }

        private static void AssertProxyStartFailedStop(List<EventWrittenEventArgs> events, string destinationPrefix, int statusCode, ProxyError? error)
        {
            var start = Assert.Single(events, e => e.EventName == "ProxyStart");
            var prefixActual = (string)Assert.Single(start.Payload);
            Assert.Equal(destinationPrefix, prefixActual);

            var stop = Assert.Single(events, e => e.EventName == "ProxyStop");
            var statusActual = (int)Assert.Single(stop.Payload);
            Assert.Equal(statusCode, statusActual);
            Assert.True(start.TimeStamp <= stop.TimeStamp);

            if (error is null)
            {
                Assert.DoesNotContain(events, e => e.EventName == "ProxyFailed");
            }
            else
            {
                var failed = Assert.Single(events, e => e.EventName == "ProxyFailed");
                var errorActual = (ProxyError)Assert.Single(failed.Payload);
                Assert.Equal(error.Value, errorActual);
                Assert.True(start.TimeStamp <= failed.TimeStamp);
                Assert.True(failed.TimeStamp <= stop.TimeStamp);
            }
        }

        private static MemoryStream StringToStream(string text)
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return stream;
        }

        private static string StreamToString(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }

        private class DuplexStream : Stream
        {
            public DuplexStream(Stream readStream, Stream writeStream)
            {
                ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
                WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
            }

            public Stream ReadStream { get; }

            public Stream WriteStream { get; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotImplementedException();

            public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadStream.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadStream.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return ReadStream.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteStream.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return WriteStream.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return WriteStream.WriteAsync(buffer, cancellationToken);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Replacement for <see cref="StreamContent"/> which just returns the raw stream,
        /// whereas <see cref="StreamContent"/> wraps it in a read-only stream.
        /// We need to return the raw internal stream to test full duplex proxying.
        /// </summary>
        private class RawStreamContent : HttpContent
        {
            private readonly Stream stream;

            public RawStreamContent(Stream stream)
            {
                this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult(stream);
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                throw new NotImplementedException();
            }

            protected override bool TryComputeLength(out long length)
            {
                throw new NotImplementedException();
            }
        }

        private class TestTrailersFeature : IHttpResponseTrailersFeature
        {
            public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
        }

        private class ThrowStream : DelegatingStream
        {
            private bool _firstRead = true;

            public ThrowStream(bool throwOnFirstRead = true)
                : base(Stream.Null)
            {
                ThrowOnFirstRead = throwOnFirstRead;
            }

            public bool ThrowOnFirstRead { get; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_firstRead && !ThrowOnFirstRead)
                {
                    _firstRead = false;
                    return new ValueTask<int>(1);
                }
                throw new IOException("Fake connection issue");
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("Fake connection issue");
            }
        }

        private class StallStream : DelegatingStream
        {
            public StallStream(Task until)
                : this(_ => until)
            { }

            public StallStream(Func<CancellationToken, Task> onStallAction)
                : base(Stream.Null)
            {
                OnStallAction = onStallAction;
            }

            public Func<CancellationToken, Task> OnStallAction { get; }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await OnStallAction(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException();
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await OnStallAction(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException();
            }
        }

        private class TestResponseBody : DelegatingStream, IHttpResponseBodyFeature, IHttpResponseFeature, IHttpRequestLifetimeFeature
        {
            public TestResponseBody()
                : this(new MemoryStream())
            { }

            public TestResponseBody(Stream innerStream)
                : base(innerStream)
            {
                InnerStream = innerStream;
            }

            public Stream InnerStream { get; }

            public bool Aborted { get; private set; }

            public Stream Stream => this;

            public PipeWriter Writer => throw new NotImplementedException();

            public int StatusCode { get; set; } = 200;
            public string ReasonPhrase { get; set; }
            public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
            public Stream Body { get => this; set => throw new NotImplementedException(); }
            public bool HasStarted { get; set; }
            public CancellationToken RequestAborted { get; set; }

            public void Abort()
            {
                Aborted = true;
            }

            public Task CompleteAsync()
            {
                throw new NotImplementedException();
            }

            public void DisableBuffering()
            {
                throw new NotImplementedException();
            }

            public void OnCompleted(Func<object, Task> callback, object state)
            {
                throw new NotImplementedException();
            }

            public void OnStarting(Func<object, Task> callback, object state)
            {
                throw new NotImplementedException();
            }

            public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
            {
                throw new NotImplementedException();
            }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                throw new NotImplementedException();
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                OnStart();
                return base.WriteAsync(buffer, cancellationToken);
            }

            private void OnStart()
            {
                if (!HasStarted)
                {
                    HasStarted = true;
                }
            }
        }

        private class OnCompletedReadStream : DelegatingStream
        {
            public OnCompletedReadStream(Action onCompleted)
                : base(Stream.Null)
            {
                OnCompleted = onCompleted;
            }

            public Action OnCompleted { get; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                OnCompleted();
                return new ValueTask<int>(0);
            }
        }
    }
}
