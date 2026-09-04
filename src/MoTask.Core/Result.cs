namespace MoTask.Core;

public class Result
{
    protected Result(bool isSuccess, string? error, IReadOnlyList<string> warnings)
    {
        IsSuccess = isSuccess;
        Error = error;
        Warnings = warnings;
    }

    public bool IsSuccess { get; }
    /// <summary>失敗時のユーザー向け理由。成功時は null。</summary>
    public string? Error { get; }
    /// <summary>成功時の警告（WIP 超過など）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    // params にしておくと Ok(string[]) が generic の Ok<T>(T) より優先される
    public static Result Ok(params string[] warnings) => new(true, null, warnings);

    public static Result Fail(string error) => new(false, error, Array.Empty<string>());

    public static Result<T> Ok<T>(T value, IReadOnlyList<string>? warnings = null)
        => new(true, value, null, warnings ?? Array.Empty<string>());

    public static Result<T> Fail<T>(string error) => new(false, default, error, Array.Empty<string>());
}

public sealed class Result<T> : Result
{
    internal Result(bool isSuccess, T? value, string? error, IReadOnlyList<string> warnings)
        : base(isSuccess, error, warnings)
    {
        Value = value;
    }

    public T? Value { get; }
}
