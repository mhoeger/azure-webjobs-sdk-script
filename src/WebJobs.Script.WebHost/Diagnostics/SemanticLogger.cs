// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics
{
    public class SemanticLogger
    {
        private readonly ILogger _functionCategoryLogger;

        public SemanticLogger(ILoggerFactory loggerFactory)
        {
            _functionCategoryLogger = loggerFactory.CreateLogger(ScriptConstants.LogCategoryFunction);
            // _logger
        }

        public void FunctionHasStarted()
        {
            _functionCategoryLogger.LogInformation(FunctionInvocation.FunctionStarted, FunctionInvocation.FunctionStarted.Name);
        }

        public class FunctionInvocation
        {
            public static readonly EventId FunctionStarted = new EventId(1000, "Function started execution");
        }
    }
}
