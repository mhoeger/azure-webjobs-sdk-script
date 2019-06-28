// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    public interface IWebHostLanguageWorkerChannelManager
    {
        Task<ILanguageWorkerChannel> InitializeChannelAsync(string language);

        Task<ILanguageWorkerChannel> InitializePlaceholderChannelAsync(string runtime);

        IEnumerable<ILanguageWorkerChannel> GetChannels(string language);

        IEnumerable<ILanguageWorkerChannel> GetPlaceholderChannels(string runtime);

        Task SpecializeAsync();

        bool ShutdownChannelIfExists(string language, string workerId, bool isPlaceholder);

        void ShutdownChannels();
    }
}
