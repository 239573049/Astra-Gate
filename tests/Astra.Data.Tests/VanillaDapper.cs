using System.Runtime.CompilerServices;

namespace Astra.Data.Tests;

/// <summary>
/// Restores the vanilla-Dapper globals that Astra.Data's <c>SqliteConnectionFactory</c> used to set:
/// this assembly opts out of Dapper.AOT (<c>[module: DapperAot(false)]</c>), so its direct Dapper
/// calls run through vanilla SqlMapper, which needs the underscore column convention and the
/// DateTimeOffset handler registered. Shipping code no longer sets them — calling
/// <c>SqlMapper.AddTypeHandler</c>/<c>DefaultTypeMap</c> from an AOT-published assembly roots
/// vanilla Dapper's reflection machinery (IL2104/IL3053).
/// Mirrored in tests/Astra.Server.IntegrationTests/VanillaDapper.cs — keep the two in sync.
/// </summary>
internal static class VanillaDapper
{
    [ModuleInitializer]
    internal static void Configure()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        Dapper.SqlMapper.AddTypeHandler(Astra.Data.DateTimeOffsetHandler.Instance);
    }
}
