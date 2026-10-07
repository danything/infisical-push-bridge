# infisical-push-bridge

Instant secret sync for self-hosted Infisical (free tier) — turns Infisical webhooks into
immediate `InfisicalSecret` reconciliation.

セルフホストの Infisical(無料)は [純正 Kubernetes operator](https://infisical.com/docs/integrations/platforms/kubernetes/overview) のポーリング同期しか使えない
(即時 push は Enterprise 限定で、サーバー側で `Event subscriptions are not available on your current plan` と拒否される)。
このブリッジは **無料側に残されている Webhook**(MIT 圏の機能)を受けて、変更されたパスに対応する
`InfisicalSecret` CR の注釈を書き換える。operator は CR の変更を watch しているので、
**ポーリング間隔を待たずにその場で同期**が走る。

```
Infisical で保存
  → Webhook が即発火
    → bridge が署名を検証し、パスが一致する InfisicalSecret に注釈を書く
      → operator が即リコンサイル → Secret 更新
        → auto-reload 注釈のある Deployment はローリング再起動
```

体感: 保存から Secret 反映まで数秒。ブリッジが落ちていても operator の
`resyncInterval` ポーリングに落ちるだけで、**壊れ方が安全**。

## 権限

ClusterRole は `infisicalsecrets` の get/list/patch だけ。**Secret の中身は読まない・読めない。**
受け口は `x-infisical-signature`(HMAC-SHA256)を固定時間比較で検証し、±15分のリプレイ窓を持つ。

`provision` を Kubernetes 認証で有効にしたときだけ、SA に `system:auth-delegator` が付く
(下の「フォルダと参照の自動作成」)。これも TokenReview / SubjectAccessReview を作れるだけで、Secret は読めない。

## インストール(Helm / OCI)

```sh
helm install infisical-push-bridge \
  oci://ghcr.io/danything/charts/infisical-push-bridge \
  --namespace infisical-push-bridge --create-namespace \
  --set infisicalSecret.enabled=true \
  --set infisicalSecret.hostAPI=http://infisical.<ns>.svc:8080/api \
  --set infisicalSecret.identityId=<machine identity id> \
  --set infisicalSecret.serviceAccountName=<SA> \
  --set infisicalSecret.serviceAccountNamespace=<SA ns> \
  --set infisicalSecret.projectSlug=<slug> \
  --set infisicalSecret.secretsPath=/infisical-push-bridge/infisical-push-bridge
```

k3s の helm-controller なら `HelmChart` CR で同じことができる(`valuesContent` に上記 values)。

## Infisical 側の設定

1. ブリッジの署名キーを作る: `openssl rand -hex 32`
2. Infisical のフォルダ(上の `secretsPath`)にキー名 **`webhook-secret`** で保存する
   (operator 経由でブリッジの `WEBHOOK_SECRET` になる)
3. プロジェクト → **Project Settings → Webhooks** → General で作成:
   - URL: `http://infisical-push-bridge.infisical-push-bridge.svc.cluster.local`
   - Environment: 対象環境(例 `prod`)
   - **Secret Path: `/**`** — Infisical の突き合わせは picomatch のグロブで、
     UI が既定で入れる `/` は「ルートそのもの」にしかマッチしない。
     全パスで発火させるにはグロブが必須(実測で確認済み)
   - Secret key: 1. で作った値
   - イベントは Secret Modified だけでよい

## 動作の判定

ペイロードから環境とパスが読めたら、`secretsScope` が一致する CR だけを叩く
(recursive な CR は配下の変更でも対象)。読めなかったら(テスト送信など)は
**全 CR を対象**にする — 余分にリコンサイルが走るだけで害の無い方向に倒してある。

## フォルダと参照の自動作成(provision)

各リポジトリの `InfisicalSecret` には `secretsPath` が書いてあるので、**Infisical 側のフォルダは自動で作る。**
ほかのフォルダの値を引き継ぐキー(Entra 共用のクライアントシークレットなど)も、注釈で書いておけば
**参照として自動で入れる**。既定は無効(`provision.enabled: false`)。

```yaml
apiVersion: secrets.infisical.com/v1alpha1
kind: InfisicalSecret
metadata:
  name: matrix
  namespace: matrix
  annotations:
    # キー=/参照元のフォルダ/参照元のキー をカンマか改行で並べる
    push-bridge.doany.io/references: >-
      oidc-client-secret=/shared/entra/client-secret,
      smtp-password=/shared/smtp/password
spec:
  authentication:
    kubernetesAuth:
      secretsScope:
        projectSlug: doa
        envSlug: prod
        secretsPath: /matrix/matrix
```

これで `prod` の `/matrix/matrix` に

| キー | 値 |
| --- | --- |
| `oidc-client-secret` | `${prod.shared.entra.client-secret}` |
| `smtp-password` | `${prod.shared.smtp.password}` |

が入る。値は Infisical の**参照**なので、`/shared` 側をローテーションすれば追従する
(展開は operator が同期のときにやる)。環境は CR の `envSlug` と同じ。

### やること・やらないこと

- `secretsPath` のフォルダが無ければ、上から順に作る(`/a/b/c` なら `/a` → `/a/b` → `/a/b/c`)
- 注釈のキーのうち、**フォルダにまだ無いものだけ**を作る。**既にあるキーは決して書き換えない**
  (手で値を入れ直したキーもそのまま)。Infisical の作成 API 自体も、既にあるキーは拒否する
- **更新と削除は一切しない。** 注釈から項目を消しても、作ったキーは残る
- 一覧は `viewSecretValue=false` で取り、**値は読まない**(キー名だけ)。値はログにも出さない
- 別の `projectSlug` を指す CR は触らない
- 失敗(権限不足・名前の誤りなど)はログに出して次の CR へ進む

### いつ動くか

`InfisicalSecret` の一覧を `pollSeconds`(既定 30 秒)ごとに見て、**新しい CR・パスや注釈が変わった CR** を処理する。
`resyncSeconds`(既定 10 分)ごとに全部をやり直す(手で消したキーの補充、権限が後から付いた場合の再試行)。
何か作ったら CR に `push-bridge.doany.io/event-at` を書くので、operator はその場で同期する。
watch ではなく list のポーリングなのは、既存の get/list 権限で足りるから。

### 注釈の書き方

```
<キー>=/<フォルダ>/…/<参照元のキー>[, <キー>=…]
```

- 区切りはカンマか改行。前後の空白は無視
- `<キー>` は英数字と `-` `_` `.`
- 参照元のフォルダ名と参照元のキーは英数字と `-` `_` だけ
  (Infisical の参照 `${env.a.b.KEY}` はドットで区切るので、ドット入りの名前は参照できない)
- 書き間違えた項目は飛ばしてログに出す。ほかの項目は作る

### Infisical 側の準備(マシンアイデンティティ)

operator のアイデンティティは読み取り用なので**広げない**。ブリッジ専用のものを作る。

1. **Project Roles にカスタムロールを作る**(例: `push-bridge-provisioner`)。権限は次の 2 つだけ:
   - **Secret Folders**: `Create`、条件 `Environment` = `prod`
   - **Secrets**: `Describe Secret` と `Create`、条件 `Environment` = `prod`
     (`Read Value`・`Edit`・`Delete` は付けない。`Describe Secret` はキー名の一覧と、
     参照元 `/shared/...` の存在確認に使う。Infisical は参照を含む値を作るとき、
     参照先に `Describe Secret` があるかを確かめる)
2. **Organization → Access Control → Identities** でアイデンティティを作る(例: `infisical-push-bridge`)。
   組織ロールは `No Access` でよい
3. 認証方式に **Kubernetes Auth** を付ける:
   - Kubernetes Host: `https://kubernetes.default.svc`(Infisical はクラスタ内)
   - Token Reviewer JWT: 空(ブリッジのトークン自身でレビューする。だから chart が SA に `system:auth-delegator` を付ける)
   - Allowed Service Account Names: `infisical-push-bridge`
   - Allowed Namespaces: `infisical-push-bridge`
   - Allowed Audience: 空(Pod の既定トークンを使う)
   - CA Certificate: クラスタの CA(operator のアイデンティティと同じ)
4. プロジェクトの **Access Control → Machine Identities** にこのアイデンティティを足し、ロールは 1. のカスタムロール
5. chart の値:

```yaml
provision:
  enabled: true
  apiURL: http://infisical.<ns>.svc:8080/api
  projectId: <プロジェクト ID>
  projectSlug: <slug>
  auth:
    method: kubernetes
    identityId: <上で作ったアイデンティティの ID>
```

Universal Auth にする場合は `auth.method: universal` にし、`client-id` / `client-secret` を
`existingSecret`(または `auth.universalSecret`)に入れる。この場合 `system:auth-delegator` は付かない。
