using System.Windows;
using MoTask.App.ViewModels;
using MoTask.App.Views;
using MoTask.Core.Ai;
using MoTask.Core.Model;

namespace MoTask.App.Ai;

/// <summary>
/// AskHuman のときに WPF のダイアログを出す。承認要求はワーカースレッドから来るので Dispatcher へ載せ替える。
/// タイムアウトはしない。ct（停止・終了）が取り消されたらダイアログを閉じて OperationCanceledException。
/// 複数ジョブが同時に聞いてきたらモーダルが重なるが、どれも答えられる。
/// </summary>
public sealed class WpfPermissionPrompt : IPermissionPrompt
{
    public async Task<HumanDecision> AskAsync(PermissionPromptContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var app = Application.Current ?? throw new InvalidOperationException("WPF アプリケーションが無い");

        HumanDecision? decision;
        try
        {
            decision = await app.Dispatcher.InvokeAsync(() =>
            {
                var vm = new PermissionDialogViewModel(context);
                var dialog = new PermissionDialog { DataContext = vm };
                if (app.MainWindow is { IsLoaded: true } owner) dialog.Owner = owner;
                using var closeOnCancel = ct.Register(() => dialog.Dispatcher.InvokeAsync(dialog.Close));
                dialog.ShowDialog();
                return vm.Decision;
            }, System.Windows.Threading.DispatcherPriority.Normal, ct);
        }
        catch (TaskCanceledException)
        {
            // Dispatcher が畳まれた（アプリ終了）
            throw new OperationCanceledException(ct);
        }

        ct.ThrowIfCancellationRequested();
        // × で閉じた: 拒否として扱い、記憶はしない
        return decision ?? new HumanDecision(RuleDecision.Deny, false, RuleScope.Global);
    }
}
