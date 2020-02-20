﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Xunit;
using IApplicationLifetime = Microsoft.AspNetCore.Hosting.IApplicationLifetime;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class WorkerFunctionInvokerTests
    {
        private readonly TestWorkerFunctionInvoker _testFunctionInvoker;
        private readonly Mock<IApplicationLifetime> _applicationLifetime;
        private readonly Mock<IFunctionInvocationDispatcher> _mockFunctionInvocationDispatcher;

        public WorkerFunctionInvokerTests()
        {
            _applicationLifetime = new Mock<IApplicationLifetime>();
            _mockFunctionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>();
            _mockFunctionInvocationDispatcher.Setup(a => a.ErrorEventsThreshold).Returns(0);

            var hostBuilder = new HostBuilder()
                .ConfigureDefaultTestWebScriptHost(o =>
                {
                    o.ScriptPath = TestHelpers.FunctionsTestDirectory;
                    o.LogPath = TestHelpers.GetHostLogFileDirectory().Parent.FullName;
                });
            var host = hostBuilder.Build();

            var sc = host.GetScriptHost();

            FunctionMetadata metaData = new FunctionMetadata();
            _testFunctionInvoker = new TestWorkerFunctionInvoker(sc, null, metaData, NullLoggerFactory.Instance, null, new Collection<FunctionBinding>(), _mockFunctionInvocationDispatcher.Object, _applicationLifetime.Object);
        }

        [Fact]
        public async Task InvokeTimeout_CallsShutdown()
        {
            try
            {
                _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Initializing);
                await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), Task.Delay(TimeSpan.FromSeconds(500)));
            }
            catch (Exception)
            {
            }
            _applicationLifetime.Verify(a => a.StopApplication(), Times.Once);
        }

        [Fact]
        public async Task FunctionDispatcher_Restarting_DelaysInvoke()
        {
            _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.WorkerProcessRestarting);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var result = await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), timeoutTask);
            Assert.Equal(timeoutTask, result);

            _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Initialized);
            var invokeCoreTask = _testFunctionInvoker.InvokeCore(new object[] { }, null);
            result = await Task.WhenAny(invokeCoreTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Equal(invokeCoreTask, result);
        }

        [Fact]
        public async Task FunctionDispatcher_DelaysInvoke_Restarting_And_Disposing()
        {
            _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.WorkerProcessRestarting);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var result = await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), timeoutTask);
            Assert.Equal(timeoutTask, result);

            _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Disposing);
            var timeoutTask2 = Task.Delay(TimeSpan.FromSeconds(5));
            result = await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), timeoutTask2);
            Assert.Equal(timeoutTask2, result);
        }

        [Fact]
        public async Task InvokeInitialized_DoesNotCallShutdown()
        {
            try
            {
                _mockFunctionInvocationDispatcher.Setup(a => a.State).Returns(FunctionInvocationDispatcherState.Initialized);
                await Task.WhenAny(_testFunctionInvoker.InvokeCore(new object[] { }, null), Task.Delay(TimeSpan.FromSeconds(125)));
            }
            catch (Exception)
            {
            }
            _applicationLifetime.Verify(a => a.StopApplication(), Times.Never);
        }
    }
}
