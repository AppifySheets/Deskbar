// Deskbar's report relay: the Cloudflare Worker behind https://report.deskbar.app (issue #331).
//
// WHY IT EXISTS: "Report a problem…" in the app must file a report without the user signing in
// anywhere, and the app cannot carry a GitHub token (the repo is public and the Store package can
// be unpacked). The token lives only in this Worker's secrets, and the Worker files the report as
// an issue in the PRIVATE repo AppifySheets/Deskbar-reports, where screenshots and window titles
// stay out of public view.
//
// ROUTES
//   GET  /                  a line saying what this is; handy to check a deploy
//   POST /v1/attachment     ?report=<id>&name=<shot-N.png|shot-N.jpg|dump.txt>, body = base64 text
//   POST /v1/report         JSON, see validateReport in report.js; answers { number }
//
// SPAM GUARD, cheapest first: the app's User-Agent, size limits, a per-address rate limit, and the
// fixed set of file names. None of it is a lock (anyone can copy the User-Agent); together they
// keep a drive-by bot out, and anything that gets through lands in a private repo under a label.

import { LIMITS, attachmentPath, issueBody, issueLabels, issueTitle, validateAttachment, validateReport } from './report.js';
import { createIssue, putFile } from './github.js';

const json = (status, value) => new Response(JSON.stringify(value), {
  status,
  headers: { 'Content-Type': 'application/json' },
});

const error = (status, message) => json(status, { error: message });

// The app sends "Deskbar/<version>". Anything else is not the app.
const fromTheApp = request => (request.headers.get('User-Agent') ?? '').startsWith('Deskbar/');

// Cloudflare sets CF-Connecting-IP to the caller's address. The rate limiter counts per address,
// per Cloudflare location, per minute (the binding's only periods are 10 and 60 seconds).
const callerOf = request => request.headers.get('CF-Connecting-IP') ?? 'unknown';

// A declared Content-Length over the limit is refused before the body is read.
const declaredTooLarge = (request, limit) => Number(request.headers.get('Content-Length') ?? 0) > limit;

// Logs carry what happened and to which report, never what the user wrote.
const log = (event, fields) => console.log(JSON.stringify({ event, ...fields }));

const handleAttachment = async (request, env, fetchImpl, url) => {
  const { success } = await env.ATTACHMENT_LIMITER.limit({ key: callerOf(request) });
  if (!success) return error(429, 'too many attachments, try again in a minute');
  if (declaredTooLarge(request, LIMITS.attachmentChars)) return error(413, 'attachment too large');

  const reportId = url.searchParams.get('report');
  const name = url.searchParams.get('name');
  const body = (await request.text()).trim();
  const check = validateAttachment(reportId, name, body);
  if (!check.ok) return error(check.status, check.error);

  const path = attachmentPath(reportId, name);
  const put = await putFile(fetchImpl, env, path, body, `Report ${reportId}: ${name}`);
  log('attachment', { reportId, name, chars: body.length, github: put.ok ? 201 : put.status });
  // 422 from GitHub here means the file is already there: the app retried a send that had worked.
  return put.ok ? json(201, { path })
    : put.status === 422 ? error(409, 'attachment already uploaded')
    : error(502, 'github refused the attachment');
};

const handleReport = async (request, env, fetchImpl) => {
  const { success } = await env.REPORT_LIMITER.limit({ key: callerOf(request) });
  if (!success) return error(429, 'too many reports, try again in a minute');
  if (declaredTooLarge(request, LIMITS.reportBodyBytes)) return error(413, 'report too large');

  const raw = await request.text();
  if (raw.length > LIMITS.reportBodyBytes) return error(413, 'report too large');
  let parsed;
  try { parsed = JSON.parse(raw); } catch { return error(400, 'not json'); }
  const check = validateReport(parsed);
  if (!check.ok) return error(check.status, check.error);

  const report = check.value;
  const issue = await createIssue(fetchImpl, env, issueTitle(report.text), issueBody(report, env.REPO, env.BRANCH, env.NOTIFY), issueLabels(report));
  log('report', { reportId: report.reportId, pro: report.pro, attachments: report.attachments.length, github: issue.ok ? 201 : issue.status, number: issue.number });
  return issue.ok ? json(201, { number: issue.number }) : error(502, 'github refused the report');
};

/** The whole request path, with fetch passed in so the tests can stand in for GitHub. */
export const handle = async (request, env, fetchImpl) => {
  const url = new URL(request.url);
  if (request.method === 'GET' && url.pathname === '/')
    return new Response('Deskbar report relay. The app posts problem reports here.\n');

  const route = `${request.method} ${url.pathname}`;
  if (route !== 'POST /v1/attachment' && route !== 'POST /v1/report') return error(404, 'not found');
  if (!fromTheApp(request)) return error(400, 'not from the app');
  // Until the token is pasted into the Worker's secrets, say so plainly instead of failing at GitHub.
  if (!env.GITHUB_TOKEN) return error(503, 'relay not configured yet');

  return route === 'POST /v1/attachment'
    ? handleAttachment(request, env, fetchImpl, url)
    : handleReport(request, env, fetchImpl);
};

export default {
  fetch: (request, env) => handle(request, env, fetch),
};
