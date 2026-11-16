using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Api;
using Astra.Server.Backup;

namespace Astra.Server.IntegrationTests;

/// <summary>The backup envelope (against a real magpie envelope) and the S3 SigV4 signer (against AWS's published examples).</summary>
public sealed class BackupFormatTests
{
    private const int Fast = 100_000; // the smallest iteration count Open accepts; keeps these tests quick

    private static JsonObject Bundle(string marker = "hello") => new() { ["bundle"] = 1, ["marker"] = marker };

    [Fact]
    public void An_encrypted_backup_round_trips_and_a_wrong_passphrase_is_refused()
    {
        var file = BackupEnvelope.Seal(Bundle(), "correct horse", hasKeys: true, Fast);
        Assert.True(BackupEnvelope.IsEncrypted(file));
        Assert.DoesNotContain("hello", Encoding.UTF8.GetString(file));
        Assert.Equal("hello", BackupEnvelope.Open(file, "correct horse")["marker"]!.GetValue<string>());
        var ex = Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(file, "wrong horse"));
        Assert.Contains("Wrong passphrase", ex.Message);
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(file, null));
    }

    [Fact]
    public void Every_header_field_is_bound_into_the_ciphertext()
    {
        var file = (JsonObject)JsonNode.Parse(BackupEnvelope.Seal(Bundle(), "correct horse", hasKeys: true, Fast))!;
        // The associated data covers format, version, kdf, iterations and salt: changing the salt or the count is a failed decryption, not a different key silently.
        var salt = file["salt"]!.GetValue<string>();
        var tampered = (JsonObject)file.DeepClone();
        var bytes = Convert.FromBase64String(salt);
        bytes[0] ^= 1;
        tampered["salt"] = Convert.ToBase64String(bytes);
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(tampered.ToJsonString()), "correct horse"));

        tampered = (JsonObject)file.DeepClone();
        tampered["iterations"] = Fast + 1;
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(tampered.ToJsonString()), "correct horse"));

        tampered = (JsonObject)file.DeepClone();
        var data = Convert.FromBase64String(file["data"]!.GetValue<string>());
        data[0] ^= 1;
        tampered["data"] = Convert.ToBase64String(data);
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(tampered.ToJsonString()), "correct horse"));
    }

    [Fact]
    public void Hostile_parameters_are_refused_before_any_key_derivation()
    {
        var file = (JsonObject)JsonNode.Parse(BackupEnvelope.Seal(Bundle(), "correct horse", hasKeys: true, Fast))!;
        foreach (var (field, value) in new (string, JsonNode)[] { ("iterations", 50), ("iterations", 2_000_000_000), ("nonce", Convert.ToBase64String(new byte[8])), ("salt", Convert.ToBase64String(new byte[4])), ("version", 2) })
        {
            var copy = (JsonObject)file.DeepClone();
            copy[field] = value;
            Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(copy.ToJsonString()), "correct horse"));
        }
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes("<html>sign in</html>"), "x"));
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes("""{"format":"other"}"""), "x"));
    }

    [Fact]
    public void An_unencrypted_backup_cannot_hold_keys()
    {
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Seal(Bundle(), null, hasKeys: true));
        var plain = BackupEnvelope.Seal(Bundle(), null, hasKeys: false);
        Assert.False(BackupEnvelope.IsEncrypted(plain));
        Assert.Equal("hello", BackupEnvelope.Open(plain, null)["marker"]!.GetValue<string>());
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Seal(Bundle(), "short", hasKeys: false));
    }

    // Produced by magpie's own internal/backup.Seal (commit cb4ad12, MIT) for the bundle {version:2, keys:true, app:"magpie-test"} with this passphrase.
    private const string MagpieEnvelope = """
        {"format":"magpie-backup","version":1,"kdf":"pbkdf2-sha256","iterations":600000,"salt":"y7dfbz6+SsAbCHcqPJWjLw==","nonce":"wUhA4Ftu6m+/M8JW","data":"TvCBECMOFLW1it+Rr0MTriE71JGrvhdLULeLY7o6B4sS0g9kMDWj+p6t6NFq4rmD7v/wYepzYlZTOVRCBUa+uzLJg5dKxNeyH/c3BKTvkcRK7ySI20mgheHyndDS6NCpdf0UohWsz58627bhj/9D"}
        """;

    [Fact]
    public void The_envelope_opens_one_made_by_magpie()
    {
        var opened = BackupEnvelope.Open(Encoding.UTF8.GetBytes(MagpieEnvelope), "correct horse battery", format: "magpie-backup");
        Assert.Equal("magpie-test", opened["app"]!.GetValue<string>());
        Assert.True(opened["keys"]!.GetValue<bool>());
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(MagpieEnvelope), "correct horse battery")); // not an astra-backup
        Assert.Throws<AdminApiException>(() => BackupEnvelope.Open(Encoding.UTF8.GetBytes(MagpieEnvelope), "wrong", format: "magpie-backup"));
    }

    // ------------------------------------------------------------------------------------------------ SigV4

    private const string Suite = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string S3Doc = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private static string Sign(string method, string host, string path, string? query, Dictionary<string, string> headers, string payload,
        string id, string secret, string service, DateTimeOffset at) =>
        S3Signer.Sign(method, host, path, query, headers, payload, id, secret, "us-east-1", service, at, out _);

    [Fact]
    public void Signing_matches_the_aws_sigv4_test_suite()
    {
        var at = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
        const string prefix = "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=";
        Assert.Equal(prefix + "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            Sign("GET", "example.amazonaws.com", "/", null, [], S3Signer.EmptyPayloadHash, "AKIDEXAMPLE", Suite, "service", at));
        Assert.Equal(prefix + "b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500",
            Sign("GET", "example.amazonaws.com", "/", "Param2=value2&Param1=value1", [], S3Signer.EmptyPayloadHash, "AKIDEXAMPLE", Suite, "service", at));
        Assert.Equal(prefix + "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b",
            Sign("POST", "example.amazonaws.com", "/", null, [], S3Signer.EmptyPayloadHash, "AKIDEXAMPLE", Suite, "service", at));
    }

    [Fact]
    public void Signing_matches_the_s3_documentation_examples()
    {
        var at = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
        const string host = "examplebucket.s3.amazonaws.com";
        const string id = "AKIAIOSFODNN7EXAMPLE";
        var empty = S3Signer.EmptyPayloadHash;

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            Sign("GET", host, "/test.txt", null, new() { ["Range"] = "bytes=0-9", ["X-Amz-Content-Sha256"] = empty }, empty, id, S3Doc, "s3", at));

        var put = S3Signer.Sha256Hex("Welcome to Amazon S3."u8);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class, Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
            Sign("PUT", host, "/" + S3Signer.Escape("test$file.text", true), null,
                new() { ["Date"] = "Fri, 24 May 2013 00:00:00 GMT", ["X-Amz-Storage-Class"] = "REDUCED_REDUNDANCY", ["X-Amz-Content-Sha256"] = put }, put, id, S3Doc, "s3", at));

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543",
            Sign("GET", host, "/", "lifecycle", new() { ["X-Amz-Content-Sha256"] = empty }, empty, id, S3Doc, "s3", at));

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7",
            Sign("GET", host, "/", "max-keys=2&prefix=J", new() { ["X-Amz-Content-Sha256"] = empty }, empty, id, S3Doc, "s3", at));

        Assert.Equal("a%20b/c~d%2Be%24%C3%A9", S3Signer.Escape("a b/c~d+e$é", true));
    }
}
