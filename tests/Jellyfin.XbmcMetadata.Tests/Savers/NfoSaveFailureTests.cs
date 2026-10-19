using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.XbmcMetadata.Savers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.XbmcMetadata.Tests.Savers;

public sealed class NfoSaveFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-nfo-save-" + Guid.NewGuid().ToString("N"));
    private readonly TestSaver _saver = new();

    public NfoSaveFailureTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCopy_PreservesExistingNfoAndRemovesTemporaryFile(bool cancel)
    {
        var path = Path.Combine(_directory, "movie.nfo");
        await File.WriteAllTextAsync(path, "<movie>original</movie>", TestContext.Current.CancellationToken);
        using var input = new FailingStream(cancel);
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => _saver.SaveToFileAsync(input, path, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => _saver.SaveToFileAsync(input, path, TestContext.Current.CancellationToken));
        }

        Assert.Equal("<movie>original</movie>", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ReplaceFailure_PreservesDestinationAndRemovesTemporaryFile()
    {
        var path = Path.Combine(_directory, "movie.nfo");
        Directory.CreateDirectory(path);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("<movie>new</movie>"));
        await Assert.ThrowsAnyAsync<IOException>(() => _saver.SaveToFileAsync(input, path, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSave_WritesProtectedValuesAndPreservesSymlink(bool targetExists)
    {
        // Windows symlink creation requires a host privilege that is not enabled on all runners.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Symlink creation requires host privileges on Windows.");
        }

        var target = Path.Combine(_directory, "target.nfo");
        var link = Path.Combine(_directory, "movie.nfo");
        if (targetExists)
        {
            await File.WriteAllTextAsync(target, "<movie>old</movie>", TestContext.Current.CancellationToken);
        }

        File.CreateSymbolicLink(link, target);
        const string xml = "<movie><genre>中文剧情</genre><lockedfields>Genres|ProductionLocations</lockedfields></movie>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await _saver.SaveToFileAsync(input, link, TestContext.Current.CancellationToken);
        Assert.Equal(target, File.ResolveLinkTarget(link, true)!.FullName);
        Assert.Equal(xml, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    private sealed class FailingStream(bool cancel) : MemoryStream(new byte[4096])
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            if (cancel)
            {
                throw new OperationCanceledException();
            }

            throw new IOException("Simulated full disk after a partial write");
        }
    }

    private sealed class TestSaver : BaseNfoSaver
    {
        public TestSaver()
            : base(Mock.Of<IFileSystem>(), Configuration(), null!, null!, null!, NullLogger<BaseNfoSaver>.Instance)
        {
        }

        public override bool IsEnabledFor(BaseItem item, ItemUpdateType updateType) => true;

        protected override void WriteCustomElements(BaseItem item, XmlWriter writer)
        {
        }

        protected override string GetLocalSavePath(BaseItem item) => item.Path;

        protected override string GetRootElementName(BaseItem item) => "movie";

        private static IServerConfigurationManager Configuration()
        {
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
            return config.Object;
        }
    }
}
