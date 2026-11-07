# Audit remediation tracking for 1.1.1

This maps the 7 November 2026 launch-readiness review to the candidate. It does not declare external signing configuration or Windows acceptance complete.

| Finding | Change | Verification / remaining gate |
|---|---|---|
| F01 High: unsafe directory deletion | File inventory, unique journal/backups, file-level replacement/recovery, no sibling `.old` cleanup; reject links and unowned path collisions | Portable and installer sentinel/traversal/symlink regressions; Windows junction/locked-file acceptance still required |
| F02: checksum fail-open | Installer and updater require a unique hexadecimal SHA-256 entry; verify the package inventory | Missing/duplicate/malformed/changed package regressions |
| F03: destructive installer swap/process scope | Rollback journal, atomic individual replacement, scoped graceful shutdown, startup acknowledgement; preserve unknown files | Installer recovery regressions and native Windows startup/update smoke job; Windows 5.1 acceptance required |
| F04: unsigned mutable download path | Per-release inspectable installer, signing/timestamp/verification gate for final binaries and installer, provenance, draft-only publication | Trusted signing credentials and publisher identity must be configured by a maintainer; existing public 1.1.0 remains unsigned |
| F05: architecture-specific coverage | Native Windows x64 and Arm64 jobs each test ZIP and single-file executables, plus isolated app/update startup | Actual GitHub Actions results must pass before merge/release; cross-publishing is not execution |
| F06: global ETW cleanup | Remove prefix-based session termination; cancel/report on owned ETW processing termination | Native multi-session acceptance required |
| F07: lost loss reports | Retain drop-frame/byte and diagnostic counters through bounded backpressure; persist incomplete-interval coverage notes; account for host-side overflow | Portable queue-pressure/IPC regressions; real Windows ETW load acceptance required |
| F08: incomplete startup rollback | Keep backups until fresh dashboard and collector acknowledge the transaction token; rollback on refusal/crash/timeout; preserve later file changes | Failure regressions and native smoke job; real prior-release/failed-start acceptance required |
| F09: excessive CI privilege/mutable actions | Read-only builds, protected signing environment, separate write-enabled release job, immutable action SHAs, provenance, draft release | Required environment reviewers/branch protections must be enabled in GitHub settings |
| F10: version/source drift | Bump to 1.1.1; embed commit/SDK/runtime/dependency inventory in ZIP | Final release tag/commit/signatures/checksums must agree |
| F11: maintainability/hidden failures | Separate file ownership/transaction/delivery code, explicit recovery state, bounded diagnostics, portable failure tests; clarify critical diagnostics failures | Incremental maintenance remains ongoing; no broad cosmetic rewrite of unrelated analytics |
| F12: chart accessibility | Keyboard point/interval selection, visible focus and read-only automation value with timestamp/series values | Native keyboard/Narrator/high-contrast/scaling acceptance required |

Additional review hypotheses: diagnostic tools now use absolute Windows system paths; different-account elevation is explicitly refused instead of applying filters/reporting under the wrong account. History clearing/capture retention and recovery limitations are documented. Interrupted journals and unrecognised legacy backups are retained for manual recovery. Only completed unchanged inventoried downloads are cleaned automatically.

See [Release checklist](Release-Checklist.md) for the exact-commit acceptance record and secure configuration needed before publication. No signing secrets, private history or audit-downloaded binaries are committed.
