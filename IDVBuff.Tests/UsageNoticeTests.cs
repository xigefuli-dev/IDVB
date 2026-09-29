using IDVBuff.Lifecycle;
using Xunit;

namespace IDVBuff.Tests;

public sealed class UsageNoticeTests
{
    [Fact]
    public void MissingChangedAndDamagedReceiptsRequireConfirmation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IDVB-notice-" + Guid.NewGuid());
        var path = Path.Combine(directory, "receipt.txt");
        try
        {
            Assert.False(UsageNotice.IsAccepted(path));
            UsageNotice.Accept(path);
            Assert.True(UsageNotice.IsAccepted(path));
            File.WriteAllText(path, "previous-content-fingerprint");
            Assert.False(UsageNotice.IsAccepted(path));
            File.WriteAllText(path, UsageNotice.Fingerprint[..12]);
            Assert.False(UsageNotice.IsAccepted(path));
            UsageNotice.Accept(path);
            Assert.True(UsageNotice.IsAccepted(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnwritableReceiptCannotBeAcknowledged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IDVB-notice-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(UsageNotice.IsAccepted(directory));
            Assert.ThrowsAny<Exception>(() => UsageNotice.Accept(directory));
        }
        finally { Directory.Delete(directory); }
    }
}
