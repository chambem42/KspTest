# KspTest's game broker

KspTest's CI needs KSP itself, which comes from encrypted 7z volumes in a private repository's release
(`ci/Pack-KspGame.ps1`). The broker hands it to the CI of the accounts it trusts without their repositories holding any
secret: a test job asks GitHub for an OIDC token and sends it here; the broker checks GitHub signed it for this broker,
that it is current and that the repository belongs to an allowed account (`ALLOWED_OWNERS`), and answers with a GitHub
App installation token that may only read the game repository, for an hour, and the archives' key. The runner then
downloads and decrypts the release itself, as before; nothing large passes through the broker.

Anyone else uses KspTest's CI with a game of their own: their repository's `KSP_GAME_REPO` variable and
`KSP_GAME_TOKEN` and `KSP_GAME_KEY` secrets (see KspTest's README), which the setup action takes before asking here.

What it does not change: a job of an allowed account's repository gets the game, whatever code it runs. A fork's pull
request runs in its base repository's account, so the gate (KspTest's gate action: a maintainer's review and label)
is still what keeps strangers' code from it.

## Setting it up, once

1. **A GitHub App** (Settings > Developer settings > GitHub Apps > New): no webhook, permission Contents: Read-only,
   only on this account. Note its App ID; generate a private key (a .pem). Install it on the game repository alone
   (`chambem42/ksp-ci-assets`) and note the installation's ID (the end of the installation's settings URL).
2. **The Worker** (Cloudflare, free plan): fill `APP_ID` and `INSTALLATION_ID` in `wrangler.toml`, then from this folder
   ```
   npx wrangler secret put APP_PRIVATE_KEY < path/to/the-app.private-key.pem
   npx wrangler secret put ARCHIVE_KEY          (the archives' 7z password)
   npx wrangler deploy
   ```
   Its URL (`https://ksptest-game.chambem42.workers.dev`) is the reusable workflow's `broker` default.
3. **Callers**: a job calling the reusable workflow grants `id-token: write` (`permissions:`); that is all.

Adding an account: `ALLOWED_OWNERS` in `wrangler.toml`, then `npx wrangler deploy`. Revoking: take it out, or rotate the
App's key or the archives' key in one place. `WORKFLOW_PREFIXES` narrows it to given workflows (`job_workflow_ref`
prefixes) if ever wanted. `npx wrangler tail` shows each grant.
