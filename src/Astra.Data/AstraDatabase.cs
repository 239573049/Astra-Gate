using Astra.Core;
using Astra.Data.Repositories;

namespace Astra.Data;

/// <summary>
/// One-stop data-layer entry point: ensures the data directories, applies migrations, imports the
/// embedded seed catalog and exposes the repositories. The server registers these instances in DI.
/// </summary>
public sealed class AstraDatabase
{
    private AstraDatabase(AstraPaths paths, SqliteConnectionFactory factory)
    {
        Paths = paths;
        Factory = factory;
        Settings = new SettingsRepository(factory);
        Models = new ModelRepository(factory);
        Providers = new ProviderRepository(factory);
        Accounts = new ProviderAccountStore(factory);
        Clients = new ClientRepository(factory);
        ClientConfigState = new ClientConfigStateStore(factory);
        Requests = new RequestRepository(factory);
        Migrations = new MigrationRunner(factory, paths);
        Seed = new SeedImporter(factory);
    }

    public AstraPaths Paths { get; }
    public SqliteConnectionFactory Factory { get; }
    public SettingsRepository Settings { get; }
    public ModelRepository Models { get; }
    public ProviderRepository Providers { get; }
    public ProviderAccountStore Accounts { get; }
    public ClientRepository Clients { get; }
    public ClientConfigStateStore ClientConfigState { get; }
    public RequestRepository Requests { get; }
    public MigrationRunner Migrations { get; }
    public SeedImporter Seed { get; }

    /// <summary>Ensures directories, migrates the database and (by default) imports the seed catalog.</summary>
    public static async Task<AstraDatabase> InitializeAsync(
        AstraPaths paths, bool importSeed = true, CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var db = new AstraDatabase(paths, new SqliteConnectionFactory(paths));
        await db.Migrations.MigrateAsync(ct);
        if (importSeed) await db.Seed.ImportAsync(ct: ct);
        return db;
    }
}
