# Release and recovery checklist

This is an operational gate, not evidence that the checks have already passed. Version 1.1.1 is a candidate until the exact commit and final signed artifacts satisfy this list.

## Configure release protections

1. Enable private vulnerability reporting and verify its intake. Protect main and release tags; require reviewed PRs and successful native x64/Arm64 jobs.
2. Create the GitHub environment `release` with required maintainer reviewers and restrict its allowed branches/tags. Environment creation alone does not configure reviewers.
3. Supply a trusted code-signing certificate using environment secrets `SIGNING_PFX_BASE64` and `SIGNING_PFX_PASSWORD`. Enter values securely in GitHub settings, never in source, chat or logs. The certificate must have a signing private key and a chain trusted by Windows. The workflow refuses missing credentials, invalid signatures and missing timestamps. Organizations using a non-exportable signing service should integrate their approved service rather than exporting its keys or bypassing the gate.
4. Complete the acceptance record below on the exact candidate commit, then set environment variable `WINDOWS_ACCEPTED_COMMIT` to its full SHA in the `release` environment. Do not reuse acceptance from an earlier commit.
5. Tag the reviewed main commit `v1.1.1` (or the next manifest version). The tag must already exist for a manual release dispatch. The workflow creates a draft; do not publish it until the final signed-byte checks and publisher details are reviewed.

Build/test jobs have read-only repository permissions and no persisted checkout credentials. Signing jobs use the protected environment and retest each architecture's signed bytes. Only the final publication job receives repository write/attestation permissions. Action revisions are pinned; review Dependabot updates rather than loosening permissions.

## Exact-commit Windows acceptance record

Attach dated logs/results to the release or PR and record device, Windows build, architecture, commit and asset hash. Checkboxes are deliberately blank.

- [ ] Both native jobs: core regressions, installer regressions, ZIP self-test and single-file self-test pass without missing/skipped required cases.
- [ ] Clean ordinary-user install on Windows 11 x64 and native Arm64, including browser ZIP and Windows PowerShell 5.1/PowerShell 7 installer paths; no installed SDK assumed.
- [ ] Verify signatures, trusted publisher, timestamps, final SHA-256 values and provenance. Test SmartScreen/antivirus handling without disabling protection. Record any remaining warnings accurately.
- [ ] Install/update/uninstall preserve unrelated target files, sibling `.old` data, history and settings; reject drive roots, traversal, junctions and unexpected ownership. Exercise legacy migration to a new folder.
- [ ] From an inventoried prior install: successful update, missing/duplicate/bad checksum, modified staging, locked/denied files, failed launch, immediate crash, startup timeout and interrupted applier. Confirm a working old version or a precise retained-journal recovery message.
- [ ] Same-account UAC consent, declined consent and different-account credentials produce correct messages; basic monitoring continues. Inspect paths of the actual elevated Windows tools.
- [ ] Enhanced monitoring/temporary block on IPv4/IPv6; stop, disconnect, kill host/broker, suspend/resume and reboot. Confirm temporary filters disappear and protected/shared targets are refused. Check that two live sessions do not stop each other.
- [ ] Force queue pressure and confirm incomplete intervals include loss counts after recovery; bound memory and avoid silent complete-looking data.
- [ ] Battery/no battery, GPU/NPU absent/present, long history, DB locking/corruption, low disk and sleep/resume. Record idle/dashboard/enhanced/capture CPU, RAM and battery overhead rather than inventing a universal cost.
- [ ] Keyboard/Narrator chart inspection and interval selection, high contrast, visible focus and 150–200% scaling.
- [ ] Confirm private security intake, support instructions, third-party redistribution terms and sensitive-capture handling.

## Packaging and publication

The workflow records commit, SDK, runtime and dependency versions in `build-info.json` inside each ZIP. `surface-insight.files.json` is regenerated after signing, and `SHA256SUMS.txt` describes the final signed assets. The installer script is signed separately. Source association and inventory do not promise byte-for-byte reproducibility of mutable SDK/data inputs.

Review the draft release's exact commit, signing subject, migration instructions and acceptance evidence. Replace the publisher placeholder in the draft notes with the verified certificate subject. Do not silently overwrite existing versioned downloads. Publishing the draft is a separate maintainer action; creating the PR does not publish a release.

For local compile/packaging on Linux, use `dotnet build src/SurfaceInsight/SurfaceInsight.csproj -p:EnableWindowsTargeting=true` and optionally `pwsh scripts/build.ps1 -Arch all -SingleFile -SkipGeo -EnableWindowsTargeting`. These are cross-builds, not Windows runtime acceptance. The geo database is optional for development; normal release builds fetch and bundle it over verified HTTPS.

## Recovery after interruption

Quit the affected installation from the tray before manual recovery. Keep the error log and the **exact unique** `transaction-*` directory reported by the updater/installer under `%LOCALAPPDATA%\SurfaceInsight\updates`. It contains `transaction.json` and indexed `.bak` files. Do not delete other folders, especially a pre-existing `<install>.old` folder. Unknown legacy backups are intentionally not cleaned automatically.

For an interrupted inventoried replacement, inspect the journal's `Target`, retain a copy of the journal/backups, download and inspect the versioned installer, then load only its functions:

```powershell
. .\install.ps1 -FunctionsOnly
# Substitute the exact transaction directory reported by the failure.
$backup = "$env:LOCALAPPDATA\SurfaceInsight\updates\transaction-EXACT-ID"
$journal = Get-Content (Join-Path $backup 'transaction.json') -Raw | ConvertFrom-Json
Stop-Installation $journal.Target
Restore-Installation $journal.Target $backup
```

Recovery checks backup hashes and refuses to overwrite a subsequently changed file. If it refuses, preserve those files and backups for manual comparison; do not force recursive deletion. Recovery works only with this version's journal format. Routine history is separate; do not delete `history.db` to repair an application update. Capture files may be sensitive and are not automatically erased by history clearing.
