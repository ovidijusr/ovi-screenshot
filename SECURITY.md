# Security

Ovi Screenshot is preview software. Only the latest source revision is maintained; there is no separate supported release branch yet.

## Report a vulnerability

If GitHub's **Security → Report a vulnerability** option is available on this repository, use it for a private report. If it is unavailable, open an issue requesting a private contact channel without including exploit details, credentials, captures or other sensitive data. Do not assume private reporting has been enabled.

Include the affected version, a minimal reproduction using generated content, and the impact. There is no guaranteed response time at this stage.

## Local data

Preferences, bounded diagnostic logs, exported PNG cache files and unsaved recordings are stored under `%LOCALAPPDATA%\OviScreenshot`. Removing the app folder does not remove this data. Review logs and screenshots before attaching them to a public issue. See [development guidance](docs/development.md) for storage and retention behavior.
