# Security Policy

ParentalGuard protects children by relying on Windows account permissions, **not** on secrecy.
The source being open does not weaken it: the enforcement holds because the child runs as a
Standard user and the Service runs as LocalSystem.

## Reporting a bypass or vulnerability

If you find a way for a Standard-user child account to disable enforcement, uninstall the
Service, mint extra screen time, or evade the filter, please report it **privately** first,
so families relying on the tool aren't exposed before a fix ships.

- Open a GitHub **private security advisory** on this repository, **or**
- email the maintainer (see the repo's profile).

Please include your Windows version, the account type you tested from, and clear steps.

## Out of scope

- Anything requiring Administrator rights on the child's account. On Windows, an administrator
  can stop any software; the documented setup requires the child to be a Standard user.
- Bypasses that require physical BIOS/boot access already covered by the hardening guide
  (a BIOS password and disabled USB/boot-order are the parent's responsibility).

## Our commitments

- No telemetry, no cloud, no data leaves the machine.
- No keylogging, screenshots, or message capture — ever. PRs adding these will be declined.
