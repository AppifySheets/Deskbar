// Tests for the pure half: validation, paths, title and body. Run with `node --test` in relay/.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LIMITS, attachmentPath, issueBody, issueTitle, validateAttachment, validateReport } from '../src/report.js';

const ID = '0123456789abcdef0123456789abcdef';
const PNG = 'iVBORw0KGgoAAAANSUhEUg==';
const JPEG = '/9j/4AAQSkZJRg==';

const report = overrides => ({
  reportId: ID,
  text: 'The bar vanished\nafter sleep',
  contact: '',
  appVersion: '2.5.12',
  windowsBuild: '10.0.26200',
  store: false,
  attachments: [],
  ...overrides,
});

test('a png, a jpeg and a dump are accepted', () => {
  assert.deepEqual(validateAttachment(ID, 'shot-1.png', PNG), { ok: true });
  assert.deepEqual(validateAttachment(ID, 'shot-5.jpg', JPEG), { ok: true });
  assert.deepEqual(validateAttachment(ID, 'dump.txt', 'aGVsbG8='), { ok: true });
});

test('attachment names outside the fixed list are refused', () => {
  ['shot-6.png', 'shot-1.gif', '../x.png', 'dump.exe', '', null].forEach(name =>
    assert.equal(validateAttachment(ID, name, PNG).status, 400, String(name)));
});

test('a report id must be 32 lowercase hex digits', () => {
  ['', 'XYZ', ID.toUpperCase(), `${ID}0`, '../../etc', null].forEach(id =>
    assert.equal(validateAttachment(id, 'shot-1.png', PNG).status, 400, String(id)));
});

test('an image whose bytes do not match its extension is refused', () => {
  assert.equal(validateAttachment(ID, 'shot-1.png', JPEG).status, 415);
  assert.equal(validateAttachment(ID, 'shot-1.jpg', PNG).status, 415);
});

test('an empty or oversized attachment is refused', () => {
  assert.equal(validateAttachment(ID, 'dump.txt', '').status, 400);
  assert.equal(validateAttachment(ID, 'dump.txt', 'a'.repeat(LIMITS.attachmentChars + 1)).status, 413);
});

test('attachments live in one folder per report', () =>
  assert.equal(attachmentPath(ID, 'shot-1.png'), `reports/${ID}/shot-1.png`));

test('a well-formed report validates and its contact is trimmed', () => {
  const result = validateReport(report({ contact: '  @someone  ', store: true, attachments: ['shot-1.png', 'dump.txt'] }));
  assert.equal(result.ok, true);
  assert.equal(result.value.contact, '@someone');
});

test('a branch build version is accepted', () =>
  assert.equal(validateReport(report({ appVersion: '2.5.12+1a2b3c4' })).ok, true));

test('malformed reports are refused with a reason', () => {
  const cases = [
    [null, 'not a json object'],
    [report({ text: '   ' }), 'empty text'],
    [report({ text: 'x'.repeat(LIMITS.textChars + 1) }), 'text too long'],
    [report({ contact: 'x'.repeat(LIMITS.contactChars + 1) }), 'bad contact'],
    [report({ appVersion: 'latest' }), 'bad app version'],
    [report({ windowsBuild: 'Windows 11' }), 'bad windows build'],
    [report({ store: 'yes' }), 'bad store flag'],
    [report({ attachments: ['evil.sh'] }), 'bad attachment name'],
    [report({ attachments: ['dump.txt', 'dump.txt'] }), 'duplicate attachment'],
    [report({ reportId: 'nope' }), 'bad report id'],
  ];
  cases.forEach(([input, reason]) => assert.equal(validateReport(input).error, reason));
});

test('the title is the first non-empty line, cut to 80 characters', () => {
  assert.equal(issueTitle('\n\n  The bar vanished  \nmore'), 'From the app: The bar vanished');
  const long = issueTitle('x'.repeat(200));
  assert.equal(long, `From the app: ${'x'.repeat(79)}…`);
});

test('the user text is fenced so nothing in it renders', () => {
  const body = issueBody(report({ text: 'ping @petre ```not a fence``` ![x](http://t/p.png)' }), 'o/r', 'main');
  // The fence must be longer than the longest backtick run in the text (3), so four.
  assert.match(body, /````text\nping @petre ```not a fence``` !\[x\]\(http:\/\/t\/p\.png\)\n````/);
});

test('the contact line cannot break out of its inline code', () => {
  const body = issueBody(report({ contact: 'a`b\nc' }), 'o/r', 'main');
  assert.match(body, /Contact: `a b c`/);
});

test('screenshots are embedded and the dump is linked', () => {
  const body = issueBody(report({ store: true, attachments: ['shot-1.png', 'shot-2.jpg', 'dump.txt'] }), 'o/r', 'main');
  assert.match(body, /Deskbar 2\.5\.12 \(Store build\) on Windows 10\.0\.26200/);
  assert.ok(body.includes(`![shot-1.png](https://github.com/o/r/blob/main/reports/${ID}/shot-1.png?raw=true)`));
  assert.ok(body.includes(`![shot-2.jpg](https://github.com/o/r/blob/main/reports/${ID}/shot-2.jpg?raw=true)`));
  assert.ok(body.includes(`[dump.txt](https://github.com/o/r/blob/main/reports/${ID}/dump.txt)`));
});

test('a report without attachments has no screenshot or dump section', () => {
  const body = issueBody(report(), 'o/r', 'main');
  assert.ok(!body.includes('### Screenshots'));
  assert.ok(!body.includes('### Debug dump'));
  assert.match(body, /Contact: none given/);
});
