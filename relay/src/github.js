// The two GitHub calls the relay makes, both against the PRIVATE reports repo (env.REPO), with the
// fine-grained token in env.GITHUB_TOKEN (Issues and Contents read/write on that repo only).
// `fetchImpl` is passed in so the tests can stand in for GitHub.

const API = 'https://api.github.com';

const headers = token => ({
  Authorization: `Bearer ${token}`,
  Accept: 'application/vnd.github+json',
  'X-GitHub-Api-Version': '2022-11-28',
  // GitHub refuses API requests without a User-Agent.
  'User-Agent': 'deskbar-report-relay',
  'Content-Type': 'application/json',
});

/**
 * Commits one file. No `sha` is sent, so GitHub refuses to overwrite an existing file (422): a
 * caller can add a report's attachments but never replace someone else's.
 * Returns { ok: true } or { ok: false, status } with the status GitHub gave.
 */
export const putFile = async (fetchImpl, env, path, base64, message) => {
  const response = await fetchImpl(`${API}/repos/${env.REPO}/contents/${path}`, {
    method: 'PUT',
    headers: headers(env.GITHUB_TOKEN),
    body: JSON.stringify({ message, content: base64, branch: env.BRANCH }),
  });
  return response.status === 201 ? { ok: true } : { ok: false, status: response.status };
};

/** Opens the issue with the from-app label. Returns { ok: true, number } or { ok: false, status }. */
export const createIssue = async (fetchImpl, env, title, body) => {
  const response = await fetchImpl(`${API}/repos/${env.REPO}/issues`, {
    method: 'POST',
    headers: headers(env.GITHUB_TOKEN),
    body: JSON.stringify({ title, body, labels: ['from-app'] }),
  });
  if (response.status !== 201) return { ok: false, status: response.status };
  const { number } = await response.json();
  return { ok: true, number };
};
