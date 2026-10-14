using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Plugins.Tests;

/// <summary>An HttpClient whose answers a test writes: the fake plugin market (and npm).</summary>
internal sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(answer(request));
    }

    public HttpClient Client() => new(this);

    public static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

internal static class TestTarballs
{
    /// <summary>An npm-style tarball: every file under package/ (names with their own folder part are taken as given).</summary>
    public static byte[] Make(params (string Name, string Text)[] files)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    // Fixed, so the same files always pack to the same bytes (the fixtures hash one copy and serve another).
                    ModificationTime = DateTimeOffset.UnixEpoch,
                };
                writer.WriteEntry(entry);
            }
        tar.Position = 0;
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) tar.CopyTo(z);
        return gz.ToArray();
    }

    /// <summary>A middleware plugin that runs only in Astra's process (no main), at <paramref name="version"/>.</summary>
    public static byte[] Middleware(string name, string version) => Make(
        ("package/package.json", $$"""{"name":"{{name}}","version":"{{version}}","magpie":{"middleware":"./mw.js"} }"""),
        ("package/mw.js", "export default { onRequest(body) { return body; } };"));

    /// <summary>An OpenCode-style plugin with a main file (so it needs the plugin host, hence Bun).</summary>
    public static byte[] Provider(string name, string version) => Make(
        ("package/package.json", $$"""{"name":"{{name}}","version":"{{version}}","main":"index.js"}"""),
        ("package/index.js", "module.exports = async () => ({});"));

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha512(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

    /// <summary>A registry document listing one hosted package with the given versions (newest first as the site sends it).</summary>
    public static string Registry(string package, params (string Version, byte[] Bytes)[] versions)
    {
        var tarballs = string.Join(",", versions.Select(v =>
            $$"""{"version":"{{v.Version}}","url":"/api/plugin-market/packages/x/{{v.Version}}.tgz","sha256":"{{Sha256(v.Bytes)}}","sha512":"{{Sha512(v.Bytes)}}","size":{{v.Bytes.Length}} }"""));
        return $$"""{"version":2,"plugins":[{"package":"{{package}}","name":"X","kind":"middleware","origin":"astra","latest":"{{versions[0].Version}}","tarballs":[{{tarballs}}] }] }""";
    }
}
