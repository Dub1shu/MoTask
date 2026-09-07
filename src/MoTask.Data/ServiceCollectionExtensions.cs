using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
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
        services.AddSingleton<IAiJobRepository, AiJobRepository>();
        services.AddSingleton<IMorningRepository, MorningRepository>();
        services.AddSingleton<IAiSettingsStore>(_ => new JsonAiSettingsStore(DbPaths.SettingsNextTo(dbPath)));
        // BoardService と AiJobService が同じ DbContext を使うので、直列化ゲートも 1 つ
        services.AddSingleton<OperationGate>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
