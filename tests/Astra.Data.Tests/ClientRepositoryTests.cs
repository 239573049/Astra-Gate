using Astra.Core.Clients;
using Astra.Data.Tests;

namespace Astra.Data;

public class ClientRepositoryTests
{
    [Fact]
    public async Task Client_Rows_Are_Lazy_And_Upsert_Works()
    {
        var db = await TestDb.InitializeAsync();
        Assert.Empty(await db.Clients.ListAsync());
        Assert.Null(await db.Clients.GetAsync("codex"));

        var applied = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var record = new ClientRecord
        {
            Kind = ClientKinds.Codex,
            Enabled = true,
            TokenId = "default",
            SelectedModel = "gpt-5",
            ExtraJson = "{\"small_model\":\"gpt-5-mini\"}",
            AppliedAt = applied,
        };
        await db.Clients.UpsertAsync(record);

        var got = (await db.Clients.GetAsync(ClientKinds.Codex))!;
        Assert.True(got.Enabled);
        Assert.Equal("default", got.TokenId);
        Assert.Equal("gpt-5", got.SelectedModel);
        Assert.Equal("{\"small_model\":\"gpt-5-mini\"}", got.ExtraJson);
        Assert.Equal(applied, got.AppliedAt);

        // Upsert again updates in place; still exactly one row.
        record.Enabled = false;
        record.AppliedAt = null;
        await db.Clients.UpsertAsync(record);
        var list = await db.Clients.ListAsync();
        Assert.Single(list);
        Assert.False(list[0].Enabled);
        Assert.Null(list[0].AppliedAt);
    }

    [Fact]
    public async Task Clients_Are_Listed_And_Moved_By_Token()
    {
        var db = await TestDb.InitializeAsync();
        await db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Codex, Enabled = true, TokenId = "t1" });
        await db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.OpenCode, Enabled = true, TokenId = "default" });
        await db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Pi, Enabled = false }); // null = default token

        Assert.Equal([ClientKinds.Codex], (await db.Clients.ListByTokenAsync("t1", false)).Select(c => c.Kind));
        Assert.Equal([ClientKinds.OpenCode, ClientKinds.Pi], (await db.Clients.ListByTokenAsync("default", true)).Select(c => c.Kind));

        Assert.Equal([ClientKinds.Codex], await db.Clients.ReassignTokenAsync("t1", "default"));
        Assert.Equal("default", (await db.Clients.GetAsync(ClientKinds.Codex))!.TokenId);
        Assert.Empty(await db.Clients.ListByTokenAsync("t1", false));
    }

    [Fact]
    public async Task Bindings_Set_Get_List_And_Clear()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Astra.Core.Models.Provider { Id = "p1", Name = "Alpha" });
        await db.Providers.InsertAsync(new Astra.Core.Models.Provider { Id = "p2", Name = "Beta" });

        await db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.Codex, ProviderId = "p1" });

        // Binding created the client row lazily, without enabling it.
        var client = (await db.Clients.GetAsync(ClientKinds.Codex))!;
        Assert.False(client.Enabled);

        Assert.Equal("p1", (await db.Clients.GetBindingAsync(ClientKinds.Codex))!.ProviderId);

        // Rebinding the same (client, priority) slot replaces the provider.
        await db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.Codex, ProviderId = "p2" });
        Assert.Equal("p2", (await db.Clients.GetBindingAsync(ClientKinds.Codex))!.ProviderId);
        Assert.Single(await db.Clients.ListBindingsAsync(ClientKinds.Codex));

        Assert.Empty(await db.Clients.ListBindingsByProviderAsync("p1"));
        var bound = await db.Clients.ListBindingsByProviderAsync("p2");
        Assert.Single(bound);
        Assert.Equal(ClientKinds.Codex, bound[0].ClientKind);

        Assert.True(await db.Clients.ClearBindingAsync(ClientKinds.Codex));
        Assert.Null(await db.Clients.GetBindingAsync(ClientKinds.Codex));
        Assert.Empty(await db.Clients.ListBindingsByProviderAsync("p2"));

        // Deleting the client cascades remaining bindings.
        await db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.OpenCode, ProviderId = "p1" });
        await db.Clients.DeleteAsync(ClientKinds.OpenCode);
        Assert.Empty(await db.Clients.ListBindingsByProviderAsync("p1"));
    }

    [Fact]
    public async Task Replace_Bindings_Renumbers_Priorities_In_List_Order()
    {
        var db = await TestDb.InitializeAsync();
        foreach (var id in new[] { "p1", "p2", "p3" })
            await db.Providers.InsertAsync(new Astra.Core.Models.Provider { Id = id, Name = id });
        await db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.Codex, ProviderId = "p1" });

        await db.Clients.ReplaceBindingsAsync(ClientKinds.Codex,
            [new ClientBinding { ProviderId = "p3" }, new ClientBinding { ProviderId = "p2" }]);
        var list = await db.Clients.ListBindingsAsync(ClientKinds.Codex);
        Assert.Equal(["p3", "p2"], list.Select(b => b.ProviderId));
        Assert.Equal([0, 1], list.Select(b => b.Priority));
        Assert.Equal("p3", (await db.Clients.GetBindingAsync(ClientKinds.Codex))!.ProviderId);
        Assert.Empty(await db.Clients.ListBindingsByProviderAsync("p1"));

        // Creates the client row when missing, without enabling it.
        await db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode, [new ClientBinding { ProviderId = "p1" }]);
        Assert.False((await db.Clients.GetAsync(ClientKinds.OpenCode))!.Enabled);
    }

    [Fact]
    public async Task Bindings_Carry_Subscription_Account_And_Unpin_On_Account_Delete()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Astra.Core.Models.Provider
        {
            Id = "sub",
            Name = "Claude 订阅",
            AuthScheme = Astra.Core.Models.AuthSchemes.OAuthSubscription,
        });
        var account = new Astra.Core.Models.ProviderAccount
        {
            Id = "acc-test-1",
            ProviderId = "sub",
            DisplayName = "me@example.com",
            Status = Astra.Core.Models.AccountStatus.Active,
        };
        await db.Accounts.InsertAsync(account);
        // Storage is millisecond-precision ISO-8601; compare on that precision.
        Assert.Equal(
            account.CreatedAt.ToUnixTimeMilliseconds(),
            (await db.Accounts.GetAsync(account.Id))!.CreatedAt.ToUnixTimeMilliseconds());

        await db.Clients.SetBindingAsync(new ClientBinding
        {
            ClientKind = ClientKinds.ClaudeCode,
            ProviderId = "sub",
            AccountId = account.Id,
        });

        var binding = await db.Clients.GetBindingAsync(ClientKinds.ClaudeCode);
        Assert.Equal("sub", binding!.ProviderId);
        Assert.Equal(account.Id, binding.AccountId);

        // Deleting the account unpins the reference; the binding itself survives.
        await db.Accounts.DeleteAsync(account.Id);
        var after = await db.Clients.GetBindingAsync(ClientKinds.ClaudeCode);
        Assert.Equal("sub", after!.ProviderId);
        Assert.Null(after.AccountId);
    }

    [Fact]
    public async Task ClientConfigState_Upsert_Get_Delete_Round_Trip()
    {
        var db = await TestDb.InitializeAsync();
        var store = db.ClientConfigState;

        var entry = new ClientConfigStateEntry
        {
            ClientKind = ClientKinds.ClaudeCode,
            FilePath = "/home/u/.claude/settings.json",
            KeyPath = "env.ANTHROPIC_BASE_URL",
            OriginalAbsent = true, // key did not exist before we wrote it
            AppliedValueJson = "\"http://127.0.0.1:17321\"",
        };
        store.Upsert(entry);
        Assert.NotEqual(default, entry.AppliedAt);

        var got = store.Get(ClientKinds.ClaudeCode, "/home/u/.claude/settings.json", "env.ANTHROPIC_BASE_URL")!;
        Assert.True(got.OriginalAbsent);
        Assert.Null(got.OriginalValueJson);
        Assert.Equal("\"http://127.0.0.1:17321\"", got.AppliedValueJson);
        // Storage is millisecond-precision ISO-8601 UTC text.
        Assert.Equal(entry.AppliedAt.ToUnixTimeMilliseconds(), got.AppliedAt.ToUnixTimeMilliseconds());

        // Upsert with the same (kind, file, key) updates the row.
        entry.OriginalAbsent = false;
        entry.OriginalValueJson = "\"https://api.anthropic.com\"";
        store.Upsert(entry);
        var list = store.List(ClientKinds.ClaudeCode);
        Assert.Single(list);
        Assert.False(list[0].OriginalAbsent);
        Assert.Equal("\"https://api.anthropic.com\"", list[0].OriginalValueJson);

        // Ordering: by file_path then key_path.
        store.Upsert(new ClientConfigStateEntry
        {
            ClientKind = ClientKinds.ClaudeCode,
            FilePath = "/home/u/.claude/settings.json",
            KeyPath = "env.ANTHROPIC_AUTH_TOKEN",
        });
        store.Upsert(new ClientConfigStateEntry
        {
            ClientKind = ClientKinds.ClaudeCode,
            FilePath = "/home/u/.claude/other.json",
            KeyPath = "env.ZZZ",
        });
        var ordered = store.List(ClientKinds.ClaudeCode);
        Assert.Equal(
            ["/home/u/.claude/other.json", "/home/u/.claude/settings.json", "/home/u/.claude/settings.json"],
            ordered.Select(e => e.FilePath).ToList());
        Assert.Equal(
            ["env.ZZZ", "env.ANTHROPIC_AUTH_TOKEN", "env.ANTHROPIC_BASE_URL"],
            ordered.Select(e => e.KeyPath).ToList());
        Assert.Null(store.Get(ClientKinds.Codex, "/x", "y"));

        store.Delete(ClientKinds.ClaudeCode, "/home/u/.claude/settings.json", "env.ANTHROPIC_BASE_URL");
        Assert.Equal(2, store.List(ClientKinds.ClaudeCode).Count);

        store.DeleteAll(ClientKinds.ClaudeCode);
        Assert.Empty(store.List(ClientKinds.ClaudeCode));
    }
}
