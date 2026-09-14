# Vencord Auto Update

**Lifecycle revision:** The event-driven Windows service supersedes the original resident tray/logon-task design. The implemented service and bounded-worker lifecycle is summarized below; archive/repair safeguards remain binding.

## Purpose and scope

An event-driven Windows service and ordinary-user worker restore Vencord's loader after a Discord host update removes it. It repairs the injection; Vencord remains responsible for updating its own plugins and bundle. It uses an existing local Vencord installation, works offline, and ships no Discord or Vencord binaries. The repository uses its existing GPL-3.0 license.

Support ordinary per-user Windows 10/11 Discord Stable, PTB and Canary installs. Use C# compatible with the Windows .NET Framework compiler and .NET Framework 4.8, Windows Forms, and PowerShell packaging scripts; no NuGet dependencies. No hardcoded user paths. Unsupported layouts, unknown archives, or missing Vencord files produce actionable status rather than speculative patching.

Vencord already has a best-effort before-quit persistence hook. This helper supplies independent recovery when that hook does not finish. The exact cause of the reported incident remains unproven. The architecture was reviewed with Claude Opus 5 at maximum effort; the completed review agreed with the following constraints.

## Behavior

A protected LocalSystem broker starts automatically and waits on directory/session notifications and deferred process exits. Healthy idle has no recurring polling, hashing, WMI subscription or tray. Only the fixed adjacent ordinary-user worker reads policy and repairs files; elevated workers/UI are rejected. Coalesce events, cap per-SID launches at one per ten seconds, and permit at most three 60-second retry timers per burst. Workers have a 180-second budget and drain entered archive transactions; service shutdown reports a drain overrun after 225 seconds and remains STOP_PENDING until owned work exits. Setup refuses binary replacement after its separate 240-second wait; retry after the pending stop completes. Closing on-demand Status exits the UI while service protection continues. Never alter Discord shortcuts, startup entries, settings, themes or accounts; never download automatically.

Discover channels separately and sort app-* versions numerically. Cache a tiny, recognized file-form app.asar loader from an existing healthy Vencord installation. Validate its archive structure, package entry, exact require-only entrypoint pointing to the expected local Vencord dist/patcher.js, and required bundle files. Cache under the helper's own user data directory and check its hash before use. Never accept arbitrary JavaScript as a loader.

Classify each current installation as healthy, stock, interrupted (valid original _app.asar with missing app.asar), unsettled, or unsupported. An original archive must parse as the recognized Discord bootstrap and contain its referenced main file. A loader in _app.asar, two differing originals, old directory-form loaders, reparse points or unexpected content must stop automatic repair. Never replace an existing _app.asar.

Require stable installation metadata observations at least 10 seconds apart, complete expected files, no updater under the root, and no remaining Discord processes from any version under that exact root before disk changes. Discord's native updater can run inside Discord.exe; absence of Update.exe alone is insufficient. Quiet metadata is a heuristic, not proof of every possible update having completed.

Write a flushed transaction journal and adjacent flushed temporary loader. Recheck the candidate before mutation. Move stock app.asar to _app.asar using a no-replace, write-through rename, then move the loader into app.asar the same way. Preserve the original hash and verify resulting loader references. Recover an interrupted transaction or built-in rename without overwriting concurrent content. Rollback may only reverse a proven helper-owned intermediate state. Unknown state retains all evidence and reports the problem.

Persist repair attempt and restart decisions per generation (root/version/original hash), with bounded retry and cooldown. Own writes must not create a fresh restart allowance. Permit at most one automatic restart per generation and one globally per 10 minutes. A failed post-restart verification stops automatic restarting.

If an unpatched generation is first seen with the main Discord process at most 3 minutes old, show a visible 20-second cancelable restart countdown after settling, using monotonic elapsed time starting when the dialog is shown. Closing the dialog or choosing Not now defers that generation. Explain that the restart closes Discord and could interrupt a call or draft. Revalidate eligibility at countdown completion; stop only exact-path Discord processes, wait for full exit, recheck state, repair, then relaunch through Update.exe --processStart <channel executable>. Never launch a version-specific executable. If quiescence or repair fails after stopping Discord, make a bounded attempt to reopen through the same launcher and show the failure.

For an established session, cancellation, disabled automatic restarts, or locked/noninteractive session, defer until Discord fully exits and provide Repair & Restart in on-demand Status. Do not stop a running session silently. A first post-update launch may briefly run stock Discord; deferred means it remains stock until a repaired restart. File verification does not establish runtime injection; confirm the Vencord settings page manually after a real update.

Provide status, pause/resume, automatic-restart toggle, explicit confirmed repair/restart, logs, official Vencord link and GPL information. No-argument launch opens ordinary-user Status; --worker requests bounded reconciliation, --status is read-only, and --stop drains only the exact same-user/data instance. An open manual UI accepts same-user work requests without perpetual idle scanning. The fixed `--worker` relay starts an ordinary `--worker-host` candidate and acknowledges its actual batch outcome independently of Status lifetime in either invocation order. Status requires actual show acknowledgement or takes the instance after an exiting host releases it, so a completion race cannot silently lose the window. Only the winning exact SID/data host owns consent, transactions and IPC; unrequested hosts exit after five seconds. Requests temporarily use an extra ordinary process. Deferred masks 65..71 identify only Stable 1 / PTB 2 / Canary 4; the service waits for each hinted root independently and bounds inconsistent empty hints to one immediate follow-up plus three delayed retries.

UAC setup installs both binaries and complete setup modules below native Program Files, an exact automatic LocalSystem service with a quoted no-argument ImagePath and protected ACL, and a common Start menu shortcut. Machine-wide serialized setup verifies manifests and old/new snapshots, drains service work, and refuses any in-use EXE (including idle Status) instead of force-killing it. Crash failure actions end with NONE after three 60-second restarts; they do not change user policy. Interrupted updates/uninstall restore verified previous ownership. Unknown content and cleanup failures retain evidence. Uninstall preserves foreign neighbors and every user's runtime data without parsing it. There is no released legacy task migration. See README and CONTRIBUTING for exact setup/fixture contracts.

## Validation and distribution

Use a dependency-free executable test harness and synthetic ASAR fixtures. Test healthy no-op, numeric versions, changed/same-version stock archives, partial writes, no-overwrite races, crash between renames, interrupted built-in hook, malformed or unknown loaders, missing assets, old-version process blocking, cooldown/restart-once, session reconciliation and cancellation. Run process and setup integration against isolated fixture directories and processes owned by the tests. Never stop or mutate the developer's real Discord installation.

Build and test on Windows CI. Publish a versioned ZIP containing both executables, all seven setup modules, five public documents and SOURCE provenance, with an external checksum. Actual administrator SCM tests are gated to isolated GitHub CI; distinguish them from notification fixture-host and WTS user-launch evidence. Release builds remain unsigned until signing is configured; document the provenance check without telling users to disable protections. Source builds use scripts/build.ps1 and scripts/test.ps1. No telemetry, credentials, machine-specific runtime state, or third-party binaries belong in Git.

## Sources

- [Vencord persistence hook](https://github.com/Vendicated/Vencord/blob/main/src/main/persistAfterDiscordUpdates.ts)
- [Official installer archive format](https://github.com/Vencord/Installer/blob/v1.4.0/app_asar.go)
- [Official installer patching](https://github.com/Vencord/Installer/blob/v1.4.0/patcher.go)
- [Electron app lifecycle](https://www.electronjs.org/docs/latest/api/app)
