# Changelog

## 0.1.3

Healthy idle no longer wakes the worker for unrelated activity. The service watches the folders above each Discord and Vencord root so it can see a root appear or disappear, but it also treated every timestamp change on those folders as relevant. Other apps adding or removing files directly in LocalAppData, AppData or the profile therefore launched a worker (at most one per ten seconds). Ancestor folders now count only when they are created, deleted or renamed. Found on a real installation, where an unrelated burst caused eleven idle worker runs in two minutes.

## 0.1.2

Setup and the service now name the exact file or folder when they refuse an ownership or permission check, with the action to take. A real installation was refused because extra user grants on the all-users Start Menu folder made the shortcut location user-deletable, and the old message did not say where.

## 0.1.1

Reliability and performance release.

- Installation metadata scans enumerate each directory once instead of querying every entry's ancestors. On a real Discord Stable install one scan dropped from about 1.9 seconds to about 21 milliseconds, keeping the restart countdown and Status responsive during repair.
- Archive renames hash and rename one held file handle, so the renamed bytes are exactly the validated bytes.
- A repair interrupted after its final rename removes its own transaction journal, reading and deleting the same held file.
- One unsupported channel root (for example a junction) or one failed directory subscription is refused alone instead of blocking the other channels, in both the worker and the service. A corrected root is picked up without restarting Status.
- State never saves a file larger than it can read back, persisted process cohorts are fixed-size digests, and a full generation history retires only versions that no longer exist and no longer limit restarts.
- A failed stop, repair, relaunch or post-restart check after a visible countdown opens Status, and the reason stays listed after later scans.
- Setup publishes its commit marker by rename, creates the service demand-start until fully configured, and recovers a service whose creation or first start was interrupted.

## 0.1.0

Initial Windows release: event-driven LocalSystem broker, bounded ordinary-user repair workers, on-demand Status / Setup, protected UAC machine installation with verified interruption recovery, and two-executable source/checksum packaging. Recognized local-loader caching, no-overwrite archive transactions, visible cancelable restart consent and durable repair limits remain in the ordinary worker. No resident tray, per-user logon task, telemetry or automatic downloads. Releases are unsigned.
