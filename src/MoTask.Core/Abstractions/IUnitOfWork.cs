namespace MoTask.Core.Abstractions;

public interface IUnitOfWork
{
    /// <summary>
    /// 溜まった変更を1トランザクションで書く。失敗時は PersistenceException を投げ、
    /// 実装側は未保存の変更を破棄した状態（追跡クリア）にしておく。
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
