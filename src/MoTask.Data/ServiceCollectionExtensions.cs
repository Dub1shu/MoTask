using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Data.Repositories;

namespace MoTask.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 単一ユーザーのデスクトップアプリなので DbContext はシングルトン。
    /// BoardService 側で操作を直列化しているため同時アクセスは起きない。
    /// </summary>
    public static IServiceCollection AddMoTaskData(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<MoTaskDbContext>(
            o => o.UseSqlite(DbPaths.ConnectionString(dbPath)),
            contextLifetime: ServiceLifetime.Singleton,
            optionsLifetime: ServiceLifetime.Singleton);
        services.AddSingleton<IBoardRepository, BoardRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
