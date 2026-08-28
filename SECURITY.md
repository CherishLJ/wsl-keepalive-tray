# Security Policy

## Reporting a vulnerability

Please do not publish exploit details in a public issue. Use GitHub's private
vulnerability reporting feature for this repository, or contact the repository
owner through the email address listed on their GitHub profile.

Include the affected version, reproduction steps, expected impact, and any
suggested mitigation. Reports will be acknowledged as soon as practical.

## Scope

The application launches `wsl.exe`, installs root-owned scripts and systemd
units inside the selected distro, and stores a current-user autostart entry.
Review installer changes before running code from an untrusted fork.
