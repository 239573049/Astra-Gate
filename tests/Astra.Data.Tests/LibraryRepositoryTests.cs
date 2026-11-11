using Astra.Core.Library;

namespace Astra.Data.Tests;

public sealed class LibraryRepositoryTests
{
    private static LibrarySkillRecord Skill(string name) => new()
    {
        Name = name, Description = "d", SourceKind = SkillSourceKinds.Local, SourceJson = "{}", Hash = "h",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Skills_and_placements_round_trip_and_placements_follow_their_skill()
    {
        var db = await TestDb.InitializeAsync();
        await db.Library.UpsertSkillAsync(Skill("pdf"));
        await db.Library.UpsertPlacementAsync(new LibraryPlacementRecord
        {
            Path = "/h/.claude/skills/pdf", SkillName = "pdf", Target = "claude-code", How = PlacementHows.Copy,
            CopyHash = "abc", Origin = PlacementOrigins.Migrated, PlacedAt = DateTimeOffset.UtcNow,
        });

        var skill = await db.Library.GetSkillAsync("pdf");
        Assert.Equal("d", skill!.Description);
        var placements = await db.Library.ListPlacementsAsync("pdf");
        var p = Assert.Single(placements);
        Assert.Equal(("claude-code", PlacementHows.Copy, "abc", PlacementOrigins.Migrated), (p.Target, p.How, p.CopyHash, p.Origin));

        var changed = Skill("pdf");
        changed.Description = "new";
        await db.Library.UpsertSkillAsync(changed);
        Assert.Equal("new", (await db.Library.GetSkillAsync("pdf"))!.Description);

        await db.Library.DeleteSkillAsync("pdf");
        Assert.Empty(await db.Library.ListPlacementsAsync());
    }

    [Fact]
    public async Task A_placement_path_is_unique()
    {
        var db = await TestDb.InitializeAsync();
        await db.Library.UpsertSkillAsync(Skill("a"));
        await db.Library.UpsertSkillAsync(Skill("b"));
        var p = new LibraryPlacementRecord { Path = "/x", SkillName = "a", Target = "shared", How = PlacementHows.Symlink, Origin = PlacementOrigins.Applied, PlacedAt = DateTimeOffset.UtcNow };
        await db.Library.UpsertPlacementAsync(p);
        p.SkillName = "b";
        await db.Library.UpsertPlacementAsync(p);
        Assert.Equal("b", Assert.Single(await db.Library.ListPlacementsAsync()).SkillName);
    }
}
