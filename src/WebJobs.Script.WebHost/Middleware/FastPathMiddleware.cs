// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;
using Microsoft.Azure.WebJobs.Script.WebHost.Security.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Middleware
{
    public class FastPathMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IScriptWebHostEnvironment _webHostEnvironment;
        private readonly IStandbyManager _standbyManager;
        private readonly IEnvironment _environment;
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _options;
        private readonly IEnumerable<WorkerConfig> _workerConfigs;
        private RequestDelegate _invoke;
        private ILoggerFactory _loggerFactory;
        private IEnumerable<FunctionMetadata> _functions;
        private bool _useFastPath = false;
        private bool _functionsLoaded = false;

        private IWebHostLanguageWorkerChannelManager _webHostlanguageWorkerChannelManager;

        public FastPathMiddleware(RequestDelegate next, IScriptWebHostEnvironment webHostEnvironment, IOptions<LanguageWorkerOptions> workerConfigOptions,
            IStandbyManager standbyManager, IEnvironment environment, IWebHostLanguageWorkerChannelManager webHostLanguageWorkerChannelManager, IOptionsMonitor<ScriptApplicationHostOptions> options, ILoggerFactory loggerFactory)
        {
            _next = next;
            _invoke = InvokeFastPath;
            _webHostEnvironment = webHostEnvironment;
            _standbyManager = standbyManager;
            _environment = environment;
            _webHostlanguageWorkerChannelManager = webHostLanguageWorkerChannelManager;
            _options = options;
            _workerConfigs = workerConfigOptions.Value.WorkerConfigs;
            _loggerFactory = loggerFactory;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            if (ShouldUseFastPath(httpContext))
            {
                IActionResult result = await InvokeFastPath(httpContext); // GetResultAsync(context, functionExecution);
                if (result != null)
                {
                    ActionContext actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
                    await result.ExecuteResultAsync(actionContext);
                    return;
                }
            }

            await _next(httpContext);
        }

        private bool ShouldUseFastPath(HttpContext httpContext)
        {
            // TODO: also discount proxies - currently assumes never proxies
            // || !_environment.FileSystemIsReadOnly()
            if (_webHostEnvironment.InStandbyMode)
            {
                return false;
            }

            if (_functions == null)
            {
                // string scriptPath = "/home/site/wwwroot";
                string scriptPath = _options.CurrentValue.ScriptPath;
                if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development"))
                {
                    scriptPath = "D:\\mhoeger\\test\\one-js-func";
                }
                _functions = ReadFunctionsMetadata(scriptPath, null, _workerConfigs);

                // assumes that we are not in placeholder mode
                var invalid = _functions.Any(f => f.IsProxy || f.Bindings.Any(binding => binding.Type != "httpTrigger" && binding.Type != "http"));

                _useFastPath = !invalid;
            }

            return _useFastPath;
        }

        private async Task<IActionResult> InvokeFastPath(HttpContext httpContext)
        {
            ILanguageWorkerChannel channel = await _webHostlanguageWorkerChannelManager.GetChannels("node").FirstOrDefault().Value.Task;

            // TODO: should handle this properly
            if (channel == null)
            {
                return null;
            }

            if (!_functionsLoaded)
            {
                await channel.SendFunctionLoadRequests(_functions);
                _functionsLoaded = true;
            }

            var func = GetMatchingFunction(_functions, httpContext.Request.Path.Value);

            if (func == null)
            {
                return null;
            }

            var triggerBinding = func.InputBindings.SingleOrDefault(p => p.IsTrigger);
            triggerBinding.Raw.TryGetValue("authLevel", StringComparison.OrdinalIgnoreCase, out JToken auth);
            Enum.TryParse(auth.ToString(), true, out AuthorizationLevel authLevel);

            bool authorized = await AuthenticateAndAuthorizeAsync(httpContext, authLevel);
            if (!authorized)
            {
                return new UnauthorizedResult();
            }
            if (func.IsDisabled &&
                !AuthUtility.PrincipalHasAuthLevelClaim(httpContext.User, AuthorizationLevel.Admin))
            {
                return new NotFoundResult();
            }

            // TODO: figure out if this function is disabled or not
            // IFunctionExecutionFeature executionFeature = httpContext.Features.Get<IFunctionExecutionFeature>();
            var canExecute = true; // executionFeature.CanExecute
            if (canExecute)
            {
                ScriptInvocationContext scriptInvocationContext = new ScriptInvocationContext()
                {
                    FunctionMetadata = func,
                    ResultSource = new TaskCompletionSource<ScriptInvocationResult>(),
                    AsyncExecutionContext = System.Threading.ExecutionContext.Capture(),

                    // TODO: link up cancellation token to parameter descriptors
                    CancellationToken = CancellationToken.None,
                    Logger = _loggerFactory.CreateLogger(func.Name)
                };

                await channel.SendInvocationRequest(scriptInvocationContext);

                var result = await scriptInvocationContext.ResultSource.Task;

                if (result.Return is IDictionary returnJson)
                {
                    // Assume only one output
                    foreach (var value in returnJson.Values)
                    {
                        return CreateResult(httpContext.Request, value);
                    }
                }

                return CreateResult(httpContext.Request, result.Return);
            }

            return new OkResult();
        }

        internal FunctionMetadata GetMatchingFunction(IEnumerable<FunctionMetadata> functions, string route)
        {
            var dict = new RouteValueDictionary();
            foreach (FunctionMetadata f in functions)
            {
                if (RouteMatcher.TryMatch("/api/" + f.Name, route, out dict))
                {
                    return f;
                }
            }
            return null;
        }

        internal static Collection<FunctionMetadata> ReadFunctionsMetadata(string rootScriptPath, ICollection<string> functionsWhiteList, IEnumerable<WorkerConfig> workerConfigs,
Dictionary<string, ICollection<string>> functionErrors = null)
        {
            IEnumerable<string> functionDirectories = Directory.EnumerateDirectories(rootScriptPath);

            var functions = new Collection<FunctionMetadata>();

            foreach (var scriptDir in functionDirectories)
            {
                var function = FunctionMetadataManager.ReadFunctionMetadata(scriptDir, functionsWhiteList, workerConfigs, functionErrors);
                if (function != null)
                {
                    functions.Add(function);
                }
            }
            return functions;
        }

        private async Task<bool> AuthenticateAndAuthorizeAsync(HttpContext context, AuthorizationLevel authLevel)
        {
            var policyEvaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
            AuthorizationPolicy policy = AuthUtility.CreateFunctionPolicy();
            // Authenticate the request
            var authenticateResult = await policyEvaluator.AuthenticateAsync(policy, context);

            // Authorize using the function policy and resource
            var authorizeResult = await policyEvaluator.AuthorizeAsync(policy, authenticateResult, context, authLevel);

            return authorizeResult.Succeeded;
        }

        // TODO: this is completely copy pasted from HTTP bining, need a better way to share this code
        internal static IActionResult CreateResult(HttpRequest request, object content)
        {
            string stringContent = content as string;
            if (stringContent != null)
            {
                try
                {
                    // attempt to read the content as JObject/JArray
                    content = JsonConvert.DeserializeObject(stringContent);
                }
                catch (JsonException)
                {
                    // not a json response
                }
            }

            // see if the content is a response object, defining http response properties
            IDictionary<string, object> responseObject = null;
            if (content is JObject)
            {
                // TODO: FACAVAL - The call bellow is pretty fragile. This would cause issues
                // if we invoke this with a JObject. Maintaining this to retain the original implementation
                // but this should be revisited.
                responseObject = JsonConvert.DeserializeObject<ExpandoObject>(content.ToString());
            }
            else
            {
                // Handle ExpandoObjects
                responseObject = content as ExpandoObject;
            }

            int statusCode = StatusCodes.Status200OK;
            IDictionary<string, object> responseHeaders = null;
            bool enableContentNegotiation = false;
            List<Tuple<string, string, CookieOptions>> cookies = new List<Tuple<string, string, CookieOptions>>();
            if (responseObject != null)
            {
                ParseResponseObject(responseObject, ref content, out responseHeaders, out statusCode, out cookies, out enableContentNegotiation);
            }

            return CreateResult(request, statusCode, content, responseHeaders, cookies, enableContentNegotiation);
        }

        internal static void ParseResponseObject(IDictionary<string, object> responseObject, ref object content, out IDictionary<string, object> headers, out int statusCode, out List<Tuple<string, string, CookieOptions>> cookies, out bool enableContentNegotiation)
        {
            headers = null;
            cookies = null;
            statusCode = StatusCodes.Status200OK;
            enableContentNegotiation = false;

            // TODO: Improve this logic
            // Sniff the object to see if it looks like a response object
            // by convention
            object bodyValue = null;
            if (responseObject.TryGetValue(LanguageWorkerConstants.RpcHttpBody, out bodyValue, ignoreCase: true))
            {
                // the response content becomes the specified body value
                content = bodyValue;

                if (responseObject.TryGetValue(LanguageWorkerConstants.RpcHttpHeaders, out IDictionary<string, object> headersValue, ignoreCase: true))
                {
                    headers = headersValue;
                }

                if (TryParseStatusCode(responseObject, out int? responseStatusCode))
                {
                    statusCode = responseStatusCode.Value;
                }

                if (responseObject.TryGetValue<bool>(LanguageWorkerConstants.RpcHttpEnableContentNegotiation, out bool enableContentNegotiationValue, ignoreCase: true))
                {
                    enableContentNegotiation = enableContentNegotiationValue;
                }

                if (responseObject.TryGetValue(LanguageWorkerConstants.RpcHttpCookies, out List<Tuple<string, string, CookieOptions>> cookiesValue, ignoreCase: true))
                {
                    cookies = cookiesValue;
                }
            }
        }

        internal static bool TryParseStatusCode(IDictionary<string, object> responseObject, out int? statusCode)
        {
            statusCode = StatusCodes.Status200OK;

            if (!responseObject.TryGetValue(LanguageWorkerConstants.RpcHttpStatusCode, out object statusValue, ignoreCase: true) &&
                !responseObject.TryGetValue(LanguageWorkerConstants.RpcHttpStatus, out statusValue, ignoreCase: true))
            {
                return false;
            }

            if (statusValue is HttpStatusCode ||
                statusValue is int)
            {
                statusCode = (int)statusValue;
                return true;
            }

            if (statusValue is uint ||
                statusValue is short ||
                statusValue is ushort ||
                statusValue is long ||
                statusValue is ulong)
            {
                statusCode = Convert.ToInt32(statusValue);
                return true;
            }

            var stringValue = statusValue as string;
            int parsedStatusCode;
            if (stringValue != null && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedStatusCode))
            {
                statusCode = parsedStatusCode;
                return true;
            }

            return false;
        }

        private static IActionResult CreateResult(HttpRequest request, int statusCode, object content, IDictionary<string, object> headers, List<Tuple<string, string, CookieOptions>> cookies, bool enableContentNegotiation)
        {
            if (enableContentNegotiation)
            {
                // We only write the response through one of the formatters if
                // the function has indicated that it wants to enable content negotiation
                return new ScriptObjectResult(content, headers) { StatusCode = statusCode };
            }
            else
            {
                return new RawScriptResult(statusCode, content)
                {
                    Headers = headers,
                    Cookies = cookies
                };
            }
        }

        internal class RouteMatcher
        {
            public static bool TryMatch(string routeTemplate, string requestPath, out RouteValueDictionary values)
            {
                values = new RouteValueDictionary();
                var template = TemplateParser.Parse(routeTemplate);
                var defaults = GetDefaults(template);
                var matcher = new TemplateMatcher(template, defaults);
                return matcher.TryMatch(requestPath, values);
            }

            // This method extracts the default argument values from the template.
            private static RouteValueDictionary GetDefaults(RouteTemplate parsedTemplate)
            {
                var result = new RouteValueDictionary();

                foreach (var parameter in parsedTemplate.Parameters)
                {
                    if (parameter.DefaultValue != null)
                    {
                        result.Add(parameter.Name, parameter.DefaultValue);
                    }
                }

                return result;
            }
        }
    }
}
