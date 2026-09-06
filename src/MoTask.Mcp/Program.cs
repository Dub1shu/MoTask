using System.Text;
using MoTask.Mcp;

// stdout は JSON-RPC 専用。BOM を付けない UTF-8 にし、行末で必ず流す。
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var host = new SystemAppHost();
using var sender = new HttpRpcSender();
var bridge = new StdioBridge(new EndpointResolver(host, sender), sender, Console.Error);

try
{
    await bridge.RunAsync(Console.In, Console.Out, cancellation.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C / Claude Code 側の終了。正常終了として扱う。
}
