﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;
using Microsoft.Azure.WebJobs.Script.WebHost.Security.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
                // TODO: replace this string scriptPath = _options.CurrentValue.ScriptPath;
                string scriptPath = "/home/site/wwwroot";
                // string scriptPath = _options.CurrentValue.ScriptPath;
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

                var t = await scriptInvocationContext.ResultSource.Task;
                return new OkObjectResult(t.Return);
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
                var function = FunctionMetadataProvider.ReadFunctionMetadata(scriptDir, workerConfigs, functionErrors);
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
