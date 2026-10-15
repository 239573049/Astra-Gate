using Astra.Core;
using Astra.Core.Tokens;
using Astra.Data.Tests;

namespace Astra.Data;

public class TokenRepositoryTests
{
    [Fact]
    public async Task The_Provider_Pool_Is_Saved_And_Read_Back()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var pool = new TokenPool
        {
            Strategy = TokenPoolStrategies.Weighted,
            Members = [new TokenPoolMember { ProviderId = "a", Weight = 3 }, new TokenPoolMember { ProviderId = "b" }],
        };
        await db.Tokens.InsertAsync(new TokenRecord { Id = "t1", Name = "Team", PoolJson = Json.Serialize(pool), CreatedAt = now, UpdatedAt = now });

        var read = (await db.Tokens.GetAsync("t1"))!;
        Assert.Equal(["a", "b"], read.Pool()!.Members.Select(m => m.ProviderId));
        Assert.Equal(3, read.Pool()!.Members[0].Weight);

        read.PoolJson = null;
        Assert.True(await db.Tokens.UpdateAsync(read));
        Assert.Null((await db.Tokens.GetAsync("t1"))!.PoolJson);
    }
}
