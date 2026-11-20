// KspTest's game broker (a Cloudflare Worker): hands KspTest's CI the game (README.md) without the calling repository
// holding any secret. A job sends its GitHub Actions OIDC token (Authorization: Bearer); the broker checks GitHub
// signed it for this broker (aud), that it is current, and that the repository's owner is allowed (ALLOWED_OWNERS),
// optionally that the job runs one of the allowed workflows (WORKFLOW_PREFIXES); then answers with a GitHub App
// installation token that may only read the game repository (ASSETS_REPO), valid for an hour, and the archives' key.
// Nothing large passes through it: the runner downloads and decrypts the release itself.
//
// Secrets (wrangler secret put): APP_PRIVATE_KEY (the App's .pem, PKCS#1 as GitHub gives it, or PKCS#8), ARCHIVE_KEY.
// Vars (wrangler.toml): APP_ID, INSTALLATION_ID, ASSETS_REPO (owner/name), ALLOWED_OWNERS (comma separated logins),
// AUDIENCE (the aud the jobs ask for), WORKFLOW_PREFIXES (comma separated job_workflow_ref prefixes; empty: any
// workflow of an allowed owner).

const ISSUER = "https://token.actions.githubusercontent.com";
const JWKS_URL = `${ISSUER}/.well-known/jwks`;

export default {
  async fetch(request, env) {
    if (request.method !== "POST") return json({ error: "POST with Authorization: Bearer <GitHub Actions OIDC token>" }, 405);
    const auth = request.headers.get("Authorization") || "";
    const m = auth.match(/^Bearer\s+(.+)$/i);
    if (!m) return json({ error: "no bearer token" }, 401);

    let claims;
    try {
      claims = await verifyGithubOidc(m[1], env.AUDIENCE);
    } catch (e) {
      return json({ error: `token refused: ${e.message}` }, 401);
    }

    const owners = list(env.ALLOWED_OWNERS).map((o) => o.toLowerCase());
    if (!owners.includes(String(claims.repository_owner || "").toLowerCase()))
      return json({ error: `${claims.repository} is not in an allowed account (${owners.join(", ")})` }, 403);
    const prefixes = list(env.WORKFLOW_PREFIXES);
    if (prefixes.length && !prefixes.some((p) => String(claims.job_workflow_ref || "").startsWith(p)))
      return json({ error: `${claims.job_workflow_ref} is not an allowed workflow` }, 403);

    let grant;
    try {
      grant = await installationToken(env);
    } catch (e) {
      return json({ error: `no game repository token: ${e.message}` }, 502);
    }
    console.log(`granted ${claims.repository} (${claims.event_name}, ${claims.job_workflow_ref}, run ${claims.run_id})`);
    return json({ repo: env.ASSETS_REPO, token: grant.token, expires_at: grant.expires_at, key: env.ARCHIVE_KEY });
  },
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function list(s) {
  return String(s || "").split(",").map((x) => x.trim()).filter(Boolean);
}

// --- GitHub's OIDC token -----------------------------------------------------------------------------------------------

let jwksCache = { at: 0, keys: [] };

async function githubKeys(force) {
  if (!force && Date.now() - jwksCache.at < 3600_000 && jwksCache.keys.length) return jwksCache.keys;
  const r = await fetch(JWKS_URL, { headers: { "user-agent": "ksptest-broker" } });
  if (!r.ok) throw new Error(`JWKS ${r.status}`);
  jwksCache = { at: Date.now(), keys: (await r.json()).keys || [] };
  return jwksCache.keys;
}

async function verifyGithubOidc(token, audience) {
  const parts = token.split(".");
  if (parts.length !== 3) throw new Error("not a JWT");
  const header = JSON.parse(text(b64url(parts[0])));
  const claims = JSON.parse(text(b64url(parts[1])));
  if (header.alg !== "RS256") throw new Error(`alg ${header.alg}`);

  let jwk = (await githubKeys(false)).find((k) => k.kid === header.kid);
  if (!jwk) jwk = (await githubKeys(true)).find((k) => k.kid === header.kid);
  if (!jwk) throw new Error("unknown signing key");
  const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
  const ok = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, b64url(parts[2]),
                                        new TextEncoder().encode(`${parts[0]}.${parts[1]}`));
  if (!ok) throw new Error("bad signature");

  const now = Math.floor(Date.now() / 1000);
  if (claims.iss !== ISSUER) throw new Error(`issuer ${claims.iss}`);
  const auds = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (!audience || !auds.includes(audience)) throw new Error(`audience ${claims.aud}`);
  if (typeof claims.exp !== "number" || claims.exp < now - 30) throw new Error("expired");
  if (typeof claims.nbf === "number" && claims.nbf > now + 30) throw new Error("not yet valid");
  return claims;
}

// --- the GitHub App's installation token, read-only on the game repository ------------------------------------------

async function installationToken(env) {
  const appJwt = await appJwtFor(env.APP_ID, env.APP_PRIVATE_KEY);
  const repo = String(env.ASSETS_REPO).split("/")[1];
  const r = await fetch(`https://api.github.com/app/installations/${env.INSTALLATION_ID}/access_tokens`, {
    method: "POST",
    headers: {
      authorization: `Bearer ${appJwt}`,
      accept: "application/vnd.github+json",
      "x-github-api-version": "2022-11-28",
      "user-agent": "ksptest-broker",
    },
    body: JSON.stringify({ repositories: [repo], permissions: { contents: "read" } }),
  });
  if (!r.ok) throw new Error(`GitHub ${r.status}: ${(await r.text()).slice(0, 200)}`);
  return r.json();
}

async function appJwtFor(appId, pem) {
  const now = Math.floor(Date.now() / 1000);
  const enc = (o) => b64urlEncode(new TextEncoder().encode(JSON.stringify(o)));
  const signing = `${enc({ alg: "RS256", typ: "JWT" })}.${enc({ iat: now - 60, exp: now + 540, iss: String(appId) })}`;
  const key = await crypto.subtle.importKey("pkcs8", pkcs8(pem), { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(signing));
  return `${signing}.${b64urlEncode(new Uint8Array(sig))}`;
}

// a PEM private key as PKCS#8 DER: GitHub's App keys are PKCS#1 ("RSA PRIVATE KEY"), which WebCrypto does not import,
// so wrapped in PKCS#8's PrivateKeyInfo (version 0, rsaEncryption, the key as an OCTET STRING)
function pkcs8(pem) {
  const der = Uint8Array.from(atob(pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "")), (c) => c.charCodeAt(0));
  if (!/BEGIN RSA PRIVATE KEY/.test(pem)) return der.buffer;
  const algo = [0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00];
  const octet = [0x04, ...derLength(der.length), ...der];
  const body = [0x02, 0x01, 0x00, ...algo, ...octet];
  return new Uint8Array([0x30, ...derLength(body.length), ...body]).buffer;
}

function derLength(n) {
  if (n < 0x80) return [n];
  const bytes = [];
  for (let x = n; x > 0; x >>= 8) bytes.unshift(x & 0xff);
  return [0x80 | bytes.length, ...bytes];
}

function b64url(s) {
  const b = s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4);
  return Uint8Array.from(atob(b), (c) => c.charCodeAt(0));
}

function b64urlEncode(bytes) {
  let s = "";
  for (const x of bytes) s += String.fromCharCode(x);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function text(bytes) {
  return new TextDecoder().decode(bytes);
}
