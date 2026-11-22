// The pure half of the relay: what a valid upload looks like, where it is stored, and the issue
// it becomes. Nothing here does I/O, so `node --test` covers it without Cloudflare or GitHub.
//
// The shape of a report, as the app sends it (issue #331):
//   1. one POST /v1/attachment per screenshot and one for the debug dump, each body the file's
//      BASE64 text, made by the app;
//   2. one POST /v1/report with the user's text, the contact line and the names of the
//      attachments that went up.
//
// WHY THE APP DOES THE BASE64 and sends one file per request: the Workers free plan allows 10 ms
// of CPU per request. Encoding or decoding several megabytes in JavaScript would blow that, and
// GitHub's Contents API wants base64 anyway, so the Worker only passes the app's string through.
// Reading the body and handing it to fetch is native code and waiting on GitHub is not CPU time.

// The limits, in characters of what arrives. 7,000,000 base64 characters is about 5 MB of image,
// which is the cap the app's window enforces (it re-encodes anything larger). GitHub's Contents API
// was measured accepting 7 MB files on 2026-11-22 (Deskbar-reports, throwaway branch).
export const LIMITS = Object.freeze({
  textChars: 20_000,
  contactChars: 200,
  attachmentChars: 7_000_000,
  reportBodyBytes: 100_000,
  attachments: 6,
});

// A GUID in .NET's "N" format (32 lowercase hex digits), made by the app per report. It names the
// report's folder, so attachments and the report that lists them meet without the relay keeping
// any state between requests.
const REPORT_ID = /^[0-9a-f]{32}$/;

// The only file names the relay stores: up to five screenshots and the dump. A fixed list means a
// caller cannot choose paths, extensions or how many files land in the repo.
const ATTACHMENT_NAME = /^(shot-[1-5]\.(png|jpg)|dump\.txt)$/;

// "2.5.12", "2.5.12+1a2b3c4" (a branch build), or similar. Kept loose but bounded.
const APP_VERSION = /^\d+\.\d+\.\d+[0-9A-Za-z.+-]{0,40}$/;

// Environment.OSVersion as the app reports it, e.g. "10.0.26200" or "10.0.26200.0".
const WINDOWS_BUILD = /^\d+\.\d+\.\d+(\.\d+)?$/;

// The first characters of a base64-encoded PNG and JPEG. Checked on the prefix only: decoding the
// whole image to inspect it is the CPU this design avoids, and GitHub refuses invalid base64.
const PNG_PREFIX = 'iVBORw0KGgo';
const JPEG_PREFIX = '/9j/';

const fail = (status, error) => ({ ok: false, status, error });

/** Checks an attachment upload before anything is sent to GitHub. `body` is the base64 text. */
export const validateAttachment = (reportId, name, body) =>
  !REPORT_ID.test(reportId ?? '') ? fail(400, 'bad report id')
  : !ATTACHMENT_NAME.test(name ?? '') ? fail(400, 'bad attachment name')
  : body.length === 0 ? fail(400, 'empty attachment')
  : body.length > LIMITS.attachmentChars ? fail(413, 'attachment too large')
  : name.endsWith('.png') && !body.startsWith(PNG_PREFIX) ? fail(415, 'not a png')
  : name.endsWith('.jpg') && !body.startsWith(JPEG_PREFIX) ? fail(415, 'not a jpeg')
  : { ok: true };

/** Where an attachment lives in the reports repo. One folder per report. */
export const attachmentPath = (reportId, name) => `reports/${reportId}/${name}`;

const isString = value => typeof value === 'string';

/**
 * Checks the report's JSON and returns the cleaned value. Unknown fields are ignored, so a newer
 * app can send more without breaking an older relay.
 */
export const validateReport = json => {
  if (json === null || typeof json !== 'object') return fail(400, 'not a json object');
  const { reportId, text, contact = '', appVersion, windowsBuild, store = false, attachments = [] } = json;
  if (!isString(reportId) || !REPORT_ID.test(reportId)) return fail(400, 'bad report id');
  if (!isString(text) || text.trim().length === 0) return fail(400, 'empty text');
  if (text.length > LIMITS.textChars) return fail(413, 'text too long');
  if (!isString(contact) || contact.length > LIMITS.contactChars) return fail(400, 'bad contact');
  if (!isString(appVersion) || !APP_VERSION.test(appVersion)) return fail(400, 'bad app version');
  if (!isString(windowsBuild) || !WINDOWS_BUILD.test(windowsBuild)) return fail(400, 'bad windows build');
  if (typeof store !== 'boolean') return fail(400, 'bad store flag');
  if (!Array.isArray(attachments) || attachments.length > LIMITS.attachments) return fail(400, 'bad attachments');
  if (!attachments.every(name => isString(name) && ATTACHMENT_NAME.test(name))) return fail(400, 'bad attachment name');
  if (new Set(attachments).size !== attachments.length) return fail(400, 'duplicate attachment');
  return { ok: true, value: { reportId, text, contact: contact.trim(), appVersion, windowsBuild, store, attachments } };
};

// Control characters (other than tab and newline) have no business in an issue and can hide text.
const stripControl = value => value.replace(/[\u0000-\u0008\u000B-\u001F\u007F]/g, '');

/** The issue title: the first non-empty line of what the user wrote, cut to 80 characters. */
export const issueTitle = text => {
  const first = stripControl(text).split('\n').map(line => line.trim()).find(line => line.length > 0) ?? '';
  const cut = first.length > 80 ? `${first.slice(0, 79)}…` : first;
  return `From the app: ${cut}`;
};

// The user's text goes inside a fenced block, so nothing they type is rendered: no @mention pings
// anyone, no image loads a tracking pixel, no link is clickable by accident. The fence is one
// backtick longer than the longest run of backticks in the text, so the text cannot close it.
const fenced = text => {
  const longestRun = Math.max(0, ...(text.match(/`+/g) ?? []).map(run => run.length));
  const fence = '`'.repeat(Math.max(3, longestRun + 1));
  return `${fence}text\n${text}\n${fence}`;
};

// The contact line sits in inline code for the same reason; backticks and line breaks are removed
// so it stays one inert line.
const inlineCode = value => `\`${value.replace(/[`\r\n]/g, ' ')}\``;

const blobUrl = (repo, branch, path) => `https://github.com/${repo}/blob/${branch}/${path}`;

/**
 * The issue body. Screenshots are embedded by their blob URL with ?raw=true, which renders for
 * anyone who can see the private repo; the dump is a link, since it can be long.
 */
export const issueBody = (report, repo, branch) => {
  const shots = report.attachments.filter(name => name.startsWith('shot-'));
  const hasDump = report.attachments.includes('dump.txt');
  const build = report.store ? 'Store build' : 'direct download';
  const lines = [
    `Deskbar ${report.appVersion} (${build}) on Windows ${report.windowsBuild}`,
    `Contact: ${report.contact.length > 0 ? inlineCode(stripControl(report.contact)) : 'none given'}`,
    `Report id: \`${report.reportId}\``,
    '',
    '### What happened',
    '',
    fenced(stripControl(report.text)),
  ];
  const shotLines = shots.length === 0 ? [] : [
    '',
    '### Screenshots',
    '',
    ...shots.map(name => `![${name}](${blobUrl(repo, branch, attachmentPath(report.reportId, name))}?raw=true)`),
  ];
  const dumpLines = !hasDump ? [] : [
    '',
    '### Debug dump',
    '',
    `[dump.txt](${blobUrl(repo, branch, attachmentPath(report.reportId, 'dump.txt'))})`,
  ];
  return [...lines, ...shotLines, ...dumpLines, ''].join('\n');
};
