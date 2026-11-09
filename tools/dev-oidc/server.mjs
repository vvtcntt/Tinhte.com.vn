// IdP OIDC GIẢ cho máy dev — CHỈ DÙNG LOCAL, KHÔNG BAO GIỜ triển khai lên staging/production.
// Dự án chưa chọn IdP thật (ADR-0004); server này giúp chạy đăng nhập CMS đầu-cuối trên máy dev:
//   node tools/dev-oidc/server.mjs            (http://127.0.0.1:5090, không cần npm install)
// API (Development) cấu hình:
//   Auth__Staff__Authority=http://127.0.0.1:5090  Auth__Staff__ClientId=tinhte-cms-dev
//   Auth__Staff__ClientSecret=dev-only-secret     Auth__Staff__RequireHttpsMetadata=false
// Hỗ trợ: discovery, JWKS (RS256, khóa sinh mới mỗi lần chạy), authorization code + PKCE S256, trang chọn danh tính,
// tùy chọn MFA (amr = ["pwd","mfa"]) hoặc chỉ mật khẩu (["pwd"]), nút từ chối (error=access_denied).
// Chỉ nghe 127.0.0.1 và chỉ chấp nhận redirect_uri về localhost / *.localhost.
import { createServer } from 'node:http';
import { createHash, generateKeyPairSync, randomBytes, sign } from 'node:crypto';

const PORT = Number(process.env.DEV_OIDC_PORT ?? 5090);
const ISSUER = `http://127.0.0.1:${PORT}`;
const CLIENT_ID = process.env.DEV_OIDC_CLIENT_ID ?? 'tinhte-cms-dev';
const CLIENT_SECRET = process.env.DEV_OIDC_CLIENT_SECRET ?? 'dev-only-secret'; // giá trị dev công khai, không phải bí mật

const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const KID = randomBytes(6).toString('hex');
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: KID, use: 'sig', alg: 'RS256' };
const codes = new Map(); // code → { subject, name, nonce, challenge, mfa, redirectUri, expires }

const b64url = (buf) => Buffer.from(buf).toString('base64url');
const escapeHtml = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

function signJwt(claims) {
  const header = b64url(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: KID }));
  const payload = b64url(JSON.stringify(claims));
  return `${header}.${payload}.${b64url(sign('RSA-SHA256', Buffer.from(`${header}.${payload}`), privateKey))}`;
}

function allowedRedirect(uri) {
  try {
    const u = new URL(uri);
    return (u.protocol === 'http:' || u.protocol === 'https:')
      && (u.hostname === 'localhost' || u.hostname === '127.0.0.1' || u.hostname.endsWith('.localhost'));
  } catch {
    return false;
  }
}

function send(res, status, body, headers = {}) {
  const isJson = typeof body !== 'string';
  res.writeHead(status, { 'content-type': isJson ? 'application/json' : 'text/html; charset=utf-8', 'cache-control': 'no-store', ...headers });
  res.end(isJson ? JSON.stringify(body) : body);
}

async function readForm(req) {
  let raw = '';
  for await (const chunk of req) raw += chunk;
  return Object.fromEntries(new URLSearchParams(raw));
}

function authorizePage(params, error) {
  const hidden = ['client_id', 'redirect_uri', 'state', 'nonce', 'code_challenge', 'code_challenge_method', 'response_type', 'scope']
    .map((k) => `<input type="hidden" name="${k}" value="${escapeHtml(params[k])}">`).join('');
  return `<!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Dev IdP — Tinhte</title><style>
body{font:15px/1.5 system-ui,sans-serif;background:#f4f4f5;color:#18181b;display:grid;place-items:center;min-height:100vh;margin:0}
form{background:#fff;padding:28px;border-radius:12px;box-shadow:0 1px 3px #0002;width:min(380px,calc(100vw - 32px));box-sizing:border-box}
h1{font-size:18px;margin:0 0 4px}p{color:#71717a;margin:0 0 20px;font-size:13px}label{display:block;margin:12px 0 4px;font-weight:600;font-size:13px}
input[type=text]{width:100%;box-sizing:border-box;padding:8px 10px;border:1px solid #d4d4d8;border-radius:8px;font:inherit}
.row{display:flex;gap:8px;align-items:center;margin-top:14px}.row label{margin:0;font-weight:400}
.actions{display:flex;gap:8px;margin-top:22px}button{flex:1;padding:9px;border-radius:8px;border:1px solid #d4d4d8;background:#fff;font:inherit;cursor:pointer}
button.primary{background:#18181b;color:#fff;border-color:#18181b}.warn{background:#fef3c7;color:#92400e;padding:8px 10px;border-radius:8px;font-size:12px}
.err{background:#fee2e2;color:#991b1b;padding:8px 10px;border-radius:8px;font-size:13px;margin-bottom:12px}</style></head><body>
<form method="post" action="/authorize">${hidden}
<h1>Dev IdP</h1><p>IdP giả cho máy dev. Không dùng thật.</p>
${error ? `<div class="err">${escapeHtml(error)}</div>` : ''}
<label for="sub">Subject (sub)</label><input type="text" id="sub" name="sub" value="dev-admin" required pattern="[A-Za-z0-9._@-]{1,100}">
<label for="name">Tên hiển thị</label><input type="text" id="name" name="name" value="Dev Admin">
<div class="row"><input type="checkbox" id="mfa" name="mfa" value="1" checked><label for="mfa">Đã xác thực MFA (amr: mfa)</label></div>
<div class="actions"><button type="submit" name="decision" value="deny">Từ chối</button><button class="primary" type="submit" name="decision" value="allow">Đăng nhập</button></div>
<p class="warn" style="margin-top:16px">Cấp vai trò trước: <code>Tinhte.DbMigrator grant-role --issuer ${ISSUER} --subject &lt;sub&gt; …</code></p>
</form></body></html>`;
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, ISSUER);
  try {
    if (req.method === 'GET' && url.pathname === '/.well-known/openid-configuration') {
      return send(res, 200, {
        issuer: ISSUER,
        authorization_endpoint: `${ISSUER}/authorize`,
        token_endpoint: `${ISSUER}/token`,
        jwks_uri: `${ISSUER}/jwks`,
        response_types_supported: ['code'],
        subject_types_supported: ['public'],
        id_token_signing_alg_values_supported: ['RS256'],
        code_challenge_methods_supported: ['S256'],
        token_endpoint_auth_methods_supported: ['client_secret_post', 'client_secret_basic'],
        scopes_supported: ['openid', 'profile', 'email'],
      });
    }

    if (req.method === 'GET' && url.pathname === '/jwks') return send(res, 200, { keys: [jwk] });

    if (url.pathname === '/authorize') {
      const params = req.method === 'POST' ? await readForm(req) : Object.fromEntries(url.searchParams);
      if (params.client_id !== CLIENT_ID || params.response_type !== 'code' || !allowedRedirect(params.redirect_uri)) {
        return send(res, 400, '<p>Bad authorize request (client_id, response_type=code, redirect_uri localhost).</p>');
      }

      if (params.code_challenge_method !== 'S256' || !params.code_challenge) {
        return send(res, 400, '<p>PKCE S256 is required.</p>');
      }

      if (req.method === 'GET') return send(res, 200, authorizePage(params));

      const back = new URL(params.redirect_uri);
      back.searchParams.set('state', params.state ?? '');
      if (params.decision !== 'allow') {
        back.searchParams.set('error', 'access_denied');
        return send(res, 302, '', { location: back.toString() });
      }

      if (!/^[A-Za-z0-9._@-]{1,100}$/.test(params.sub ?? '')) return send(res, 400, authorizePage(params, 'Subject không hợp lệ.'));
      const code = randomBytes(16).toString('hex');
      codes.set(code, {
        subject: params.sub, name: (params.name || params.sub).slice(0, 80), nonce: params.nonce, challenge: params.code_challenge,
        mfa: params.mfa === '1', redirectUri: params.redirect_uri, expires: Date.now() + 60_000,
      });
      back.searchParams.set('code', code);
      return send(res, 302, '', { location: back.toString() });
    }

    if (req.method === 'POST' && url.pathname === '/token') {
      const form = await readForm(req);
      let clientId = form.client_id;
      let secret = form.client_secret;
      const basic = req.headers.authorization?.match(/^Basic (.+)$/i);
      if (basic) [clientId, secret] = Buffer.from(basic[1], 'base64').toString().split(':').map(decodeURIComponent);
      if (clientId !== CLIENT_ID || secret !== CLIENT_SECRET) return send(res, 401, { error: 'invalid_client' });

      const pending = codes.get(form.code);
      codes.delete(form.code);
      if (form.grant_type !== 'authorization_code' || !pending || pending.expires < Date.now() || pending.redirectUri !== form.redirect_uri) {
        return send(res, 400, { error: 'invalid_grant' });
      }

      if (b64url(createHash('sha256').update(form.code_verifier ?? '').digest()) !== pending.challenge) {
        return send(res, 400, { error: 'invalid_grant', error_description: 'PKCE verification failed' });
      }

      const now = Math.floor(Date.now() / 1000);
      const accessToken = randomBytes(16).toString('hex');
      const idToken = signJwt({
        iss: ISSUER, aud: CLIENT_ID, sub: pending.subject, name: pending.name, nonce: pending.nonce,
        iat: now, nbf: now, exp: now + 300, auth_time: now,
        amr: pending.mfa ? ['pwd', 'mfa'] : ['pwd'],
        at_hash: b64url(createHash('sha256').update(accessToken).digest().subarray(0, 16)),
      });
      return send(res, 200, { access_token: accessToken, token_type: 'Bearer', expires_in: 300, id_token: idToken });
    }

    return send(res, 404, { error: 'not_found' });
  } catch (e) {
    console.error(e);
    return send(res, 500, { error: 'server_error' });
  }
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`dev-oidc (DEV ONLY) listening on ${ISSUER} — client_id=${CLIENT_ID}`);
});
