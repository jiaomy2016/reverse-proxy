// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Microsoft.ReverseProxy.Service.Proxy;
using Microsoft.ReverseProxy.Service.RuntimeModel.Transforms;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.Service.Config
{
    /// <summary>
    /// Validates and builds request and response transforms for a given route.
    /// </summary>
    internal class TransformBuilder : ITransformBuilder
    {
        private static readonly HashSet<string> _responseHeadersToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HeaderNames.TransferEncoding
        };

        private readonly TemplateBinderFactory _binderFactory;
        private readonly IRandomFactory _randomFactory;

        /// <summary>
        /// Creates a new <see cref="TransformBuilder"/>
        /// </summary>
        public TransformBuilder(TemplateBinderFactory binderFactory, IRandomFactory randomFactory)
        {
            _binderFactory = binderFactory ?? throw new ArgumentNullException(nameof(binderFactory));
            _randomFactory = randomFactory ?? throw new ArgumentNullException(nameof(randomFactory));
        }

        /// <inheritdoc/>
        public IList<Exception> Validate(IList<IDictionary<string, string>> rawTransforms)
        {
            var errors = new List<Exception>();

            if (rawTransforms == null || rawTransforms.Count == 0)
            {
                return errors;
            }

            foreach (var rawTransform in rawTransforms)
            {
                if (rawTransform.TryGetValue("PathSet", out var pathSet))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("PathPrefix", out var pathPrefix))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("PathRemovePrefix", out var pathRemovePrefix))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("PathPattern", out var pathPattern))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                    // TODO: Validate the pattern format. Does it build?
                }
                else if (rawTransform.TryGetValue("QueryValueParameter", out var queryValueParameter))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Append", out var _) && !rawTransform.TryGetValue("Set", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for QueryValueParameter: {string.Join(';', rawTransform.Keys)}. Expected 'Append' or 'Set'."));
                    }
                }
                else if (rawTransform.TryGetValue("QueryRouteParameter", out var queryRouteParameter))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Append", out var _) && !rawTransform.TryGetValue("Set", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for QueryRouteParameter: {string.Join(';', rawTransform.Keys)}. Expected 'Append' or 'Set'."));
                    }
                }
                else if (rawTransform.TryGetValue("QueryRemoveParameter", out var removeQueryParameter))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("RequestHeadersCopy", out var copyHeaders))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                    if (!string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", copyHeaders, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ArgumentException($"Unexpected value for RequestHeaderCopy: {copyHeaders}. Expected 'true' or 'false'"));
                    }
                }
                else if (rawTransform.TryGetValue("RequestHeaderOriginalHost", out var originalHost))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                    if (!string.Equals("True", originalHost, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", originalHost, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ArgumentException($"Unexpected value for RequestHeaderOriginalHost: {originalHost}. Expected 'true' or 'false'"));
                    }
                }
                else if (rawTransform.TryGetValue("RequestHeader", out var headerName))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for RequestHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("ResponseHeader", out var _))
                {
                    if (rawTransform.TryGetValue("When", out var whenValue))
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 3);
                        if (!string.Equals("Always", whenValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("Success", whenValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for ResponseHeader:When: {whenValue}. Expected 'Always' or 'Success'"));
                        }
                    }
                    else
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    }

                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for ResponseHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("ResponseTrailer", out var _))
                {
                    if (rawTransform.TryGetValue("When", out var whenValue))
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 3);
                        if (!string.Equals("Always", whenValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("Success", whenValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for ResponseTrailer:When: {whenValue}. Expected 'Always' or 'Success'"));
                        }
                    }
                    else
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    }

                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for ResponseTrailer: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("X-Forwarded", out var xforwardedHeaders))
                {
                    var expected = 1;

                    if (rawTransform.TryGetValue("Append", out var appendValue))
                    {
                        expected++;
                        if (!string.Equals("True", appendValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", appendValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded:Append: {appendValue}. Expected 'true' or 'false'"));
                        }
                    }

                    if (rawTransform.TryGetValue("Prefix", out var _))
                    {
                        expected++;
                    }

                    TryCheckTooManyParameters(errors.Add, rawTransform, expected);

                    // for, host, proto, PathBase
                    var tokens = xforwardedHeaders.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var token in tokens)
                    {
                        if (!string.Equals(token, "For", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "PathBase", StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'PathBase'"));
                        }
                    }
                }
                else if (rawTransform.TryGetValue("Forwarded", out var forwardedHeader))
                {
                    var expected = 1;

                    if (rawTransform.TryGetValue("Append", out var appendValue))
                    {
                        expected++;
                        if (!string.Equals("True", appendValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", appendValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:Append: {appendValue}. Expected 'true' or 'false'"));
                        }
                    }

                    var enumValues = "Random,RandomAndPort,Unknown,UnknownAndPort,Ip,IpAndPort";
                    if (rawTransform.TryGetValue("ForFormat", out var forFormat))
                    {
                        expected++;
                        if (!Enum.TryParse<RequestHeaderForwardedTransform.NodeFormat>(forFormat, ignoreCase: true, out var _))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:ForFormat: {forFormat}. Expected: {enumValues}"));
                        }
                    }

                    if (rawTransform.TryGetValue("ByFormat", out var byFormat))
                    {
                        expected++;
                        if (!Enum.TryParse<RequestHeaderForwardedTransform.NodeFormat>(byFormat, ignoreCase: true, out var _))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:ByFormat: {byFormat}. Expected: {enumValues}"));
                        }
                    }

                    TryCheckTooManyParameters(errors.Add, rawTransform, expected);

                    // for, host, proto, by
                    var tokens = forwardedHeader.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var token in tokens)
                    {
                        if (!string.Equals(token, "By", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'by'"));
                        }
                    }
                }
                else if (rawTransform.TryGetValue("ClientCert", out var clientCertHeader))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("HttpMethod", out var fromHttpMethod))
                {
                    CheckTooManyParameters(rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Set", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for HttpMethod: {string.Join(';', rawTransform.Keys)}. Expected 'Set'"));
                    }
                }
                else
                {
                    errors.Add(new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}"));
                }
            }

            return errors;
        }

        /// <inheritdoc/>
        public HttpTransforms Build(IList<IDictionary<string, string>> rawTransforms)
        {
            var transfomrs = BuildInternal(rawTransforms);
            return AdaptTransforms(transfomrs);
        }

        internal Transforms BuildInternal(IList<IDictionary<string, string>> rawTransforms)
        {
            bool? copyRequestHeaders = null;
            bool? useOriginalHost = null;
            bool? forwardersSet = null;
            var requestTransforms = new List<RequestParametersTransform>();
            var requestHeaderTransforms = new Dictionary<string, RequestHeaderTransform>(StringComparer.OrdinalIgnoreCase);
            var responseHeaderTransforms = new Dictionary<string, ResponseHeaderTransform>(StringComparer.OrdinalIgnoreCase);
            var responseTrailerTransforms = new Dictionary<string, ResponseHeaderTransform>(StringComparer.OrdinalIgnoreCase);

            if (rawTransforms?.Count > 0)
            {
                foreach (var rawTransform in rawTransforms)
                {
                    if (rawTransform.TryGetValue("PathSet", out var pathSet))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        var path = MakePathString(pathSet);
                        requestTransforms.Add(new PathStringTransform(PathStringTransform.PathTransformMode.Set, path));
                    }
                    else if (rawTransform.TryGetValue("PathPrefix", out var pathPrefix))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        var path = MakePathString(pathPrefix);
                        requestTransforms.Add(new PathStringTransform(PathStringTransform.PathTransformMode.Prefix, path));
                    }
                    else if (rawTransform.TryGetValue("PathRemovePrefix", out var pathRemovePrefix))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        var path = MakePathString(pathRemovePrefix);
                        requestTransforms.Add(new PathStringTransform(PathStringTransform.PathTransformMode.RemovePrefix, path));
                    }
                    else if (rawTransform.TryGetValue("PathPattern", out var pathPattern))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        var path = MakePathString(pathPattern);
                        requestTransforms.Add(new PathRouteValuesTransform(path.Value, _binderFactory));
                    }
                    else if (rawTransform.TryGetValue("QueryValueParameter", out var queryValueParameter))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            requestTransforms.Add(new QueryParameterFromStaticTransform(QueryStringTransformMode.Append, queryValueParameter, appendValue));
                        }
                        else if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            requestTransforms.Add(new QueryParameterFromStaticTransform(QueryStringTransformMode.Set, queryValueParameter, setValue));
                        }
                    }
                    else if (rawTransform.TryGetValue("QueryRouteParameter", out var queryRouteParameter))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Append", out var routeValueKeyAppend))
                        {
                            requestTransforms.Add(new QueryParameterRouteTransform(QueryStringTransformMode.Append, queryRouteParameter, routeValueKeyAppend));
                        }
                        else if (rawTransform.TryGetValue("Set", out var routeValueKeySet))
                        {
                            requestTransforms.Add(new QueryParameterRouteTransform(QueryStringTransformMode.Set, queryRouteParameter, routeValueKeySet));
                        }
                        else
                        {
                            throw new NotSupportedException(string.Join(";", rawTransform.Keys));
                        }
                    }
                    else if (rawTransform.TryGetValue("QueryRemoveParameter", out var removeQueryParameter))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        requestTransforms.Add(new QueryParameterRemoveTransform(removeQueryParameter));
                    }
                    else if (rawTransform.TryGetValue("RequestHeadersCopy", out var copyHeaders))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        copyRequestHeaders = string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("RequestHeaderOriginalHost", out var originalHost))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        useOriginalHost = string.Equals("True", originalHost, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("RequestHeader", out var headerName))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            // TODO: What about multiple transforms per header? Last wins? We don't have any examples for needing multiple.
                            requestHeaderTransforms[headerName] = new RequestHeaderValueTransform(setValue, append: false);
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            requestHeaderTransforms[headerName] = new RequestHeaderValueTransform(appendValue, append: true);
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for RequestHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("ResponseHeader", out var responseHeaderName))
                    {
                        var always = false;
                        if (rawTransform.TryGetValue("When", out var whenValue))
                        {
                            CheckTooManyParameters(rawTransform, expected: 3);
                            always = string.Equals("always", whenValue, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            CheckTooManyParameters(rawTransform, expected: 2);
                        }

                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            responseHeaderTransforms[responseHeaderName] = new ResponseHeaderValueTransform(setValue, append: false, always);
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            responseHeaderTransforms[responseHeaderName] = new ResponseHeaderValueTransform(appendValue, append: true, always);
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for ResponseHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("ResponseTrailer", out var responseTrailerName))
                    {
                        var always = false;
                        if (rawTransform.TryGetValue("When", out var whenValue))
                        {
                            CheckTooManyParameters(rawTransform, expected: 3);
                            always = string.Equals("always", whenValue, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            CheckTooManyParameters(rawTransform, expected: 2);
                        }

                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            responseTrailerTransforms[responseTrailerName] = new ResponseHeaderValueTransform(setValue, append: false, always);
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            responseTrailerTransforms[responseTrailerName] = new ResponseHeaderValueTransform(appendValue, append: true, always);
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for ResponseTrailer: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("X-Forwarded", out var xforwardedHeaders))
                    {
                        forwardersSet = true;
                        var expected = 1;

                        var append = true;
                        if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            expected++;
                            append = string.Equals("true", appendValue, StringComparison.OrdinalIgnoreCase);
                        }

                        var prefix = "X-Forwarded-";
                        if (rawTransform.TryGetValue("Prefix", out var prefixValue))
                        {
                            expected++;
                            prefix = prefixValue;
                        }

                        CheckTooManyParameters(rawTransform, expected);

                        // for, host, proto, PathBase
                        var tokens = xforwardedHeaders.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var token in tokens)
                        {
                            if (string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                            {
                                requestHeaderTransforms[prefix + "For"] = new RequestHeaderXForwardedForTransform(append);
                            }
                            else if (string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                requestHeaderTransforms[prefix + "Host"] = new RequestHeaderXForwardedHostTransform(append);
                            }
                            else if (string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase))
                            {
                                requestHeaderTransforms[prefix + "Proto"] = new RequestHeaderXForwardedProtoTransform(append);
                            }
                            else if (string.Equals(token, "PathBase", StringComparison.OrdinalIgnoreCase))
                            {
                                requestHeaderTransforms[prefix + "PathBase"] = new RequestHeaderXForwardedPathBaseTransform(append);
                            }
                            else
                            {
                                throw new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'PathBase'");
                            }
                        }
                    }
                    else if (rawTransform.TryGetValue("Forwarded", out var forwardedHeader))
                    {
                        forwardersSet = true;

                        var useHost = false;
                        var useProto = false;
                        var useFor = false;
                        var useBy = false;
                        var forFormat = RequestHeaderForwardedTransform.NodeFormat.None;
                        var byFormat = RequestHeaderForwardedTransform.NodeFormat.None;

                        // for, host, proto, PathBase
                        var tokens = forwardedHeader.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var token in tokens)
                        {
                            if (string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                            {
                                useFor = true;
                                forFormat = RequestHeaderForwardedTransform.NodeFormat.Random; // RFC Default
                            }
                            else if (string.Equals(token, "By", StringComparison.OrdinalIgnoreCase))
                            {
                                useBy = true;
                                byFormat = RequestHeaderForwardedTransform.NodeFormat.Random; // RFC Default
                            }
                            else if (string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                useHost = true;
                            }
                            else if (string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase))
                            {
                                useProto = true;
                            }
                            else
                            {
                                throw new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'by'");
                            }
                        }

                        var expected = 1;

                        var append = true;
                        if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            expected++;
                            append = string.Equals("true", appendValue, StringComparison.OrdinalIgnoreCase);
                        }

                        if (useFor && rawTransform.TryGetValue("ForFormat", out var forFormatString))
                        {
                            expected++;
                            forFormat = Enum.Parse<RequestHeaderForwardedTransform.NodeFormat>(forFormatString, ignoreCase: true);
                        }

                        if (useBy && rawTransform.TryGetValue("ByFormat", out var byFormatString))
                        {
                            expected++;
                            byFormat = Enum.Parse<RequestHeaderForwardedTransform.NodeFormat>(byFormatString, ignoreCase: true);
                        }

                        CheckTooManyParameters(rawTransform, expected);

                        if (useBy || useFor || useHost || useProto)
                        {
                            requestHeaderTransforms["Forwarded"] = new RequestHeaderForwardedTransform(_randomFactory, forFormat, byFormat, useHost, useProto, append);
                        }
                    }
                    else if (rawTransform.TryGetValue("ClientCert", out var clientCertHeader))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        requestHeaderTransforms[clientCertHeader] = new RequestHeaderClientCertTransform();
                    }
                    else if (rawTransform.TryGetValue("HttpMethod", out var fromHttpMethod))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Set", out var toHttpMethod))
                        {
                            requestTransforms.Add(new HttpMethodTransform(fromHttpMethod, toHttpMethod));
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}");
                    }
                }
            }

            // If there's no transform defined for Host, suppress the host by default
            if (!requestHeaderTransforms.ContainsKey(HeaderNames.Host) && !(useOriginalHost ?? false))
            {
                requestHeaderTransforms[HeaderNames.Host] = new RequestHeaderValueTransform(string.Empty, append: false);
            }

            // Add default forwarders
            if (!forwardersSet.GetValueOrDefault())
            {
                if (!requestHeaderTransforms.ContainsKey(ForwardedHeadersDefaults.XForwardedProtoHeaderName))
                {
                    requestHeaderTransforms[ForwardedHeadersDefaults.XForwardedProtoHeaderName] = new RequestHeaderXForwardedProtoTransform(append: true);
                }
                if (!requestHeaderTransforms.ContainsKey(ForwardedHeadersDefaults.XForwardedHostHeaderName))
                {
                    requestHeaderTransforms[ForwardedHeadersDefaults.XForwardedHostHeaderName] = new RequestHeaderXForwardedHostTransform(append: true);
                }
                if (!requestHeaderTransforms.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName))
                {
                    requestHeaderTransforms[ForwardedHeadersDefaults.XForwardedForHeaderName] = new RequestHeaderXForwardedForTransform(append: true);
                }
                if (!requestHeaderTransforms.ContainsKey("X-Forwarded-PathBase"))
                {
                    requestHeaderTransforms["X-Forwarded-PathBase"] = new RequestHeaderXForwardedPathBaseTransform(append: true);
                }
            }

            return new Transforms(copyRequestHeaders, requestTransforms, requestHeaderTransforms, responseHeaderTransforms, responseTrailerTransforms);
        }

        private HttpTransforms AdaptTransforms(Transforms tranforms)
        {
            return new HttpTransforms()
            {
                // Use the default copy logic only if we don't have any transforms.
                CopyRequestHeaders = tranforms.RequestHeaderTransforms.Count == 0 && (tranforms.CopyRequestHeaders ?? true),
                OnRequest = tranforms.RequestTransforms.Count == 0 && tranforms.RequestHeaderTransforms.Count == 0 ? null
                    : (context, request, destination)
                        => TransformRequestAsync(context, request, destination, tranforms.RequestTransforms, tranforms.RequestHeaderTransforms, tranforms.CopyRequestHeaders ?? true),

                CopyResponseHeaders = tranforms.ResponseHeaderTransforms.Count == 0,
                OnResponse = tranforms.ResponseHeaderTransforms.Count == 0 ? null
                    : (context, response) => TransformResponseHeadersAsync(context, response, tranforms.ResponseHeaderTransforms),

                CopyResponseTrailers = tranforms.ResponseTrailerTransforms.Count == 0,
                OnResponseTrailers = tranforms.ResponseTrailerTransforms.Count == 0 ? null
                    : (context, response) => TransformResponseTralersAsync(context, response, tranforms.ResponseTrailerTransforms)
            };
        }

        private void TryCheckTooManyParameters(Action<Exception> onError, IDictionary<string, string> rawTransform, int expected)
        {
            if (rawTransform.Count > expected)
            {
                onError(new InvalidOperationException("The transform contains more parameters than expected: " + string.Join(';', rawTransform.Keys)));
            }
        }

        private void CheckTooManyParameters(IDictionary<string, string> rawTransform, int expected)
        {
            TryCheckTooManyParameters(ex => throw ex, rawTransform, expected);
        }

        private PathString MakePathString(string path)
        {
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }
            return new PathString(path);
        }

        private Task TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationAddress, IList<RequestParametersTransform> requestTransforms, Dictionary<string, RequestHeaderTransform> requestHeaderTransforms, bool copyRequestHeaders)
        {
            var transformContext = new RequestParametersTransformContext()
            {
                HttpContext = context,
                Request = request,
                Path = context.Request.Path,
                Query = new QueryTransformContext(context.Request),
            };
            foreach (var requestTransform in requestTransforms)
            {
                requestTransform.Apply(transformContext);
            }

            // TODO Perf: We could probably avoid splitting this and just append the final path and query
            UriHelper.FromAbsolute(destinationAddress, out var destinationScheme, out var destinationHost, out var destinationPathBase, out _, out _); // Query and Fragment are not supported here.
            var targetUrl = UriHelper.BuildAbsolute(destinationScheme, destinationHost, destinationPathBase, transformContext.Path, transformContext.Query.QueryString);
            request.RequestUri = new Uri(targetUrl, UriKind.Absolute);

            CopyRequestHeaders(context, request, requestHeaderTransforms, copyRequestHeaders);

            return Task.CompletedTask;
        }

        private static void CopyRequestHeaders(HttpContext context, HttpRequestMessage destination, Dictionary<string, RequestHeaderTransform> transforms, bool copyRequestHeaders)
        {
            // Transforms that were run in the first pass.
            HashSet<string> transformsRun = null;
            if (copyRequestHeaders)
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

                    if (transforms.TryGetValue(headerName, out var transform))
                    {
                        (transformsRun ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(headerName);
                        headerValue = transform.Apply(context, headerValue);
                        if (StringValues.IsNullOrEmpty(headerValue))
                        {
                            continue;
                        }
                    }

                    AddHeader(destination, headerName, headerValue);
                }
            }

            // Run any transforms that weren't run yet.
            foreach (var (headerName, transform) in transforms)
            {
                if (!(transformsRun?.Contains(headerName) ?? false))
                {
                    var headerValue = context.Request.Headers[headerName];
                    headerValue = transform.Apply(context, headerValue);
                    if (!StringValues.IsNullOrEmpty(headerValue))
                    {
                        AddHeader(destination, headerName, headerValue);
                    }
                }
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

        private static Task TransformResponseHeadersAsync(HttpContext context, HttpResponseMessage source, Dictionary<string, ResponseHeaderTransform> transforms)
        {
            HashSet<string> transformsRun = null;
            var responseHeaders = context.Response.Headers;
            CopyHeaders(source, source.Headers, context, responseHeaders, transforms, ref transformsRun);
            if (source.Content != null)
            {
                CopyHeaders(source, source.Content.Headers, context, responseHeaders, transforms, ref transformsRun);
            }
            RunRemainingResponseTransforms(source, context, responseHeaders, transforms, transformsRun);
            return Task.CompletedTask;
        }

        private static Task TransformResponseTralersAsync(HttpContext context, HttpResponseMessage source, Dictionary<string, ResponseHeaderTransform> transforms)
        {
            // Trailers support was already verified by the caller.
            var responseTrailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
            var outgoingTrailers = responseTrailersFeature?.Trailers;
            HashSet<string> transformsRun = null;
            CopyHeaders(source, source.TrailingHeaders, context, outgoingTrailers, transforms, ref transformsRun);
            RunRemainingResponseTransforms(source, context, outgoingTrailers, transforms, transformsRun);
            return Task.CompletedTask;
        }

        private static void CopyHeaders(HttpResponseMessage response, HttpHeaders source, HttpContext context, IHeaderDictionary destination,
            IReadOnlyDictionary<string, ResponseHeaderTransform> transforms, ref HashSet<string> transformsRun)
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
                var headerValue = new StringValues(header.Value.ToArray());

                if (transforms.TryGetValue(headerName, out var transform))
                {
                    (transformsRun ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(headerName);
                    headerValue = transform.Apply(context, response, headerValue);
                }
                if (!StringValues.IsNullOrEmpty(headerValue))
                {
                    destination.Append(headerName, headerValue);
                }
            }
        }

        private static void RunRemainingResponseTransforms(HttpResponseMessage response, HttpContext context, IHeaderDictionary destination,
            IReadOnlyDictionary<string, ResponseHeaderTransform> transforms, HashSet<string> transformsRun)
        {
            // Run any transforms that weren't run yet.
            foreach (var (headerName, transform) in transforms) // TODO: What about multiple transforms per header? Last wins?
            {
                if (!(transformsRun?.Contains(headerName) ?? false))
                {
                    var headerValue = StringValues.Empty;
                    headerValue = transform.Apply(context, response, headerValue);
                    if (!StringValues.IsNullOrEmpty(headerValue))
                    {
                        destination.Append(headerName, headerValue);
                    }
                }
            }
        }
    }
}
