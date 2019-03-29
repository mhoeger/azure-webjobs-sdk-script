// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    public partial class WebHostSemanticLogger : ISemanticLogger
    {
        private readonly ILoggerFactory _loggerFactory;
        private ConcurrentDictionary<string, ILogger> _loggerDictionary;

        public WebHostSemanticLogger(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _loggerDictionary = new ConcurrentDictionary<string, ILogger>();
        }

        private ILogger GetLogger(string category)
        {
            return _loggerDictionary.GetOrAdd(category, _loggerFactory.CreateLogger);
        }
    }

    /// <summary>
    /// Function invocation logs
    /// Event Id: 1000's
    /// </summary>
    public partial class WebHostSemanticLogger
    {
        public void FunctionInvocationStart(string functionName)
        {
            GetLogger(LogCategories.CreateFunctionCategory(functionName)).LogInformation(FunctionInvocation.StartExecution);
        }

        private class FunctionInvocation
        {
            public static readonly EventId StartExecution = new EventId(1000, "Function started execution");
            public static readonly EventId CompleteSuccessfulExecution = new EventId(1001, "Function completed execution");
            public static readonly EventId CompleteFailedExecution = new EventId(1002, "Function failed execution");
        }
    }

    /// <summary>
    /// Host configuration logs
    /// Event Id: 2000's
    /// </summary>
    public partial class WebHostSemanticLogger
    {
        public void HostConfigApplied()
        {
            GetLogger(LogCategories.Startup).LogDebug(HostJsonConfiguration.Applied);
        }

        public void ReadingHostConfig(string hostFilePath)
        {
            var readingEvent = HostJsonConfiguration.ReadingFile;
            GetLogger(LogCategories.Startup).LogInformation(readingEvent, readingEvent.Format(hostFilePath));
        }

        public void ReadHostConfig(string sanitizedJson)
        {
            var readEvent = HostJsonConfiguration.ReadFile;
            GetLogger(LogCategories.Startup).LogInformation(readEvent, readEvent.Format(Environment.NewLine, sanitizedJson));
        }

        public void HostConfigIsEmpty()
        {
            GetLogger(LogCategories.Startup).LogInformation(HostJsonConfiguration.EmptyConfigFound);
        }

        public void NoHostConfigFound()
        {
            GetLogger(LogCategories.Startup).LogInformation(HostJsonConfiguration.NoConfigFound);
        }

        public void HostConfigCreationFailed()
        {
            GetLogger(LogCategories.Startup).LogInformation(HostJsonConfiguration.ConfigCreationFailed);
        }

        public void HostConfigCreationSkipped()
        {
            GetLogger(LogCategories.Startup).LogInformation(HostJsonConfiguration.ConfigCreationSkipped);
        }

        private class HostJsonConfiguration
        {
            public static readonly EventId Applied = new EventId(2001, "Host configuration applied.");
            public static readonly EventId ReadingFile = new EventId(2002, "Reading host configuration file '{0}'");
            public static readonly EventId ReadFile = new EventId(2003, "Host configuration file read:{0}{1}");
            public static readonly EventId EmptyConfigFound = new EventId(2004, $"Empty host configuration file found. Creating a default {ScriptConstants.HostMetadataFileName} file.");
            public static readonly EventId NoConfigFound = new EventId(2005, $"No host configuration file found. Creating a default {ScriptConstants.HostMetadataFileName} file.");
            public static readonly EventId ConfigCreationFailed = new EventId(2006, $"Failed to create {ScriptConstants.HostMetadataFileName} file. Host execution will continue.");
            public static readonly EventId ConfigCreationSkipped = new EventId(2007, $"File system is read-only. Skipping {ScriptConstants.HostMetadataFileName} creation.");
        }
    }
}
