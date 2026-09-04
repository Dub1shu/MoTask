using FluentAssertions;
using Xunit;

namespace MoTask.Data.Tests;

public class RecoveryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    [Fact]
    public void BackupAndReset_CopiesToTimestampedBak_AndRemovesOriginalAndSidecars()
    {
        // 実際には開けない壊れた DB ファイル（有効な SQLite 形式ではない）と、その副生成物を用意する。
        File.WriteAllText(_db.Path, "broken");
        File.WriteAllText(_db.Path + "-wal", "wal");
        File.WriteAllText(_db.Path + "-shm", "shm");

        var backup = DatabaseRecovery.BackupAndReset(_db.Path, new DateTime(2026, 9, 4, 8, 40, 5));

        backup.Should().Be(_db.Path + ".bak-20260904-084005");
        File.ReadAllText(backup).Should().Be("broken");
        File.Exists(_db.Path).Should().BeFalse();
        File.Exists(_db.Path + "-wal").Should().BeFalse();
        File.Exists(_db.Path + "-shm").Should().BeFalse();

        File.Delete(backup);
    }

    [Fact]
    public void BackupAndReset_WhenNoFile_ReturnsPathWithoutCreatingBackup()
    {
        string? backup = null;
        Action act = () => backup = DatabaseRecovery.BackupAndReset(_db.Path, new DateTime(2026, 9, 4, 8, 40, 5));

        act.Should().NotThrow("元ファイルが無くても呼び出し自体は失敗してはいけない");

        backup.Should().Be(_db.Path + ".bak-20260904-084005");
        File.Exists(backup).Should().BeFalse("元ファイルが無ければバックアップは作られない");
    }

    [Fact]
    public void BackupAndReset_CalledTwiceWithSameTimestamp_DoesNotOverwritePreviousBackup()
    {
        var now = new DateTime(2026, 9, 4, 8, 40, 5);

        File.WriteAllText(_db.Path, "broken-1");
        var backup1 = DatabaseRecovery.BackupAndReset(_db.Path, now);

        File.WriteAllText(_db.Path, "broken-2");
        var backup2 = DatabaseRecovery.BackupAndReset(_db.Path, now);

        backup1.Should().NotBe(backup2, "同一秒に2回呼ばれても既存のバックアップを黙って上書きしてはいけない");
        File.Exists(backup1).Should().BeTrue();
        File.Exists(backup2).Should().BeTrue();
        File.ReadAllText(backup1).Should().Be("broken-1", "1回目のバックアップ内容が2回目で上書きされていないこと");
        File.ReadAllText(backup2).Should().Be("broken-2");

        File.Delete(backup1);
        File.Delete(backup2);
    }

    public void Dispose() => _db.Dispose();
}
