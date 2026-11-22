// Tests for the request path: routing, the guards, and what is sent to GitHub. GitHub is a fake
// fetch that records every call; the rate limiters are fakes that can be told to refuse.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from '../src/index.js';

const ID = '0123456789abcdef0123456789abcdef';
const PNG = 'iVBORw0KGgoAAAANSUhEUg==';

const limiter = (allow = true) => ({ limit: async () => ({ success: allow }) });

const env = overrides => ({
  REPO: 'AppifySheets/Deskbar-reports',
  BRANCH: 'main',
  GITHUB_TOKEN: 'test-token',
  REPORT_LIMITER: limiter(),
  ATTACHMENT_LIMITER: limiter(),
  ...overrides,
});

// A fake GitHub: answers each call with the next status in `statuses` and records the request.
const fakeGitHub = (...statuses) => {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, init, body: JSON.parse(init.body) });
    const status = statuses[calls.length - 1] ?? 201;
    return new Response(JSON.stringify({ number: 42 }), { status });
  };
  return { calls, fetchImpl };
};

const appRequest = (path, init = {}) => new Request(`https://report.deskbar.app${path}`, {
  ...init,
  headers: { 'User-Agent': 'Deskbar/2.5.12', 'CF-Connecting-IP': '203.0.113.7', ...(init.headers ?? {}) },
});

const attachment = (name = 'shot-1.png', body = PNG) =>
  appRequest(`/v1/attachment?report=${ID}&name=${name}`, { method: 'POST', body });

const reportRequest = overrides => appRequest('/v1/report', {
  method: 'POST',
  body: JSON.stringify({
    reportId: ID, text: 'It broke', contact: '', appVersion: '2.5.12',
    windowsBuild: '10.0.26200', store: false, attachments: ['shot-1.png'], ...overrides,
  }),
});

test('GET / says what this is', async () => {
  const response = await handle(new Request('https://report.deskbar.app/'), env(), fakeGitHub().fetchImpl);
  assert.equal(response.status, 200);
  assert.match(await response.text(), /Deskbar report relay/);
});

test('unknown routes are 404', async () => {
  const response = await handle(appRequest('/v1/other', { method: 'POST' }), env(), fakeGitHub().fetchImpl);
  assert.equal(response.status, 404);
});

test('a request without the app User-Agent is refused before GitHub is called', async () => {
  const github = fakeGitHub();
  const request = new Request(`https://report.deskbar.app/v1/report`, { method: 'POST', body: '{}' });
  assert.equal((await handle(request, env(), github.fetchImpl)).status, 400);
  assert.equal(github.calls.length, 0);
});

test('without the token the relay answers 503', async () => {
  const response = await handle(reportRequest(), env({ GITHUB_TOKEN: undefined }), fakeGitHub().fetchImpl);
  assert.equal(response.status, 503);
});

test('an attachment is committed to the report folder without a sha', async () => {
  const github = fakeGitHub(201);
  const response = await handle(attachment(), env(), github.fetchImpl);
  assert.equal(response.status, 201);
  assert.deepEqual(await response.json(), { path: `reports/${ID}/shot-1.png` });
  const [call] = github.calls;
  assert.equal(call.url, `https://api.github.com/repos/AppifySheets/Deskbar-reports/contents/reports/${ID}/shot-1.png`);
  assert.equal(call.init.method, 'PUT');
  assert.equal(call.init.headers.Authorization, 'Bearer test-token');
  assert.equal(call.body.content, PNG);
  assert.equal(call.body.branch, 'main');
  assert.equal(call.body.sha, undefined);
});

test('an attachment that already exists answers 409, so a retry is harmless', async () => {
  const response = await handle(attachment(), env(), fakeGitHub(422).fetchImpl);
  assert.equal(response.status, 409);
});

test('a GitHub failure on an attachment answers 502', async () => {
  const response = await handle(attachment(), env(), fakeGitHub(500).fetchImpl);
  assert.equal(response.status, 502);
});

test('an invalid attachment never reaches GitHub', async () => {
  const github = fakeGitHub();
  const response = await handle(attachment('shot-1.png', '/9j/notapng'), env(), github.fetchImpl);
  assert.equal(response.status, 415);
  assert.equal(github.calls.length, 0);
});

test('the attachment rate limit answers 429 before GitHub is called', async () => {
  const github = fakeGitHub();
  const response = await handle(attachment(), env({ ATTACHMENT_LIMITER: limiter(false) }), github.fetchImpl);
  assert.equal(response.status, 429);
  assert.equal(github.calls.length, 0);
});

test('a report opens an issue with the from-app label and answers its number', async () => {
  const github = fakeGitHub(201);
  const response = await handle(reportRequest(), env(), github.fetchImpl);
  assert.equal(response.status, 201);
  assert.deepEqual(await response.json(), { number: 42 });
  const [call] = github.calls;
  assert.equal(call.url, 'https://api.github.com/repos/AppifySheets/Deskbar-reports/issues');
  assert.deepEqual(call.body.labels, ['from-app']);
  assert.equal(call.body.title, 'From the app: It broke');
  assert.match(call.body.body, /shot-1\.png\?raw=true/);
});

test('the people in NOTIFY are mentioned in the issue', async () => {
  const github = fakeGitHub(201);
  await handle(reportRequest(), env({ NOTIFY: 'lukasurmava' }), github.fetchImpl);
  assert.match(github.calls[0].body.body, /cc @lukasurmava/);
});

test('a Deskbar Pro report is also labelled priority', async () => {
  const github = fakeGitHub(201);
  await handle(reportRequest({ pro: true }), env(), github.fetchImpl);
  assert.deepEqual(github.calls[0].body.labels, ['from-app', 'priority']);
  assert.match(github.calls[0].body.body, /Deskbar Pro: priority support/);
});

test('the report rate limit answers 429', async () => {
  const response = await handle(reportRequest(), env({ REPORT_LIMITER: limiter(false) }), fakeGitHub().fetchImpl);
  assert.equal(response.status, 429);
});

test('a report body that is not json answers 400', async () => {
  const request = appRequest('/v1/report', { method: 'POST', body: 'not json' });
  assert.equal((await handle(request, env(), fakeGitHub().fetchImpl)).status, 400);
});

test('an invalid report answers its reason and opens nothing', async () => {
  const github = fakeGitHub();
  const response = await handle(reportRequest({ text: '' }), env(), github.fetchImpl);
  assert.equal(response.status, 400);
  assert.deepEqual(await response.json(), { error: 'empty text' });
  assert.equal(github.calls.length, 0);
});

test('a GitHub failure on the report answers 502', async () => {
  const response = await handle(reportRequest(), env(), fakeGitHub(403).fetchImpl);
  assert.equal(response.status, 502);
});
