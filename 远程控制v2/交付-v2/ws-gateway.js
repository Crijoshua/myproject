// R2 ttyd 网关（ws-gateway.js）
// 背景：iOS Safari 不在 WebSocket 升级请求上携带 Basic Auth 凭据，导致 ttyd -c 在
// HTTP 层直接拒绝（"User code denied connection"）。本网关：
//   1. 对所有普通请求校验 Basic Auth（无凭据 → 401，R2-A3 语义保持）；
//   2. 首次认证成功后签发会话 Cookie（HttpOnly、随机 128 位）；
//   3. WebSocket 升级请求接受「Authorization 头 或 会话 Cookie」任一凭证，
//      通过后向 ttyd 注入正确的 Authorization 头再桥接（Safari 靠 Cookie 走通）。
// 仅监听 127.0.0.1:9802，唯一暴露面仍是 tailscale serve（TLS + grants）。
'use strict';
const http = require('http');
const net = require('net');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const HOST = '127.0.0.1';
const PORT = 9802;
const TTYD_PORT = 9801;
const COOKIE_NAME = 'r2ttyd_sess';
const SESSION_TTL_MS = 12 * 60 * 60 * 1000; // 12 小时

// ---------- 从 credentials.md 读取 Basic Auth 凭据（与 start-cmd.ps1 同一来源） ----------
function readCredentials() {
    const credFile = path.join(__dirname, 'credentials.md');
    const text = fs.readFileSync(credFile, 'utf8');
    let user = null, pass = null;
    for (const line of text.split(/\r?\n/)) {
        let m = line.match(/^ttyd_user\s*:\s*(\S+)\s*$/); if (m && !user) user = m[1];
        m = line.match(/^ttyd_pass\s*:\s*(\S+)\s*$/); if (m && !pass) pass = m[1];
    }
    if (!user || !pass) throw new Error('credentials.md 中未找到 ttyd_user/ttyd_pass');
    return { user, pass };
}
const cred = readCredentials();
const expectedAuthHeader = 'Basic ' + Buffer.from(`${cred.user}:${cred.pass}`).toString('base64');

// ---------- 会话 Cookie ----------
const sessions = new Map(); // token -> expiry
function newSessionToken() {
    const tok = crypto.randomBytes(16).toString('hex');
    sessions.set(tok, Date.now() + SESSION_TTL_MS);
    return tok;
}
function validCookie(req) {
    const raw = req.headers.cookie || '';
    const m = raw.match(new RegExp('(?:^|;\\s*)' + COOKIE_NAME + '=([A-Za-z0-9]+)'));
    if (!m) return false;
    const exp = sessions.get(m[1]);
    if (!exp || exp < Date.now()) { sessions.delete(m[1]); return false; }
    return true;
}
function validBasic(req) {
    return (req.headers.authorization || '') === expectedAuthHeader;
}
const authed = (req) => validBasic(req) || validCookie(req);

// ---------- HTTP 反代（注入 Authorization，附加会话 Cookie） ----------
const server = http.createServer((req, res) => {
    if (!authed(req)) {
        res.writeHead(401, {
            'WWW-Authenticate': 'Basic realm="ttyd"',
            'Content-Type': 'text/plain; charset=utf-8',
        });
        return res.end('未授权：请输入终端用户名与密码');
    }
    const headers = Object.assign({}, req.headers);
    headers.authorization = expectedAuthHeader;
    delete headers.host;
    const upstream = http.request(
        { host: HOST, port: TTYD_PORT, path: req.url, method: req.method, headers },
        (ur) => {
            const h = Object.assign({}, ur.headers);
            if (validBasic(req) && !validCookie(req)) {
                h['set-cookie'] = (h['set-cookie'] || []).concat(
                    `${COOKIE_NAME}=${newSessionToken()}; Path=/; HttpOnly; SameSite=Strict; Max-Age=${SESSION_TTL_MS / 1000}`);
            }
            res.writeHead(ur.statusCode, h);
            ur.pipe(res);
        });
    upstream.on('error', (e) => {
        if (!res.headersSent) res.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('网关错误：ttyd 不可达');
    });
    req.pipe(upstream);
});

// ---------- WebSocket 升级桥（Safari 只带 Cookie 的关键路径） ----------
server.on('upgrade', (req, socket, head) => {
    if (!authed(req)) {
        socket.write('HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm="ttyd"\r\nConnection: close\r\n\r\n');
        return socket.destroy();
    }
    const key = req.headers['sec-websocket-key'];
    const proto = (req.headers['sec-websocket-protocol'] || 'tty').split(',')[0].trim();
    const ver = req.headers['sec-websocket-version'] || '13';
    const reqLine =
        `GET ${req.url} HTTP/1.1\r\n` +
        `Host: ${HOST}:${TTYD_PORT}\r\n` +
        `Upgrade: websocket\r\nConnection: Upgrade\r\n` +
        `Sec-WebSocket-Key: ${key}\r\n` +
        `Sec-WebSocket-Version: ${ver}\r\n` +
        `Sec-WebSocket-Protocol: ${proto}\r\n` +
        `Authorization: ${expectedAuthHeader}\r\n` +
        `\r\n`;
    const upstream = net.connect(TTYD_PORT, HOST, () => {
        upstream.write(reqLine);
        if (head && head.length) upstream.write(head);
        socket.pipe(upstream);
        upstream.pipe(socket);
    });
    const fail = () => { try { socket.destroy(); upstream.destroy(); } catch (_) {} };
    upstream.on('error', fail);
    socket.on('error', fail);
    socket.on('close', () => upstream.destroy());
    upstream.on('close', () => socket.destroy());
});

server.listen(PORT, HOST, () => {
    console.log(`[ttyd-gateway] listening on http://${HOST}:${PORT} -> 127.0.0.1:${TTYD_PORT} (pid=${process.pid})`);
});
