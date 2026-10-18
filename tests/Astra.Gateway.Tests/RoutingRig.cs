using System.Security.Cryptography;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;

namespace Astra.Gateway.Tests;

/// <summary>A reversible stand-in for the DataProtection-backed secret protector.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + plaintext;

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith("enc:", StringComparison.Ordinal)
            ? protectedValue["enc:".Length..]
            : throw new CryptographicException("not protected by this protector");
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class RoutingClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A real database with one usable provider serving the given models: what a grade's model is chosen from.</summary>
internal sealed class RoutingStore
{
    public static readonly string[] DefaultModels = ["small-model", "mid-model", "big-model"];

    public required AstraDatabase Db { get; init; }
    public required Provider Provider { get; init; }

    public static async Task<RoutingStore> StartAsync(params string[] models)
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-gw-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = await AstraDatabase.InitializeAsync(new AstraPaths(dir));
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Models",
            AuthScheme = AuthSchemes.Bearer,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://models.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await db.Providers.InsertAsync(provider);
        foreach (var model in models.Length == 0 ? DefaultModels : models)
            await db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = model });
        return new RoutingStore { Db = db, Provider = provider };
    }

    /// <summary>Another usable provider serving <paramref name="models"/>.</summary>
    public async Task<Provider> AddProviderAsync(string name, params string[] models)
    {
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = name,
            AuthScheme = AuthSchemes.Bearer,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = $"https://{name.ToLowerInvariant()}.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await Db.Providers.InsertAsync(provider);
        foreach (var model in models) await Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = model });
        return provider;
    }

    /// <summary>A service over this database, as the server builds it.</summary>
    public SmartRoutingService NewService() => new(Db, new ClientRouting(Db, new EffectiveModelResolver(Db)));

    /// <summary>Settings that can be switched on: a judge key and a model for each of the three default grades.</summary>
    public static void Complete(SmartRoutingSettings s, ISecretProtector protector)
    {
        s.Judge.ApiKeyProtected = protector.Protect("sk-judge");
        s.Grades[0].Model = "small-model";
        s.Grades[1].Model = "mid-model";
        s.Grades[2].Model = "big-model";
    }
}
