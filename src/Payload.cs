using System.Text.Json;

namespace InfisicalPushBridge;

/// <summary>
/// Webhook ペイロードから「どの環境の・どのパスが変わったか」を取り出す。
///
/// 本物のイベントは { event, project: { workspaceId, environment, secretPath }, timestamp }。
/// UI のテスト送信などで形が違うことがあるので、**取れなければ null を返す**。
/// null は「絞り込めない」の意味で、呼び出し側は全 CR を対象にする
/// (余分にリコンサイルが走るだけで害が無い方向に倒す)。
/// </summary>
public static class Payload
{
    public static (string? Env, string? Path) ExtractScope(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            if (!doc.RootElement.TryGetProperty("project", out var project)) return (null, null);

            string? env = null;
            if (project.TryGetProperty("environment", out var e))
            {
                // 文字列(slug)の版と { slug: ... } の版の両方に備える
                env = e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.Object when e.TryGetProperty("slug", out var slug) => slug.GetString(),
                    _ => null
                };
            }

            string? path = null;
            if (project.TryGetProperty("secretPath", out var p) && p.ValueKind == JsonValueKind.String)
                path = p.GetString();

            return (env, path);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
