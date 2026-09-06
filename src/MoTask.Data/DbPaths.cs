using Microsoft.Data.Sqlite;
using MoTask.Core;

namespace MoTask.Data;

public static class DbPaths
{
    public static string DefaultDirectory => AppPaths.DataDirectory;

    public static string DefaultDatabase => Path.Combine(DefaultDirectory, "motask.db");

    public static string DefaultSettings => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>DB と同じフォルダの settings.json。</summary>
    public static string SettingsNextTo(string dbPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? DefaultDirectory, "settings.json");

    public static string ConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
}
