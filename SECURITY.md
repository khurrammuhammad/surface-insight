# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead: **Security tab > Report a vulnerability** on this repository. Include the version (Settings > About this build), what you did, and what you expected. You'll get a reply as soon as the maintainer can, and credit in the release notes if you want it.

## Supported versions

Only the latest release gets fixes.

## What is worth reporting

Surface Insight runs as your user and stores history locally. It contacts the network only for the optional update check (GitHub's API) and the optional reverse-DNS lookup. The most sensitive parts are:

- **The in-app updater.** It downloads a release, checks it against `SHA256SUMS.txt`, stops the running app and replaces the program folder. Anything that lets a wrong, older or altered build be installed, that makes the check or install run without the user asking, or that lets another user or process redirect the swap is in scope.

- **The optional elevated helper** used for enhanced network monitoring and temporary blocking. It is started only after a Windows administrator prompt and talks to the tray monitor over a local named pipe. Anything that lets another process or user abuse that channel is in scope.
- **Temporary blocks** (Windows Filtering Platform filters). Anything that makes a block outlive its session, or lets one be created for a program the app should refuse, is in scope.
- **The installer script** and **release packages**: anything that could cause the wrong or an altered file to be installed.
- **Locally stored history**, which is unencrypted by design (see the README's Privacy section). Reading it requires access to your Windows account, so that alone isn't a vulnerability.

## Verifying downloads

Each release publishes `SHA256SUMS.txt`, and the installer checks it automatically. Builds are not yet code-signed.
