using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Definitions.Webhooks;
using Mostlylucid.BotDetection.Privacy;

namespace Mostlylucid.BotDetection.Test.Data;

public sealed class SqliteWebhookReputationStoreTests : IDisposable
{
    private static readonly PiiHasher TestHasher = new(Encoding.UTF8.GetBytes("wh-test-signing-key-0123456789ab"));
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"wh_{Guid.NewGuid():N}.db");
    private SqliteWebhookReputationStore New() => new(_db, WebhookCatalog.Default, TestHasher);
    public void Dispose() { if (File.Exists(_db)) File.Delete(_db); }

    [Fact]
    public void Dominant_ip_requires_min_count_and_share()
    {
        var s = New();
        for (var i = 0; i < 25; i++) s.RecordRequest("/h", "1.1.1.1");   // dominant
        s.RecordRequest("/h", "9.9.9.9");                                 // rare
        s.IsDominantIp("/h", "1.1.1.1").Should().BeTrue();
        s.IsDominantIp("/h", "9.9.9.9").Should().BeFalse();
    }

    [Fact]
    public void Verified_record_requires_consistent_2xx_over_4xx()
    {
        var s = New();
        for (var i = 0; i < 12; i++) s.RecordOutcome("/h", "1.1.1.1", 200);
        s.RecordOutcome("/h", "1.1.1.1", 400);
        s.HasVerifiedRecord("/h", "1.1.1.1").Should().BeTrue();
        for (var i = 0; i < 12; i++) s.RecordOutcome("/h", "2.2.2.2", 400); // spoofer: all 4xx
        s.HasVerifiedRecord("/h", "2.2.2.2").Should().BeFalse();
    }

    [Fact]
    public void Server_5xx_is_neutral_does_not_demote_verified_sender()
    {
        var s = New();
        for (var i = 0; i < 12; i++) s.RecordOutcome("/h", "1.1.1.1", 200); // verified
        for (var i = 0; i < 50; i++) s.RecordOutcome("/h", "1.1.1.1", 503); // receiver outage: retries
        s.HasVerifiedRecord("/h", "1.1.1.1").Should().BeTrue("5xx is the receiver's fault, not the sender's");
    }

    [Fact]
    public void Persists_across_reopen()
    {
        { var s = New(); for (var i=0;i<25;i++) s.RecordRequest("/h","1.1.1.1"); }
        New().IsDominantIp("/h", "1.1.1.1").Should().BeTrue();
    }

    [Fact]
    public void Raw_ip_is_never_persisted_but_correlation_still_works()
    {
        var s = New();
        const string rawIp = "1.2.3.4";
        for (var i = 0; i < 25; i++) s.RecordRequest("/h", rawIp);

        // Zero-PII: read the raw `ip` column back directly and assert the cleartext
        // IP never landed on disk.
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ip FROM webhook_endpoint_ip WHERE endpoint = @endpoint";
        cmd.Parameters.AddWithValue("@endpoint", "/h");
        var storedIp = (string)cmd.ExecuteScalar()!;

        storedIp.Should().NotBe(rawIp, "the raw IP must never touch the persisted column");
        storedIp.Should().NotContain(rawIp, "the raw IP must not even appear as a substring of the stored value");

        // Correlation is preserved: same raw IP in -> same hash internally -> dominance
        // still resolves correctly on the public (raw-IP-taking) interface surface.
        s.IsDominantIp("/h", rawIp).Should().BeTrue();
    }

    [Fact]
    public void Ip_hash_is_keyed_different_signing_key_produces_different_hash()
    {
        var dbA = Path.Combine(Path.GetTempPath(), $"wh_{Guid.NewGuid():N}.db");
        var dbB = Path.Combine(Path.GetTempPath(), $"wh_{Guid.NewGuid():N}.db");
        try
        {
            var hasherA = new PiiHasher(Encoding.UTF8.GetBytes("wh-key-one-0123456789abcdef"));
            var hasherB = new PiiHasher(Encoding.UTF8.GetBytes("wh-key-two-0123456789abcdef"));
            var storeA = new SqliteWebhookReputationStore(dbA, WebhookCatalog.Default, hasherA);
            var storeB = new SqliteWebhookReputationStore(dbB, WebhookCatalog.Default, hasherB);
            const string ip = "5.6.7.8";
            storeA.RecordRequest("/h", ip);
            storeB.RecordRequest("/h", ip);

            static string ReadHash(string db)
            {
                using var conn = new SqliteConnection($"Data Source={db}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ip FROM webhook_endpoint_ip WHERE endpoint = @endpoint";
                cmd.Parameters.AddWithValue("@endpoint", "/h");
                return (string)cmd.ExecuteScalar()!;
            }

            var hashA = ReadHash(dbA);
            var hashB = ReadHash(dbB);
            hashA.Should().NotBe(hashB,
                "the same raw IP under two different signing keys must hash differently -- proof this is a " +
                "KEYED HMAC (PiiHasher), not an unkeyed SHA-256 that would collide regardless of key");
        }
        finally
        {
            if (File.Exists(dbA)) File.Delete(dbA);
            if (File.Exists(dbB)) File.Delete(dbB);
        }
    }
}
