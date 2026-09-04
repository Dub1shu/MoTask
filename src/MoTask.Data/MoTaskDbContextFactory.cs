using Microsoft.EntityFrameworkCore.Design;

namespace MoTask.Data;

public sealed class MoTaskDbContextFactory : IDesignTimeDbContextFactory<MoTaskDbContext>
{
    public MoTaskDbContext CreateDbContext(string[] args)
        => new(MoTaskDbContextOptions.Create("Data Source=design-time.db"));
}
