// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Script.WebHost.Extensions;
using Microsoft.Azure.WebJobs.Script.WebHost.Helpers;
using Microsoft.Extensions.Primitives;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management
{
    public class ZipFileSystem : VirtualFileSystemBase
    {
        public ZipFileSystem(WebHostSettings settings) : base(settings)
        {
        }

        protected override Task<HttpResponseMessage> CreateDirectoryGetResponse(HttpRequest request, DirectoryInfo info, string localFilePath)
        {
            var response = CreateResponse(HttpStatusCode.OK);

            // If there is a queryString called fileName, use that as the file name
            request.Query.TryGetValue("fileName", out StringValues fileNames);

            // otherwise, use the directory's name.
            var fileName = fileNames.Count > 0 ? fileNames[0] : Path.GetFileName(Path.GetDirectoryName(localFilePath)) + ".zip";
            response.Content = ZipStreamContent.Create(fileName, zip =>
            {
                foreach (FileSystemInfo fileSysInfo in info.GetFileSystemInfos())
                {
                    if (fileSysInfo is DirectoryInfo directoryInfo)
                    {
                        zip.AddDirectory(directoryInfo, fileSysInfo.Name);
                    }
                    else
                    {
                        // Add it at the root of the zip
                        zip.AddFile(fileSysInfo.FullName, string.Empty);
                    }
                }
            });
            return Task.FromResult(response);
        }

        protected override Task<HttpResponseMessage> CreateItemGetResponse(HttpRequest reques, FileSystemInfo info, string localFilePath)
        {
            // We don't support getting a file from the zip controller
            // Conceivably, it could be a zip file containing just the one file, but that's rarely interesting
            var notFoundResponse = CreateResponse(HttpStatusCode.NotFound);
            return Task.FromResult(notFoundResponse);
        }

        protected override Task<HttpResponseMessage> CreateDirectoryPutResponse(HttpRequest request, DirectoryInfo info, string localFilePath)
        {
            // The unzipping is done over the existing folder, without first removing existing files.
            // Hence it's more of a PATCH than a PUT. We should consider supporting both with the right semantic.
            // Though a true PUT at the root would be scary as it would wipe all existing files!
            var zipArchive = new ZipArchive(request.Body, ZipArchiveMode.Read);
            zipArchive.Extract(localFilePath);

            return Task.FromResult(CreateResponse(HttpStatusCode.OK));
        }

        protected override Task<HttpResponseMessage> CreateItemPutResponse(HttpRequest reques, FileSystemInfo info, string localFilePath, bool itemExists)
        {
            // We don't support putting an individual file using the zip controller
            var notFoundResponse = CreateResponse(HttpStatusCode.NotFound);
            return Task.FromResult(notFoundResponse);
        }
    }
}