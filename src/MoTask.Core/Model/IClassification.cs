namespace MoTask.Core.Model;

/// <summary>プロジェクトとラベルに共通する、並べ替えに要る分だけの形。</summary>
public interface IClassification
{
    int Id { get; }
    string Name { get; }
    bool Archived { get; }
    /// <summary>表示順。小さいほど上。並んだときは名前、さらに Id で決める（<see cref="ClassificationOrder"/>）。</summary>
    int Order { get; set; }
}
