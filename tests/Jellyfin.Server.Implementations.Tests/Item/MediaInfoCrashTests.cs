using System;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class MediaInfoCrashTests
{
    private const string WorkerPath = "JELLYFIN_TEST_MEDIAINFO_CRASH_PATH";
    private const string WorkerStage = "JELLYFIN_TEST_MEDIAINFO_CRASH_STAGE";

    [Theory]
    [InlineData("MediaStreamInfos")]
    [InlineData("AttachmentStreamInfos")]
    [InlineData("Chapters")]
    [InlineData("commit")]
    public async Task KilledWriter_ReopensWithEntirePreviousSnapshot(string stage)
    {
        var root = Directory.CreateTempSubdirectory("jellyfin-crash-test-");
        var path = Path.Combine(root.FullName, "library.db");
        try
        {
            var id = Guid.NewGuid();
            await using (var context = Open(path))
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Movie).FullName!, Path = "/generated/movie.mkv", RunTimeTicks = 1 });
                context.MediaStreamInfos.Add(new MediaStreamInfo { Item = null!, ItemId = id, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, Codec = "old" });
                context.AttachmentStreamInfos.Add(new AttachmentStreamInfo { Item = null!, ItemId = id, Index = 0, Filename = "old" });
                context.Chapters.Add(new Chapter { Item = null!, ItemId = id, ChapterIndex = 0, StartPositionTicks = 0, Name = "old" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, OperatingSystem.IsWindows() ? ".exe" : null);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add(typeof(MediaInfoCrashTests).FullName + ".CrashWorker");
            start.Environment[WorkerPath] = path;
            start.Environment[WorkerStage] = stage;
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }

            Assert.True(File.Exists(path + ".killed"), await stdout + await stderr);
            Assert.NotEqual(0, child.ExitCode);
            await using var reopened = Open(path);
            Assert.Equal(1, (await reopened.BaseItems.SingleAsync(e => e.Id.Equals(id), TestContext.Current.CancellationToken)).RunTimeTicks);
            Assert.Equal("old", (await reopened.MediaStreamInfos.SingleAsync(TestContext.Current.CancellationToken)).Codec);
            Assert.Equal("old", (await reopened.AttachmentStreamInfos.SingleAsync(TestContext.Current.CancellationToken)).Filename);
            Assert.Equal("old", (await reopened.Chapters.SingleAsync(TestContext.Current.CancellationToken)).Name);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task CrashWorker()
    {
        var path = Environment.GetEnvironmentVariable(WorkerPath);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var stage = Environment.GetEnvironmentVariable(WorkerStage)!;
        await using var read = Open(path);
        var id = (await read.BaseItems.SingleAsync(e => e.Path == "/generated/movie.mkv", TestContext.Current.CancellationToken)).Id;
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Open(path, new KillAfterDelete(path, stage), new KillBeforeCommit(path, stage)));
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.ReverseVirtualPath(It.IsAny<string>())).Returns((string value) => value);
        var service = new ItemPersistenceService(factory.Object, host.Object, NullLogger<ItemPersistenceService>.Instance);
        var item = new Movie
        {
            Id = id, Path = "/generated/movie.mkv", RunTimeTicks = 2,
            PendingMediaInfo = new MediaInfoSnapshot
            {
                Streams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "new" }],
                Attachments = [new MediaAttachment { Index = 0, FileName = "new" }],
                Chapters = [new ChapterInfo { Name = "new" }]
            }
        };
        await service.SaveMediaInfoAsync(item, TestContext.Current.CancellationToken);
        Assert.Fail("The fault injection point was not reached.");
    }

    private static JellyfinDbContext Open(string path, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite("Data Source=" + path + ";Pooling=False").AddInterceptors(interceptors).Options;
        return new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static void Kill(string path)
    {
        File.WriteAllText(path + ".killed", "fault reached");
        using var process = Process.GetCurrentProcess();
        process.Kill();
        Environment.Exit(137);
    }

    private sealed class KillAfterDelete(string path, string stage) : DbCommandInterceptor
    {
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal) && command.CommandText.Contains(stage, StringComparison.Ordinal))
            {
                Kill(path);
            }

            return result;
        }
    }

    private sealed class KillBeforeCommit(string path, string stage) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (stage == "commit")
            {
                Kill(path);
            }

            return ValueTask.FromResult(result);
        }
    }
}
