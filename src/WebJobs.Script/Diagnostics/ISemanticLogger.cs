// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Microsoft.Azure.WebJobs.Script.Diagnostics
{
    public interface ISemanticLogger
    {
        void FunctionInvocationStart(string functionName);

        void HostConfigApplied();

        void HostConfigCreationFailed();

        void HostConfigCreationSkipped();

        void HostConfigIsEmpty();

        void NoHostConfigFound();

        void ReadHostConfig(string sanitizedJson);

        void ReadingHostConfig(string hostFilePath);
    }
}