# Security

Report security issues privately through GitHub's repository security advisory feature when available. Do not include tokens, private messages or unredacted profile paths. Obtain releases from this repository and verify the checksum and matching source tag. Releases are unsigned; there is no automatic update or download channel.

The LocalSystem service only brokers known Windows sessions, notifications and fixed adjacent ordinary-user worker launches. It never interprets user archives or policy as privileged authority. Installed binaries, setup modules, manifests and ancestor ACLs must remain administrator protected. The worker/UI rejects elevated and service identities; read-only `--status` is permitted. No user-selected command/path IPC reaches SYSTEM.

UAC authorizes the extracted setup scripts. Verify the full release before approving it. Setup checks exact machine ownership and effective service configuration before changing it, refuses foreign/modified resources, and preserves recovery evidence when proof is incomplete. It also refuses writable destination descendants and unowned runtime sidecars that could influence CLR/native loading. It never executes a legacy user-writable helper elevated, force-kills an active repair, or parses/removes user runtime data. Do not change protections or delete ambiguous recovery records merely to force setup to proceed.
