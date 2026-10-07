namespace InfisicalPushBridge;

/// <summary>1 つの CR を処理した結果。ログと「operator に即同期させるか」の判断に使う。</summary>
public sealed record ProvisionResult(int FoldersCreated, List<string> KeysCreated, List<string> Problems)
{
    public bool Changed => FoldersCreated > 0 || KeysCreated.Count > 0;
}

/// <summary>
/// 各リポジトリの InfisicalSecret を見て、Infisical 側に足りないものを作る。
///
/// 1. <c>secretsScope.secretsPath</c> のフォルダが無ければ、上から順に作る
/// 2. <see cref="Provisioning.ReferencesAnnotation"/> 注釈に書かれたキーのうち、
///    **フォルダにまだ無いものだけ** を、参照(<c>${prod.shared.entra.client-secret}</c>)として作る
///
/// **作るだけで、更新も削除もしない。** 失敗はログに出して次へ進む(1 件の失敗で全体を止めない)。
/// </summary>
public sealed class Provisioner(IInfisical infisical, string projectSlug, Action<string> log)
{
    public const string Comment = "infisical-push-bridge が references 注釈から作成(値は参照なので元の値に追従する)";

    public async Task<ProvisionResult> Reconcile(Target t)
    {
        var result = await ReconcileCore(t);
        if (result.Changed)
            log($"[provision] {t.Namespace}/{t.Name}: {t.EnvSlug}:{Matching.Normalize(t.SecretsPath)} フォルダ {result.FoldersCreated} 個・キー {result.KeysCreated.Count} 個を作成" +
                (result.KeysCreated.Count > 0 ? $" ({string.Join(", ", result.KeysCreated)})" : ""));
        foreach (var p in result.Problems) log($"[provision] {p}");
        return result;
    }

    async Task<ProvisionResult> ReconcileCore(Target t)
    {
        var result = new ProvisionResult(0, [], []);
        var id = $"{t.Namespace}/{t.Name}";

        // 別のプロジェクトを指す CR は触らない(ブリッジのアイデンティティはこのプロジェクトのためのもの)
        if (!string.Equals(t.ProjectSlug, projectSlug, StringComparison.Ordinal)) return result;

        if (!Provisioning.IsSegment(t.EnvSlug))
        {
            result.Problems.Add($"{id}: envSlug '{t.EnvSlug}' が読めない");
            return result;
        }

        var segments = Provisioning.SplitPath(t.SecretsPath);
        if (segments is null)
        {
            result.Problems.Add($"{id}: secretsPath '{t.SecretsPath}' にフォルダ名に使えない文字がある(英数字と - _ だけ)");
            return result;
        }
        var folder = "/" + string.Join('/', segments);

        var created = 0;
        try
        {
            created = await EnsureFolder(t.EnvSlug, segments);
        }
        catch (Exception ex)
        {
            result.Problems.Add($"{id}: フォルダ {folder} を用意できなかった: {ex.Message}");
            return result;
        }
        result = result with { FoldersCreated = created };

        var (references, errors) = Provisioning.ParseReferences(t.References);
        foreach (var e in errors) result.Problems.Add($"{id}: 注釈 {Provisioning.ReferencesAnnotation}: {e}");
        if (references.Count == 0) return result;

        IReadOnlySet<string> existing;
        try
        {
            existing = await infisical.ListSecretKeys(t.EnvSlug, folder);
        }
        catch (Exception ex)
        {
            result.Problems.Add($"{id}: {folder} のキー一覧を取れなかった: {ex.Message}");
            return result;
        }

        foreach (var (key, value) in Provisioning.PlanMissing(references, existing, t.EnvSlug, folder))
        {
            try
            {
                await infisical.CreateSecret(t.EnvSlug, folder, key, value, Comment);
                result.KeysCreated.Add(key);
            }
            catch (Exception ex)
            {
                // 値(参照の文字列)は出さない。キー名だけ
                result.Problems.Add($"{id}: {folder} にキー {key} を作れなかった: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>上から順に、無い段だけ作る。作った数を返す。</summary>
    async Task<int> EnsureFolder(string env, string[] segments)
    {
        var created = 0;
        var parentMissing = false;
        foreach (var (parent, name) in Provisioning.FolderChain(segments))
        {
            // 親を今作ったばかりなら、その下が空なのは分かっているので一覧は取らない
            if (!parentMissing && (await infisical.ListFolderNames(env, parent)).Contains(name)) continue;
            await infisical.CreateFolder(env, parent, name);
            parentMissing = true;
            created++;
        }
        return created;
    }
}
