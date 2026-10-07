namespace InfisicalPushBridge;

/// <summary>
/// InfisicalSecret を定期的に list し、新しく現れた CR・中身(パスや注釈)の変わった CR を
/// <see cref="Provisioner"/> に渡す。<c>resync</c> ごとに全 CR をやり直す(手で消したキーの補充や、
/// 権限が後から付いた場合の再試行もこれで拾う)。
///
/// watch ではなく list のポーリングにしているのは、既存の get/list 権限だけで足りるから。
/// list は k8s API に対してだけで、Infisical を叩くのは変化のあった CR と resync のときだけ。
/// </summary>
public sealed class ProvisionLoop(KubeClient kube, Provisioner provisioner, TimeSpan poll, TimeSpan resync)
    : BackgroundService
{
    readonly Dictionary<(string, string), Target> _seen = new();
    DateTimeOffset _lastFull = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Console.WriteLine($"[provision] 有効(poll {poll.TotalSeconds}s / resync {resync.TotalSeconds}s)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[provision] InfisicalSecret の一覧を取れなかった: {ex.Message}");
            }

            try
            {
                await Task.Delay(poll, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task Tick()
    {
        var all = await kube.ListInfisicalSecrets();
        var now = DateTimeOffset.UtcNow;
        var full = now - _lastFull >= resync;
        if (full) _lastFull = now;

        foreach (var t in SelectDue(all, _seen, full))
        {
            var result = await provisioner.Reconcile(t);
            if (!result.Changed) continue;
            try
            {
                // 作ったら operator にその場で同期させる(resyncInterval を待たない)
                await kube.Annotate(t.Namespace, t.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[provision] {t.Namespace}/{t.Name}: 注釈を書けなかった(operator のポーリングで追いつく): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 今回処理する CR を選び、<paramref name="seen"/> を今の状態に更新する。
    /// <paramref name="full"/> なら全部、そうでなければ新顔と中身の変わったものだけ。消えた CR は忘れる。
    /// event-at 注釈(ブリッジ自身が書く)は <see cref="Target"/> に入っていないので、自分の書き込みでは再処理しない。
    /// </summary>
    public static List<Target> SelectDue(IReadOnlyList<Target> all, Dictionary<(string, string), Target> seen, bool full)
    {
        var due = new List<Target>();
        var present = new HashSet<(string, string)>();
        foreach (var t in all)
        {
            var key = (t.Namespace, t.Name);
            present.Add(key);
            if (full || !seen.TryGetValue(key, out var prev) || prev != t) due.Add(t);
            seen[key] = t;
        }
        foreach (var gone in seen.Keys.Where(k => !present.Contains(k)).ToList()) seen.Remove(gone);
        return due;
    }
}
