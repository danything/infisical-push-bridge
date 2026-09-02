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
