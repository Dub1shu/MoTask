using Microsoft.Data.Sqlite;

namespace MoTask.Data;

public static class DbPaths
{
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoTask");

    public static string DefaultDatabase => Path.Combine(DefaultDirectory, "motask.db");

    public static string ConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
}
