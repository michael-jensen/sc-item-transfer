# Configuration

item-copy needs a host name and automation credentials for each SitecoreAI environment it copies from
or to. They go in a `.env` file.

## Credentials

You need the **Organization Admin** or **Organization Owner** role. For **each** environment:

1. Sitecore Cloud Portal → SitecoreAI Deploy → **Credentials** → **Environment** →
   **Create credentials** → **Automation**.
2. Copy the client ID and secret (they can't be viewed again).
3. Note the host name: **Projects** → your project → **Authoring environments** → your environment →
   **Details** → **Environment host name**.

## `.env`

Start from [`.env.example`](../.env.example). Each environment needs three variables:

```
SITECORE_DEV_HOST=xmc-xxxxxxxx-dev.sitecorecloud.io
SITECORE_DEV_CLIENT_ID=...
SITECORE_DEV_CLIENT_SECRET=...
```

The name in the middle is how job files refer to the environment (`"source": "dev"`), ignoring case.
Any name made of letters, digits and underscores works, not just DEV/SIT/PROD.

| Variable | Default | Purpose |
|---|---|---|
| `SITECORE_<NAME>_HOST` | | The environment's host name |
| `SITECORE_<NAME>_CLIENT_ID` | | Automation client ID |
| `SITECORE_<NAME>_CLIENT_SECRET` | | Automation client secret |
| `SITECORE_PROTECTED_ENVS` | `PROD` | Comma-separated destinations that require typing the environment name to confirm. Set it to empty to protect none. See [Confirmation](running.md#confirmation). |
| `SITECORE_AUTH_URL` | `https://auth.sitecorecloud.io/oauth/token` | Only change this if Sitecore tells you to. |
| `SITECORE_AUTH_AUDIENCE` | `https://api.sitecorecloud.io` | Only change this if Sitecore tells you to. |

### Where item-copy looks for it

1. The file given with `--env-file <path>`, if any.
2. `.env` in the current directory.
3. `.env` next to the executable.

Real `SITECORE_*` environment variables take precedence over `.env`, which is useful in CI.

### Keep it private

`.env` holds credentials that can write to your environments.

- Restrict it to your user: `chmod 600 .env`.
- Never commit it. This repository's `.gitignore` already excludes it.
- Keep it in a working folder rather than next to the executable, so credentials don't end up in a
  `bin` folder.
- In WSL, keep it under your Linux home folder (`~`) rather than `/mnt/c`, where `chmod` has no effect
  by default.
