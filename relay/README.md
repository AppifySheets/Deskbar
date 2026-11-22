# Report relay

A Cloudflare Worker at `https://report.deskbar.app`. The app's "Report a problem…" window sends
reports here, and the Worker files each one as an issue in the private repository
[AppifySheets/Deskbar-reports](https://github.com/AppifySheets/Deskbar-reports), labelled
`from-app`. The design and the reasons for it are in
[issue #331](https://github.com/AppifySheets/Deskbar/issues/331).

The relay exists so that nobody has to sign in to send a report and the app carries no secret.
The GitHub token lives only in the Worker's secrets. Reports go to a private repository because a
screenshot shows the user's desktop and the debug dump lists every window title on it.

## How a report travels

1. The app makes a report id, a GUID in `N` format (32 lowercase hex digits).
2. For each screenshot and for the dump, the app sends
   `POST /v1/attachment?report=<id>&name=<name>`, with the file's base64 text as the body. Names
   are fixed: `shot-1.png` to `shot-5.png` (or `.jpg`) and `dump.txt`. The Worker commits the file
   to `reports/<id>/<name>` in the reports repository and answers `201`. A `409` means the file
   is already there, so a retried send is harmless.
3. The app sends `POST /v1/report` with this JSON:

   ```json
   {
     "reportId": "0123456789abcdef0123456789abcdef",
     "text": "What happened, as the user typed it",
     "contact": "optional email or handle",
     "appVersion": "2.5.12",
     "windowsBuild": "10.0.26200",
     "store": false,
     "pro": false,
     "attachments": ["shot-1.png", "dump.txt"]
   }
   ```

   `pro` is optional and false by default. It is true when the copy owns the Deskbar Pro add-on,
   which comes with priority support, and then the issue gets the `priority` label as well and a
   "Deskbar Pro: priority support" line.

   The Worker opens the issue and answers `201 {"number": 12}`. The app shows that number as the
   report's reference. The user cannot open the issue itself, since the repository is private.

Every request needs a `User-Agent` that starts with `Deskbar/`. Errors come back as
`{"error": "..."}`, with 400 for a malformed request, 413 when it is too large, 415 for an image
whose bytes do not match its extension, 429 for the rate limit, 502 when GitHub refused and 503
while the token is not set yet.

The app base64-encodes files itself and sends them one at a time because the Workers free plan
allows 10 ms of CPU per request. The Worker never decodes an image. It passes the string through
to GitHub, and waiting on GitHub does not count as CPU time.

## Limits and spam

- Text up to 20,000 characters, contact up to 200, each attachment up to 7,000,000 base64
  characters (about 5 MB), at most six attachments per report.
- Per caller address and Cloudflare location: 3 reports and 12 attachments a minute
  (`wrangler.toml`). The rate-limit binding only counts over 10 or 60 seconds, so there is no
  hourly cap.
- The user's text is put in a fenced code block and the contact line in inline code, so an
  `@mention` pings nobody and an image link loads nothing.
- Anything that gets past this lands in a private repository under a label. If it becomes a
  problem, rotating the token (below) stops it within a minute.

## One-time setup

The `Relay` workflow (`.github/workflows/relay.yml`) deploys on every merge to main that changes
`relay/`. It needs two repository secrets, and the Worker needs one secret of its own.

1. **Cloudflare API token.** In the Cloudflare dashboard go to My Profile, API Tokens, Create
   Token, and use the "Edit Cloudflare Workers" template. Limit Account Resources to the account
   that holds deskbar.app, and Zone Resources to deskbar.app. Save the token as the repository
   secret `CLOUDFLARE_API_TOKEN`.
2. **Account id.** It is on the deskbar.app zone's overview page in the dashboard. Save it as
   `CLOUDFLARE_ACCOUNT_ID`.
3. **First deploy.** Run the Relay workflow by hand, or merge a change under `relay/`. Until both
   secrets exist, the deploy job skips with a notice instead of failing.
4. **GitHub token for the Worker.** Create a
   [fine-grained token](https://github.com/settings/personal-access-tokens/new) with resource
   owner AppifySheets, only the Deskbar-reports repository, and Issues and Contents set to read
   and write. Nothing else. In Cloudflare go to Workers & Pages, deskbar-report-relay, Settings,
   Variables and Secrets, and add it as the secret `GITHUB_TOKEN`. It goes to Cloudflare only,
   never into GitHub's secrets. Fine-grained tokens expire after a year at most, so put a
   reminder in the calendar.

Check it worked: `curl https://report.deskbar.app/` prints a line naming the relay. Then send a
test report:

```bash
id=$(node -e "console.log(crypto.randomUUID().replaceAll('-',''))")
curl -s -A "Deskbar/0.0.0" -X POST https://report.deskbar.app/v1/report \
  -d "{\"reportId\":\"$id\",\"text\":\"relay test, please close\",\"appVersion\":\"0.0.0\",\"windowsBuild\":\"10.0.26200\"}"
```

The answer is the new issue's number in Deskbar-reports. Close that issue afterwards.

## Rotating the GitHub token

Create a new fine-grained token as in step 4, replace `GITHUB_TOKEN` in the Worker's settings,
then revoke the old token on GitHub. The Worker picks up the new secret at once and no deploy is
needed.

## Tests

```bash
cd relay
node --test
```

The tests use Node's built-in runner and need no install. GitHub is a fake `fetch` and the rate
limiters are fakes, so nothing leaves the machine. Wrangler itself needs Node 22.12 or newer;
the workflow runs a dry-run deploy on every pull request, so it is not needed locally.
