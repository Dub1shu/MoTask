using System.Collections.Concurrent;
using System.IO;
using System.Text;
using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.App.Ai;

/// <summary>
/// events.jsonl を追う（仕様 §5）。フックが追記するだけのファイルなので、最後に読んだバイト位置から
/// 差分を読み直すのが本体で、通知は要らない。FileSystemWatcher は追記の通知を取りこぼす／重複させる
/// うえ、結局この読み直しが必要になるので使わない。小さなファイル 1 本を 500 ms ごとに見るだけ。
/// </summary>
public sealed class JobEventWatcher : IJobEventSource, IDisposable
{
    private sealed class Follower
    {
        public required JobEventSubscription Subscription { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<int, Follower> _followers = new();
    private readonly TimeSpan _pollInterval;
    private bool _disposed;

    public JobEventWatcher() : this(TimeSpan.FromMilliseconds(500))
    {
    }

    internal JobEventWatcher(TimeSpan pollInterval)
    {
        _pollInterval = pollInterval;
    }

    public void Follow(JobEventSubscription subscription)
    {
        if (_disposed) return;
        StopFollowing(subscription.JobId);
        var follower = new Follower { Subscription = subscription, Cancellation = new CancellationTokenSource() };
        _followers[subscription.JobId] = follower;
        _ = Task.Run(() => LoopAsync(follower));
    }

    public void StopFollowing(int jobId)
    {
        if (!_followers.TryRemove(jobId, out var follower)) return;
        try
        {
            follower.Cancellation.Cancel();
        }
        catch (Exception)
        {
            // 追従をやめるだけ。止め方の失敗で呼び出し側を落とさない。
        }
    }

    private async Task LoopAsync(Follower follower)
    {
        var subscription = follower.Subscription;
        var token = follower.Cancellation.Token;
        long offset = 0;
        var remaining = subscription.SkipLines;
        var seenTheFile = false;
        var partial = "";
        // フックは 4096 バイトの書き込みバッファを持つ FileStream で追記するので、こちらが読む
        // タイミング次第でマルチバイト文字の途中までしか書かれていないことがある。Decoder を
        // 追従の間ずっと使い回すことで、未完成の末尾バイト列を内部状態として次の周回まで
        // 持ち越す（flush: false）。周回ごとに新しい StreamReader を作ると、その未完成バイト列が
        // 毎回 U+FFFD に化けたうえで読み飛ばされ、文字が永久に失われる。
        var decoder = Utf8.GetDecoder();

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var info = new FileInfo(subscription.EventsPath);
                if (!info.Exists)
                {
                    // まだ 1 行も書かれていないだけなら待つ。一度見えていたのに消えたのは事故（仕様 §12）。
                    if (!seenTheFile) continue;
                    await ReportAsync(subscription).ConfigureAwait(false);
                    return;
                }
                seenTheFile = true;
                // 追記専用のはずのファイルが縮んだ＝作り直された。同じ扱いで追従をやめる。
                if (info.Length < offset)
                {
                    await ReportAsync(subscription).ConfigureAwait(false);
                    return;
                }
                if (info.Length == offset) continue;

                string text;
                try
                {
                    using var stream = new FileStream(subscription.EventsPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(offset, SeekOrigin.Begin);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
                    offset = stream.Position;
                    var bytes = buffer.GetBuffer();
                    var byteCount = (int)buffer.Length;
                    // UTF-8 の文字数はバイト数以下なので、この大きさのバッファで必ず足りる。
                    var chars = new char[byteCount];
                    var charCount = decoder.GetChars(bytes, 0, byteCount, chars, 0, flush: false);
                    text = new string(chars, 0, charCount);
                }
                catch (IOException)
                {
                    continue; // フックが書いている最中。次の周回で読み直す。
                }

                // 末尾の改行までが「完成した行」。途中まで書かれた行は次の周回へ持ち越す。
                partial += text;
                var lastBreak = partial.LastIndexOf('\n');
                if (lastBreak < 0) continue;
                var complete = partial[..lastBreak];
                partial = partial[(lastBreak + 1)..];

                foreach (var raw in complete.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (remaining > 0)
                    {
                        remaining--;
                        continue;
                    }
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        await subscription.OnLine(line).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 1 行の取り込み失敗で追従を止めない（取り込み側が自分で警告を出す）
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopFollowing / Dispose
        }
        catch (Exception ex)
        {
            // IOException 以外の予期しない例外（UnauthorizedAccessException 等）。
            // ここで飲み込まずに Task.Run が未観測の失敗タスクとして終わると、
            // 購読側に「追従が止まった」ことがまったく伝わらなくなる。
            await ReportAsync(subscription,
                string.Format(Messages.EventsWatchFailedFormat, subscription.EventsPath, ex.Message))
                .ConfigureAwait(false);
        }
    }

    private static Task ReportAsync(JobEventSubscription subscription)
        => ReportAsync(subscription, string.Format(Messages.EventsFileGoneFormat, subscription.EventsPath));

    private static async Task ReportAsync(JobEventSubscription subscription, string message)
    {
        try
        {
            await subscription.OnProblem(message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 通知が失敗しても、この追従はもう終わっている
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var jobId in _followers.Keys.ToList()) StopFollowing(jobId);
    }
}
