// -----------------------------------------------------------------------
//  <copyright file="RavenDB_2889.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.IO;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Database.Smuggler;
using Raven.Abstractions.Database.Smuggler.FileSystem;
using Raven.Abstractions.FileSystem;
using Raven.Database.Extensions;
using Raven.Json.Linq;
using Raven.Smuggler;
using Raven.Smuggler.FileSystem;
using Raven.Smuggler.FileSystem.Files;
using Raven.Smuggler.FileSystem.Remote;

using Xunit;

namespace Raven.Tests.FileSystem.Issues
{
    public class RavenDB_2889 : RavenFilesTestWithLogs
    {
        [Fact]
        public async Task SmugglerCanStripReplicationInformationDuringImport()
        {
            using (var store = NewStore())
            {
                var server = GetServer();
                var exportFile = Path.Combine(server.Configuration.Core.DataDirectory, "Export");

                await store.AsyncFilesCommands.Admin.EnsureFileSystemExistsAsync("N1");
                await store.AsyncFilesCommands.Admin.EnsureFileSystemExistsAsync("N2");

                var content = new MemoryStream(new byte[] { 1, 2, 3 })
                {
                    Position = 0
                };
                var commands = store.AsyncFilesCommands.ForFileSystem("N1");
                await commands.UploadAsync("test.bin", content, new RavenJObject { { "test", "value" } });
                var metadata = await commands.GetMetadataForAsync("test.bin");
                var source1 = metadata[SynchronizationConstants.RavenSynchronizationSource];
                var version1 = metadata[SynchronizationConstants.RavenSynchronizationVersion];
                var history1 = metadata[SynchronizationConstants.RavenSynchronizationHistory] as RavenJArray;
                Assert.NotNull(source1);
                Assert.NotNull(version1);
                Assert.NotNull(history1);
                Assert.Empty(history1);

                var smuggler = new FileSystemSmuggler(new FileSystemSmugglerOptions() { StripReplicationInformation = true });

                await smuggler.ExecuteAsync(
                    new RemoteSmugglingSource(
                        new FilesConnectionStringOptions
                        {
                            Url = store.Url,
                            DefaultFileSystem = "N1"
                        }),
                    new FileSmugglingDestination(exportFile, false));

                await smuggler.ExecuteAsync(
                    new FileSmugglingSource(exportFile),
                    new RemoteSmugglingDestination(
                        new FilesConnectionStringOptions
                        {
                            Url = store.Url,
                            DefaultFileSystem = "N2"
                        }));

                commands = store.AsyncFilesCommands.ForFileSystem("N2");
                metadata = await commands.GetMetadataForAsync("test.bin");
                var source2 = metadata[SynchronizationConstants.RavenSynchronizationSource];
                var version2 = metadata[SynchronizationConstants.RavenSynchronizationVersion];
                var history2 = metadata[SynchronizationConstants.RavenSynchronizationHistory] as RavenJArray;
                Assert.NotEqual(source1, source2);
                Assert.Equal(version1, version2);
                Assert.NotNull(history2);
                Assert.Empty(history2);
            }
        }
    }
}
