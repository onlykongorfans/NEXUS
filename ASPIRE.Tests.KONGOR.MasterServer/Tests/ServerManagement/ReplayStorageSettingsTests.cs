namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.ServerManagement;

public sealed class ReplayStorageSettingsTests
{
    [Test]
    public async Task Absent_Configuration_Disables_Replay_Transfers()
    {
        await Assert.That(ReplayStorageSettings.Load(null).Enabled).IsFalse();
    }

    [Test]
    [Arguments("https://store.test/replays/", true)]
    [Arguments("http://store.test/replays/", false)]
    [Arguments("https://user:password@store.test/replays/", false)]
    public async Task Uploads_Require_HTTPS_And_Credentials_Are_Kept_Out_Of_URLs(string uploadURL, bool valid)
    {
        string path = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["url"] = uploadURL, ["download_url"] = "http://files.test/replays/", ["receiver_url"] = "http://master.test/replays/upload",
                ["username"] = "example", ["password"] = "example-password",
                ["native_password_sha256"] = new string('A', 64), ["native_hash_suffix"] = new string('B', 64)
            }));

            if (valid)
            {
                ReplayStorageSettings settings = ReplayStorageSettings.Load(path);
                await Assert.That(settings.Enabled).IsTrue();
                await Assert.That(settings.GetDownloadURL(123)).IsEqualTo("http://files.test/replays/M123.honreplay");
                await Assert.That(settings.ReceiverTarget).IsEqualTo("master.test/replays/upload");
            }
            else
                await Assert.That(() => ReplayStorageSettings.Load(path)).Throws<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
