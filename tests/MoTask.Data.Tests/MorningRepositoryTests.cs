using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MoTask.Core.Abstractions;
using MoTask.Core.Model;
using MoTask.Data.Repositories;
using Xunit;

namespace MoTask.Data.Tests;

public class MorningRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    private async Task InitAsync()
    {
        await using var ctx = _db.CreateContext();
        await new DatabaseInitializer(ctx).InitializeAsync();
    }

    private static MorningRun NewRun(DateOnly date, MorningRunStatus status = MorningRunStatus.Pending)
        => new()
        {
            Date = date, Status = status, SessionId = Guid.NewGuid(),
            Instruction = "指示", JobFolder = @"C:\work\morning\0001-2026-09-07",
            StartedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
        };

    private static TriageCandidate NewCandidate(int runId, string externalId, TriageStatus status = TriageStatus.Pending)
        => new()
        {
            MorningRunId = runId, ExternalId = externalId, Source = "Outlook",
            From = "顧客A 山本さん", Title = "請求先情報を更新する",
            Evidence = "「9月8日までに」", Link = "https://outlook.office.com/x",
            Reasoning = "依頼が明確", ReceivedAt = new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc),
            SuggestedDueDate = new DateOnly(2026, 9, 8), SuggestedProject = "顧客A",
            SuggestedAction = TriageAction.Register, Status = status,
        };

    [Fact]
    public async Task MorningRun_RoundTrips()
    {
        await InitAsync();
        int id;
        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);
            var run = NewRun(new DateOnly(2026, 9, 7));
            run.PlanJson = "{\"groups\":[]}";
            run.ProcessedLines = 12;
            repo.Add(run);
            await ctx.SaveChangesAsync();
            id = run.Id;
        }

        await using (var ctx = _db.CreateContext())
        {
            var run = await new MorningRepository(ctx).GetRunAsync(id);

            run!.Date.Should().Be(new DateOnly(2026, 9, 7));
            run.Status.Should().Be(MorningRunStatus.Pending);
            run.Instruction.Should().Be("指示");
            run.JobFolder.Should().Be(@"C:\work\morning\0001-2026-09-07");
            run.PlanJson.Should().Be("{\"groups\":[]}");
            run.ProcessedLines.Should().Be(12);
        }
    }

    [Fact]
    public async Task Status_IsStoredAsText()
    {
        await InitAsync();
        await using (var ctx = _db.CreateContext())
        {
            new MorningRepository(ctx).Add(NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var stored = await ctx.Database
                .SqlQueryRaw<string>("SELECT Status AS Value FROM MorningRuns").ToListAsync();

            stored.Should().Equal(new[] { "Ingested" }, "enum は既存の流儀どおり文字列で持つ");
        }
    }

    [Fact]
    public async Task TriageCandidate_RoundTrips_IncludingTheFromColumn()
    {
        await InitAsync();
        int runId;
        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);
            var run = NewRun(new DateOnly(2026, 9, 7));
            repo.Add(run);
            await ctx.SaveChangesAsync();
            runId = run.Id;
            repo.AddCandidate(NewCandidate(runId, "outlook:AAMkAD001"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var candidate = (await new MorningRepository(ctx).GetQueueAsync(runId)).Single();

            candidate.From.Should().Be("顧客A 山本さん", "From は SQLite の予約語だが EF が引用符で囲む");
            candidate.Evidence.Should().Be("「9月8日までに」");
            candidate.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
            candidate.SuggestedAction.Should().Be(TriageAction.Register);
            candidate.Status.Should().Be(TriageStatus.Pending);
        }
    }

    [Fact]
    public async Task ExternalId_IsUnique()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var run = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(run);
        await ctx.SaveChangesAsync();

        repo.AddCandidate(NewCandidate(run.Id, "outlook:same"));
        repo.AddCandidate(NewCandidate(run.Id, "outlook:same"));

        var save = async () => await ctx.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>("却下した候補を二度と拾わないための鍵");
    }

    [Fact]
    public async Task GetKnownExternalIds_ReturnsOnlyTheOnesAlreadyStored()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var run = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(run);
        await ctx.SaveChangesAsync();
        repo.AddCandidate(NewCandidate(run.Id, "outlook:known"));
        await ctx.SaveChangesAsync();

        var known = await repo.GetKnownExternalIdsAsync(new[] { "outlook:known", "outlook:new" });

        known.Should().Equal("outlook:known");
    }

    [Fact]
    public async Task GetQueue_IsTodaysPendingPlusOlderLaters()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var yesterday = NewRun(new DateOnly(2026, 9, 6));
        var today = NewRun(new DateOnly(2026, 9, 7));
        repo.Add(yesterday);
        repo.Add(today);
        await ctx.SaveChangesAsync();

        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:later", TriageStatus.Later));
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:rejected", TriageStatus.Rejected));
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:registered", TriageStatus.Registered));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:fresh"));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:decided-today", TriageStatus.Later));
        await ctx.SaveChangesAsync();

        var queue = await repo.GetQueueAsync(today.Id);

        queue.Select(c => c.ExternalId).Should().Equal("outlook:later", "outlook:fresh");
    }

    [Fact]
    public async Task GetCandidatesOfRun_ReturnsEveryStatus_ButOnlyThatRun()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        var yesterday = NewRun(new DateOnly(2026, 9, 6), MorningRunStatus.Ingested);
        var today = NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Ingested);
        repo.Add(yesterday);
        repo.Add(today);
        await ctx.SaveChangesAsync();
        repo.AddCandidate(NewCandidate(yesterday.Id, "outlook:old", TriageStatus.Later));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:registered", TriageStatus.Registered));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:rejected", TriageStatus.Rejected));
        repo.AddCandidate(NewCandidate(today.Id, "outlook:pending"));
        await ctx.SaveChangesAsync();

        var all = await repo.GetCandidatesOfRunAsync(today.Id);

        all.Select(c => c.ExternalId).Should().Equal(
            new[] { "outlook:registered", "outlook:rejected", "outlook:pending" },
            "解決には決着済みの候補も要る。他の実行の候補は含めない");
    }

    [Fact]
    public async Task GetUnfinishedRun_FindsOnlyPendingOrRunning()
    {
        await InitAsync();
        await using var ctx = _db.CreateContext();
        var repo = new MorningRepository(ctx);
        repo.Add(NewRun(new DateOnly(2026, 9, 5), MorningRunStatus.Ingested));
        repo.Add(NewRun(new DateOnly(2026, 9, 6), MorningRunStatus.Cancelled));
        await ctx.SaveChangesAsync();

        (await repo.GetUnfinishedRunAsync()).Should().BeNull();
        (await repo.CountRunsAsync()).Should().Be(2);

        var running = NewRun(new DateOnly(2026, 9, 7), MorningRunStatus.Running);
        repo.Add(running);
        await ctx.SaveChangesAsync();

        (await repo.GetUnfinishedRunAsync())!.Id.Should().Be(running.Id);
        (await repo.GetLatestRunAsync())!.Id.Should().Be(running.Id);
    }

    /// <summary>
    /// MorningService.StartAsync の後始末が本番経路で効くことを実 SQLite で押さえる
    /// （仕様 §12）。EfUnitOfWork.SaveChangesAsync は失敗時に例外を投げる前に
    /// ChangeTracker.Clear() する（TransactionTests 参照）ので、1 回目の保存で採番済みの
    /// 行を保持したままの C# インスタンスをそのまま書き換えて保存しても何も反映されない。
    /// リポジトリから読み直せば追跡された同一インスタンスが返るので、それを Failed に倒す。
    /// </summary>
    [Fact]
    public async Task GetUnfinishedRun_ReturnsATrackedInstance_AfterAFailedSaveClearsTheTracker()
    {
        await InitAsync();
        int runId;
        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);
            var uow = new EfUnitOfWork(ctx);
            var run = NewRun(new DateOnly(2026, 9, 7));
            repo.Add(run);
            await uow.SaveChangesAsync(); // 1回目: 成功し、Id が採番される
            runId = run.Id;

            // 2回目の保存で運ぶはずだった変更（指示文の書き戻しを模す）。
            run.Instruction = "書き戻すはずだった指示文";
            // 重複した ExternalId → ユニーク制約違反で2回目の SaveChanges が失敗する
            // （ExternalId_IsUnique と同じ仕掛け）。
            repo.AddCandidate(NewCandidate(run.Id, "outlook:dup"));
            repo.AddCandidate(NewCandidate(run.Id, "outlook:dup"));

            var act = () => uow.SaveChangesAsync();

            await act.Should().ThrowAsync<PersistenceException>();
            ctx.ChangeTracker.Entries().Should().BeEmpty("失敗後は EfUnitOfWork が追跡を捨てる（run も Detach 済み）");

            // MorningService.StartAsync の catch と同じ手順: 読み直して Failed に倒す。
            var persisted = await repo.GetUnfinishedRunAsync();
            persisted.Should().NotBeNull("1回目の保存は成功しているので DB には行が残っている");
            persisted!.Id.Should().Be(runId);
            persisted.Instruction.Should().Be("指示", "2回目の変更はコミットされていない");

            persisted.Status = MorningRunStatus.Failed;
            persisted.ErrorMessage = "テスト用の保存失敗";
            await uow.SaveChangesAsync();
        }

        await using (var ctx = _db.CreateContext())
        {
            var repo = new MorningRepository(ctx);

            (await repo.GetUnfinishedRunAsync()).Should().BeNull("Failed に倒れたので二重起動防止に引っかからない");
            var stored = await repo.GetRunAsync(runId);
            stored!.Status.Should().Be(MorningRunStatus.Failed);
            stored.ErrorMessage.Should().Be("テスト用の保存失敗");
        }
    }

    public void Dispose() => _db.Dispose();
}
