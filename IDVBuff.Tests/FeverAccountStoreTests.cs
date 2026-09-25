using IDVBuff.Features.GameLaunch;

namespace IDVBuff.Tests;

public sealed class FeverAccountStoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _storageDir;
    private readonly string _unisdkDir;
    private readonly string _mpayDir;

    public FeverAccountStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IDVB_FeverAccountTests_" + Guid.NewGuid().ToString("N"));
        _storageDir = Path.Combine(_testRoot, "Storage");
        _unisdkDir = Path.Combine(_testRoot, "unisdk");
        _mpayDir = Path.Combine(_testRoot, "mpay");
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_unisdkDir);
        Directory.CreateDirectory(_mpayDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task CaptureCurrentAccount_CreatesSnapshotAndSetsActive()
    {
        // 1. Prepare dummy unisdk and mpay files
        var mpayDb = Path.Combine(_unisdkDir, "aecglf6ee4aaaarz-g-a50-64-mpay.db");
        await File.WriteAllTextAsync(mpayDb, "dummy-unisdk-db");
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");
        await File.WriteAllTextAsync(h55Db, "dummy-h55-db");

        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);

        // 2. Capture
        const string ticketJson = "{\"uid\":\"1000888\",\"username\":\"tester@163.com\"}";
        var profile = await store.CaptureCurrentAccountAsync(preferredName: "大号", ticket: ticketJson);

        // 3. Verify profile
        Assert.NotNull(profile);
        Assert.Equal("大号", profile.Name);
        Assert.Equal("1000888", profile.AccountIdentifier);

        var active = store.GetActiveAccount();
        Assert.NotNull(active);
        Assert.Equal(profile.Id, active.Id);

        // Verify snapshot files
        var snapshotUnisdk = Path.Combine(_storageDir, "profiles", profile.Id, "unisdk", "aecglf6ee4aaaarz-g-a50-64-mpay.db");
        Assert.True(File.Exists(snapshotUnisdk));
        Assert.Equal("dummy-unisdk-db", await File.ReadAllTextAsync(snapshotUnisdk));

        var snapshotMpay = Path.Combine(_storageDir, "profiles", profile.Id, "netease_mpay", "h55-64-mpay.db");
        Assert.True(File.Exists(snapshotMpay));
        Assert.Equal("dummy-h55-db", await File.ReadAllTextAsync(snapshotMpay));
    }

    [Fact]
    public async Task PrepareForNewLogin_ClearsSessionDbsToPreventAutoLogin()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);

        var unisdkDb = Path.Combine(_unisdkDir, "aecglf6ee4aaaarz-g-a50-64-mpay.db");
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");
        var mwsDir = Path.Combine(_mpayDir, "MWSlocalStorage");
        Directory.CreateDirectory(mwsDir);
        var cookieFile = Path.Combine(mwsDir, "Cookies");
        await File.WriteAllTextAsync(cookieFile, "cached-cookies");
        await File.WriteAllTextAsync(unisdkDb, "session-unisdk");
        await File.WriteAllTextAsync(h55Db, "session-h55");

        Assert.True(File.Exists(unisdkDb));
        Assert.True(File.Exists(h55Db));
        Assert.True(Directory.Exists(mwsDir));

        store.PrepareForNewLogin();

        Assert.False(File.Exists(unisdkDb));
        Assert.False(File.Exists(h55Db));
        Assert.False(Directory.Exists(mwsDir));
    }

    [Fact]
    public async Task CaptureCurrentAccount_DeduplicatesRawTicketToken()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");
        await File.WriteAllTextAsync(h55Db, "session-1");

        // Raw 16-character token from NetEase MPay
        const string rawTicket = "AAAAAJEYqakG0OfD";
        var acc1 = await store.CaptureCurrentAccountAsync("官服账号 1", rawTicket);
        Assert.Single(store.GetAccounts());
        Assert.Equal(rawTicket, acc1.AccountIdentifier);
        Assert.Equal(rawTicket, store.GetActiveAccountTicket());

        // Attempting to add account again with same raw ticket token
        await File.WriteAllTextAsync(h55Db, "session-1-refreshed");
        var acc2 = await store.CaptureCurrentAccountAsync("官服账号 1", rawTicket);

        // Must NOT create a duplicate account
        Assert.Single(store.GetAccounts());
        Assert.Equal(acc1.Id, acc2.Id);
        Assert.Equal(rawTicket, store.GetActiveAccountTicket());
    }

    [Fact]
    public async Task CaptureCurrentAccount_DeduplicatesSameAccountIdentifier()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");
        await File.WriteAllTextAsync(h55Db, "session-1");

        var acc1 = await store.CaptureCurrentAccountAsync("账号1", "{\"uid\":\"99999\"}");
        Assert.Single(store.GetAccounts());

        // Scan again with same UID
        await File.WriteAllTextAsync(h55Db, "session-1-refreshed");
        var acc2 = await store.CaptureCurrentAccountAsync("账号1", "{\"uid\":\"99999\"}");

        // Must NOT create a duplicate account
        Assert.Single(store.GetAccounts());
        Assert.Equal(acc1.Id, acc2.Id);

        var snapshotFile = Path.Combine(_storageDir, "profiles", acc1.Id, "netease_mpay", "h55-64-mpay.db");
        Assert.Equal("session-1-refreshed", await File.ReadAllTextAsync(snapshotFile));
    }

    [Fact]
    public async Task SwitchAccount_RestoresExactSnapshotToBothDirectories()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);

        var unisdkDb = Path.Combine(_unisdkDir, "aecglf6ee4aaaarz-g-a50-64-mpay.db");
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");

        // Account 1
        await File.WriteAllTextAsync(unisdkDb, "unisdk-acc1");
        await File.WriteAllTextAsync(h55Db, "h55-acc1");
        var acc1 = await store.CaptureCurrentAccountAsync("账号1", "uid=111");

        // Account 2
        await File.WriteAllTextAsync(unisdkDb, "unisdk-acc2");
        await File.WriteAllTextAsync(h55Db, "h55-acc2");
        var acc2 = await store.CaptureCurrentAccountAsync("账号2", "uid=222");

        Assert.Equal(acc2.Id, store.GetActiveAccount()?.Id);

        // Switch to Account 1
        var switchedTo1 = await store.SwitchAccountAsync(acc1.Id);
        Assert.True(switchedTo1);
        Assert.Equal(acc1.Id, store.GetActiveAccount()?.Id);
        Assert.Equal("unisdk-acc1", await File.ReadAllTextAsync(unisdkDb));
        Assert.Equal("h55-acc1", await File.ReadAllTextAsync(h55Db));

        // Switch to Account 2
        var switchedTo2 = await store.SwitchAccountAsync(acc2.Id);
        Assert.True(switchedTo2);
        Assert.Equal(acc2.Id, store.GetActiveAccount()?.Id);
        Assert.Equal("unisdk-acc2", await File.ReadAllTextAsync(unisdkDb));
        Assert.Equal("h55-acc2", await File.ReadAllTextAsync(h55Db));
    }

    [Fact]
    public async Task RollbackNewLogin_RestoresActiveAccountSession()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);
        var h55Db = Path.Combine(_mpayDir, "h55-64-mpay.db");
        await File.WriteAllTextAsync(h55Db, "original-session");
        var acc = await store.CaptureCurrentAccountAsync("大号", "uid=123");

        // User clicks "+ 添加账号", environment prepared
        store.PrepareForNewLogin();
        Assert.False(File.Exists(h55Db));

        // User cancels login
        await store.RollbackNewLoginAsync();

        // Original session must be restored
        Assert.True(File.Exists(h55Db));
        Assert.Equal("original-session", await File.ReadAllTextAsync(h55Db));
    }

    [Fact]
    public async Task RenameAndDeleteAccount_WorksCorrectly()
    {
        var store = new FeverAccountStore(_storageDir, _unisdkDir, _mpayDir);
        var unisdkDb = Path.Combine(_unisdkDir, "aecglf6ee4aaaarz-g-a50-64-mpay.db");
        await File.WriteAllTextAsync(unisdkDb, "test-data");

        var acc = await store.CaptureCurrentAccountAsync("原名称");
        Assert.Equal("原名称", store.GetActiveAccount()?.Name);

        // Rename
        var renamed = store.RenameAccount(acc.Id, "新名称");
        Assert.True(renamed);
        Assert.Equal("新名称", store.GetActiveAccount()?.Name);

        // Delete
        var deleted = store.DeleteAccount(acc.Id);
        Assert.True(deleted);
        Assert.Empty(store.GetAccounts());
        Assert.Null(store.GetActiveAccount());
        Assert.False(Directory.Exists(Path.Combine(_storageDir, "profiles", acc.Id)));
    }

    [Fact]
    public void FeverIpcBridge_CanStartAndStopWithoutException()
    {
        var bridge = FeverIpcBridge.Instance;
        bridge.Start("test_ticket_value");
        bridge.SetTicket("updated_ticket");
        bridge.Stop();
    }

    [Fact]
    public void FeverIpcBridge_PacketStructureAndRoundTripEncryption_IsValid()
    {
        const string rawTicket = "AAAAAH3H_f0KH-GU";
        var packet = FeverIpcBridge.CreateTicketPacket(rawTicket);

        // Packet structure assertions
        var payloadLen = BitConverter.ToInt32(packet, 0);
        Assert.Equal(packet.Length - 12, payloadLen);
        Assert.Equal(14, BitConverter.ToInt32(packet, 4));  // opcode 14
        Assert.Equal(0, BitConverter.ToInt32(packet, 8));   // flags 0

        // Roundtrip decryption
        var decryptedTicket = FeverIpcBridge.DecryptTicketPacket(packet);
        Assert.Equal(rawTicket, decryptedTicket);
    }
}
