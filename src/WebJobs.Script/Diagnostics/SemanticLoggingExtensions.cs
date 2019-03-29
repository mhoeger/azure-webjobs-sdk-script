// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    internal static class SemanticLoggingExtensions
    {
        public static string Format(this EventId eventId, string arg0)
        {
            return string.Format(CultureInfo.InvariantCulture, eventId.Name, arg0);
        }

        public static string Format(this EventId eventId, string arg0, string arg1)
        {
            return string.Format(CultureInfo.InvariantCulture, eventId.Name, arg0, arg1);
        }

        public static void LogInformation(this ILogger logger, EventId eventId)
        {
            logger.LogInformation(eventId, eventId.Name);
        }

        public static void LogDebug(this ILogger logger, EventId eventId)
        {
            logger.LogDebug(eventId, eventId.Name);
        }
    }
}
