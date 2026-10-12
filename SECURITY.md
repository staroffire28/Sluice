# Security Policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Use GitHub's private reporting instead: go to the **Security** tab of this
repository and choose **Report a vulnerability**. You will get a response as
soon as possible.

Sluice stores connection secrets (ADLS Gen2 and SQL Server credentials)
encrypted at rest. If you find a way to read, log, or leak them, that is exactly
the kind of report we want.

## Supported versions

Only the latest release on `main` receives security fixes.
