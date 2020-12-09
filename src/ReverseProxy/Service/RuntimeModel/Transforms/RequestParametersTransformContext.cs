// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace Microsoft.ReverseProxy.Service.RuntimeModel.Transforms
{
    /// <summary>
    /// Transform state for use with <see cref="RequestParametersTransform"/>
    /// </summary>
    public class RequestParametersTransformContext
    {
        /// <summary>
        /// The current request context.
        /// </summary>
        public HttpContext HttpContext { get; set; }

        public HttpRequestMessage Request { get; internal set; }

        /// <summary>
        /// The path to use for the proxy request.
        /// </summary>
        /// <remarks>
        /// This will be prefixed by any PathBase specified for the destination server.
        /// </remarks>
        public PathString Path { get; set; }

        /// <summary>
        /// The query used for the proxy request.
        /// </summary>
        public QueryTransformContext Query { get; set; }
    }
}
