// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.Azure.WebJobs.Script.WebHost.Helpers;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Extensions
{
    public static class ZipArchiveExtensions
    {
        public static void AddDirectory(this ZipArchive zipArchive, string directoryPath, string directoryNameInArchive = "")
        {
            var directoryInfo = FileSystemHelpers.DirectoryInfoFromDirectoryName(directoryPath);
            zipArchive.AddDirectory(directoryInfo, directoryNameInArchive);
        }

        public static void AddDirectory(this ZipArchive zipArchive, DirectoryInfo directory, string directoryNameInArchive, out IList<ZipArchiveEntry> files)
        {
            files = new List<ZipArchiveEntry>();
            InternalAddDirectory(zipArchive, directory, directoryNameInArchive, files);
        }

        public static void AddDirectory(this ZipArchive zipArchive, DirectoryInfo directory, string directoryNameInArchive)
        {
            InternalAddDirectory(zipArchive, directory, directoryNameInArchive);
        }

        private static void InternalAddDirectory(ZipArchive zipArchive, DirectoryInfo directory, string directoryNameInArchive, IList<ZipArchiveEntry> files = null)
        {
            bool any = false;
            foreach (var info in directory.GetFileSystemInfos())
            {
                any = true;
                if (info is DirectoryInfo subDirectoryInfo)
                {
                    string childName = ForwardSlashCombine(directoryNameInArchive, subDirectoryInfo.Name);
                    InternalAddDirectory(zipArchive, subDirectoryInfo, childName, files);
                }
                else
                {
                    var entry = zipArchive.AddFile((FileInfo)info, directoryNameInArchive);
                    files?.Add(entry);
                }
            }

            if (!any)
            {
                // If the directory did not have any files or folders, add a entry for it
                zipArchive.CreateEntry(EnsureTrailingSlash(directoryNameInArchive));
            }
        }

        private static string ForwardSlashCombine(string part1, string part2)
        {
            return Path.Combine(part1, part2).Replace('\\', '/');
        }

        public static ZipArchiveEntry AddFile(this ZipArchive zipArchive, string filePath, string directoryNameInArchive = "")
        {
            var fileInfo = FileSystemHelpers.FileInfoFromFileName(filePath);
            return zipArchive.AddFile(fileInfo, directoryNameInArchive);
        }

        public static ZipArchiveEntry AddFile(this ZipArchive zipArchive, FileInfo file, string directoryNameInArchive)
        {
            Stream fileStream = null;
            try
            {
                fileStream = file.OpenRead();
            }
            catch (Exception ex)
            {
                // tolerate if file in use.
                // for simplicity, any exception.
                // TODO: log ex
                return null;
            }

            try
            {
                string fileName = ForwardSlashCombine(directoryNameInArchive, file.Name);
                ZipArchiveEntry entry = zipArchive.CreateEntry(fileName, CompressionLevel.Fastest);
                entry.LastWriteTime = file.LastWriteTime;

                using (Stream zipStream = entry.Open())
                {
                    fileStream.CopyTo(zipStream);
                }
                return entry;
            }
            finally
            {
                fileStream.Dispose();
            }
        }

        public static ZipArchiveEntry AddFileContent(this ZipArchive zip, string fileName, string fileContent)
        {
            ZipArchiveEntry entry = zip.CreateEntry(fileName, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(fileContent);
            }
            return entry;
        }

        public static void Extract(this ZipArchive archive, string directoryName)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = Path.Combine(directoryName, entry.FullName);
                if (entry.Length == 0 && (path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal)))
                {
                    // Extract directory
                    Directory.CreateDirectory(path);
                }
                else
                {
                    var fileInfo = FileSystemHelpers.FileInfoFromFileName(path);

                    if (!fileInfo.Directory.Exists)
                    {
                        fileInfo.Directory.Create();
                    }

                    using (Stream zipStream = entry.Open(),
                                  fileStream = fileInfo.Open(FileMode.Create, FileAccess.Write))
                    {
                        zipStream.CopyTo(fileStream);
                    }

                    fileInfo.LastWriteTimeUtc = entry.LastWriteTime.ToUniversalTime().DateTime;
                }
            }
        }

        private static string EnsureTrailingSlash(string input)
        {
            return input.EndsWith("/", StringComparison.Ordinal) ? input : input + "/";
        }
    }
}