// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    public class WebHostLanguageWorkerChannelManager : IWebHostLanguageWorkerChannelManager
    {
        private readonly ILogger _logger = null;
        private readonly TimeSpan workerInitTimeout = TimeSpan.FromSeconds(30);
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions = null;
        private readonly IScriptEventManager _eventManager = null;
        private readonly IEnvironment _environment;
        private readonly ILoggerFactory _loggerFactory = null;
        private readonly ILanguageWorkerChannelFactory _languageWorkerChannelFactory;
        private string _workerRuntime;
        private Action _shutdownStandbyWorkerChannels;

        private ConcurrentDictionary<WorkerChannelKey, List<ILanguageWorkerChannel>> _workerChannels = new ConcurrentDictionary<WorkerChannelKey, List<ILanguageWorkerChannel>>();
        // Keeps environment config from placeholder mode that must be consistent for placeholders to run correctly
        private Dictionary<string, Dictionary<string, string>> _placeholderEnvironmentConfig = new Dictionary<string, Dictionary<string, string>>();

        public WebHostLanguageWorkerChannelManager(IScriptEventManager eventManager, IEnvironment environment, ILoggerFactory loggerFactory, ILanguageWorkerChannelFactory languageWorkerChannelFactory, IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _eventManager = eventManager;
            _loggerFactory = loggerFactory;
            _languageWorkerChannelFactory = languageWorkerChannelFactory;
            _logger = loggerFactory.CreateLogger<WebHostLanguageWorkerChannelManager>();
            _applicationHostOptions = applicationHostOptions;

            _shutdownStandbyWorkerChannels = ScheduleShutdownStandbyChannels;
            _shutdownStandbyWorkerChannels = _shutdownStandbyWorkerChannels.Debounce(5000);

            // Set up initial checks for environment variables
            _placeholderEnvironmentConfig.Add(LanguageWorkerConstants.NodeLanguageWorkerName, new Dictionary<string, string>
            {
                { LanguageWorkerConstants.FunctionsNodeVersionSetting, _environment.GetEnvironmentVariable(LanguageWorkerConstants.FunctionsNodeVersionSetting) }
            });
        }

        public Task<ILanguageWorkerChannel> InitializeChannelAsync(string runtime)
        {
            _logger?.LogDebug("Initializing language worker channel for runtime:{runtime}", runtime);
            return InitializeLanguageWorkerChannel(runtime, false);
        }

        public Task<ILanguageWorkerChannel> InitializePlaceholderChannelAsync(string runtime)
        {
            _logger?.LogDebug("Initializing placeholder language worker channel for runtime:{runtime}", runtime);
            return InitializeLanguageWorkerChannel(runtime, true);
        }

        private async Task<ILanguageWorkerChannel> InitializeLanguageWorkerChannel(string runtime, bool isPlaceholder)
        {
            ILanguageWorkerChannel languageWorkerChannel = null;
            string workerId = Guid.NewGuid().ToString();
            string scriptRootPath = _applicationHostOptions.CurrentValue.ScriptPath;
            string channelKeyName = new WorkerChannelKey(runtime, isPlaceholder).KeyName;
            _logger.LogDebug("Creating language worker channel for runtime:{runtime}", channelKeyName);
            try
            {
                languageWorkerChannel = _languageWorkerChannelFactory.CreateLanguageWorkerChannel(scriptRootPath, runtime, null, 0, true);
                await languageWorkerChannel.StartWorkerProcessAsync();
                IObservable<RpcWebHostChannelReadyEvent> rpcChannelReadyEvent = _eventManager.OfType<RpcWebHostChannelReadyEvent>()
                                                                        .Where(msg => msg.Language == runtime).Timeout(workerInitTimeout);
                // Wait for response from language worker process
                RpcWebHostChannelReadyEvent readyEvent = await rpcChannelReadyEvent.FirstAsync();
                AddOrUpdateWorkerChannels(readyEvent, isPlaceholder);
            }
            catch (Exception ex)
            {
                throw new HostInitializationException($"Failed to start Language Worker Channel for language :{channelKeyName}", ex);
            }
            return languageWorkerChannel;
        }

        public IEnumerable<ILanguageWorkerChannel> GetChannels(string runtime)
        {
            return GetChannels(new WorkerChannelKey(runtime, false));
        }

        public IEnumerable<ILanguageWorkerChannel> GetPlaceholderChannels(string runtime)
        {
            return GetChannels(new WorkerChannelKey(runtime, true));
        }

        private IEnumerable<ILanguageWorkerChannel> GetChannels(WorkerChannelKey channelKey)
        {
            if (!string.IsNullOrEmpty(channelKey.KeyName) && _workerChannels.TryGetValue(channelKey, out List<ILanguageWorkerChannel> workerChannels))
            {
                return workerChannels;
            }
            return null;
        }

        public async Task SpecializeAsync()
        {
            _logger.LogInformation("Starting language worker channel specialization");
            _workerRuntime = _environment.GetEnvironmentVariable(LanguageWorkerConstants.FunctionWorkerRuntimeSettingName);

            var placeholderWorkerKey = new WorkerChannelKey(_workerRuntime, true);
            if (_workerChannels.ContainsKey(placeholderWorkerKey) && PlaceholderEnvironmentMatchesCurrent(_workerRuntime))
            {
                await SpecializeChannel(_workerRuntime);
            }

            _shutdownStandbyWorkerChannels();
        }

        private async Task SpecializeChannel(string runtime)
        {
            var placeholderWorkerKey = new WorkerChannelKey(_workerRuntime, true);
            // Remove placeholder channel
            _workerChannels.TryRemove(placeholderWorkerKey, out List<ILanguageWorkerChannel> placeholderChannels);

            // Assumes only 1 placeholder language worker channel per runtime
            var languageWorkerChannel = placeholderChannels.FirstOrDefault();
            if (languageWorkerChannel == null)
            {
                return;
            }

            _logger.LogInformation("Loading environment variables for runtime: {runtime}", _workerRuntime);
            await languageWorkerChannel.SendFunctionEnvironmentReloadRequest();

            // Add specialized channel
            var workerKey = new WorkerChannelKey(_workerRuntime, false);
            _workerChannels.AddOrUpdate(workerKey, placeholderChannels, (key, previousValue) => placeholderChannels);
        }

        private bool PlaceholderEnvironmentMatchesCurrent(string workerRuntime)
        {
            if (string.IsNullOrEmpty(workerRuntime))
            {
                return false;
            }

            // Check environment config to see if placeholder worker can be used by the function app
            foreach (string runtime in _placeholderEnvironmentConfig.Keys)
            {
                if (_placeholderEnvironmentConfig.TryGetValue(runtime, out Dictionary<string, string> environmentConfig))
                {
                    foreach (string settingKey in environmentConfig.Keys)
                    {
                        var currentValue = _environment.GetEnvironmentVariable(settingKey);
                        environmentConfig.TryGetValue(settingKey, out string cachedValue);
                        if (!string.Equals(currentValue, cachedValue))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public bool ShutdownChannelIfExists(string runtime, string workerId)
        {
            if (string.IsNullOrEmpty(runtime))
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            var runtimeChannels = _workerChannels.Where(ch => !ch.Key.KeyName.Contains(runtime));
            foreach (var languageWorkerChannels in runtimeChannels)
            {
                var channelList = languageWorkerChannels.Value;
                var channel = channelList.FirstOrDefault(ch => ch.Id == workerId);
                if (channel != null)
                {
                    (channel as IDisposable)?.Dispose();
                    channelList.Remove(channel);
                    return true;
                }
            }
            return false;
        }

        internal void ScheduleShutdownStandbyChannels()
        {
            var channelKey = new WorkerChannelKey(_workerRuntime, true);
            var standbyWorkerChannels = _workerChannels.Where(ch => !ch.Key.KeyName.Contains(LanguageWorkerConstants.FunctionsWorkerPlaceholderPrefix));
            foreach (var runtime in standbyWorkerChannels)
            {
                _logger.LogInformation("Disposing standby channel for runtime:{language}", runtime.Key);

                if (_workerChannels.TryRemove(runtime.Key, out List<ILanguageWorkerChannel> standbyChannels))
                {
                    foreach (var channel in standbyChannels)
                    {
                        if (channel != null)
                        {
                            (channel as IDisposable)?.Dispose();
                        }
                    }
                }
            }
        }

        public void ShutdownChannels()
        {
            foreach (WorkerChannelKey runtime in _workerChannels.Keys)
            {
                _logger.LogInformation("Shutting down language worker channels for runtime:{runtime}", runtime);
                if (_workerChannels.TryRemove(runtime, out List<ILanguageWorkerChannel> standbyChannels))
                {
                    foreach (var channel in standbyChannels)
                    {
                        if (channel != null)
                        {
                            (channel as IDisposable)?.Dispose();
                        }
                    }
                }
            }
        }

        internal void AddOrUpdateWorkerChannels(RpcWebHostChannelReadyEvent rpcChannelReadyEvent, bool isPlaceholder)
        {
            var channelKey = new WorkerChannelKey(rpcChannelReadyEvent.Language, isPlaceholder);
            _logger.LogDebug("Adding webhost language worker channel for runtime: {language}. workerId:{id}", channelKey.KeyName, rpcChannelReadyEvent.LanguageWorkerChannel.Id);

            _workerChannels.AddOrUpdate(channelKey,
                    (key) =>
                    {
                        List<ILanguageWorkerChannel> newLanguageWorkerChannels = new List<ILanguageWorkerChannel>();
                        newLanguageWorkerChannels.Add(rpcChannelReadyEvent.LanguageWorkerChannel);
                        return newLanguageWorkerChannels;
                    },
                    (key, existingLanguageWorkerChannels) =>
                    {
                        existingLanguageWorkerChannels.Add(rpcChannelReadyEvent.LanguageWorkerChannel);
                        return existingLanguageWorkerChannels;
                    });
        }

        private class WorkerChannelKey : IEquatable<WorkerChannelKey>
        {
            public WorkerChannelKey(string runtime, bool isPlaceholder)
            {
                if (string.IsNullOrEmpty(runtime))
                {
                    KeyName = string.Empty;
                }
                else if (isPlaceholder)
                {
                    KeyName = $"{LanguageWorkerConstants.FunctionsWorkerPlaceholderPrefix}{runtime}";
                }
                else
                {
                    KeyName = runtime;
                }
            }

            public string KeyName { get; }

            public bool Equals(WorkerChannelKey other)
            {
                return KeyName.Equals(other.KeyName);
            }

            public bool Equals(WorkerChannelKey other, StringComparison stringComparison)
            {
                return KeyName.Equals(other.KeyName, stringComparison);
            }

            public override int GetHashCode()
            {
                return KeyName.GetHashCode();
            }

            public override string ToString()
            {
                return KeyName;
            }
        }
    }
}
