"""Regression tests for linux/wsl-tray-watchdog.

The watchdog is a POSIX sh script run as a systemd oneshot under a timer, so a
failure produced no output at all: journald only recorded "Failed to execute"
with no service or container named. These tests pin the reporting behaviour by
running the real script against stub PATH commands, so no systemd or docker is
touched on the developer's machine.

Run with: python3 -m unittest discover -s tests -p 'test_watchdog.py' -v
"""

import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
WATCHDOG = REPO_ROOT / "linux" / "wsl-tray-watchdog"
SH = shutil.which("sh") or "/bin/sh"


def write_stub(directory: Path, name: str, body: str) -> Path:
    path = directory / name
    path.write_text("#!/bin/sh\n" + body + "\n", encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    return path


class WatchdogTestCase(unittest.TestCase):
    """Base class that builds a throwaway PATH with stub systemctl/docker."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="watchdog-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.bin_dir = self.tmp / "bin"
        self.bin_dir.mkdir()

        # logger exists and is a no-op unless a test overrides it.
        self.logged = self.tmp / "logger.log"
        write_stub(self.bin_dir, "logger", f'echo "$*" >> "{self.logged}"')

        self.config = self.tmp / "watchdog.conf"
        # Point the script at our temporary config by rewriting the constant.
        self.script = self.tmp / "watchdog-under-test"
        source = WATCHDOG.read_text(encoding="utf-8")
        self.assertIn("CONFIG_FILE=/etc/default/wsl-keepalive-tray", source)
        self.script.write_text(
            source.replace(
                "CONFIG_FILE=/etc/default/wsl-keepalive-tray",
                f"CONFIG_FILE={self.config}",
            ),
            encoding="utf-8",
        )
        self.script.chmod(0o755)

    def write_config(self, services: str = "", containers: str = "") -> None:
        self.config.write_text(
            f'WATCHDOG_SERVICES="{services}"\nWATCHDOG_CONTAINERS="{containers}"\n',
            encoding="utf-8",
        )

    def run_watchdog(self, *, with_docker: bool = True, strip_path: bool = False):
        # `sh` must stay resolvable even when PATH is narrowed to prove that
        # the script copes with a missing docker, so pass an absolute path.
        env = dict(os.environ)
        if strip_path:
            # No system PATH at all: `sh` still runs (absolute), but the script
            # can find neither docker nor logger.
            env["PATH"] = self.bin_dir.as_posix()
        else:
            env["PATH"] = f"{self.bin_dir}:{env['PATH']}"
        if not with_docker:
            # Keep sh/logger reachable but hide the real docker binary.
            shim = self.tmp / "path"
            shim.mkdir(exist_ok=True)
            for tool in ("sh", "logger", "systemctl"):
                target = shutil.which(tool, path=env["PATH"])
                if target:
                    (shim / tool).symlink_to(target)
            env["PATH"] = shim.as_posix()
        return subprocess.run(
            [SH, str(self.script)],
            capture_output=True,
            text=True,
            env=env,
            timeout=30,
        )

    def syslog_text(self) -> str:
        return self.logged.read_text(encoding="utf-8") if self.logged.exists() else ""


class WatchdogReporting(WatchdogTestCase):
    def test_success_reports_passed_and_names_scope(self):
        self.write_config(services="docker.service", containers="")
        write_stub(self.bin_dir, "systemctl", 'exit 0')

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("health check started", result.stdout)
        self.assertIn("health check passed", result.stdout)
        self.assertIn("service docker.service: active", result.stdout)
        self.assertNotIn("FAILED", result.stdout)

    def test_absent_unit_is_skipped_not_failed(self):
        """A unit that does not exist must not be counted as a failure."""
        self.write_config(services="not-installed.service", containers="")
        # `systemctl cat` failing is how the script detects a missing unit.
        write_stub(
            self.bin_dir,
            "systemctl",
            'if [ "$1" = cat ]; then exit 1; fi\n'
            'if [ "$1" = start ]; then echo "should not start"; exit 0; fi\n'
            "exit 0",
        )

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("skip not-installed.service: unit not present", result.stdout)
        self.assertNotIn("start not-installed.service", result.stdout)

    def test_failed_start_names_the_service_and_exits_nonzero(self):
        self.write_config(services="broken.service", containers="")
        write_stub(
            self.bin_dir,
            "systemctl",
            'if [ "$1" = cat ]; then exit 0; fi\n'
            'if [ "$1" = is-active ]; then exit 1; fi\n'
            'if [ "$1" = start ]; then exit 1; fi\n'
            "exit 0",
        )

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 1)
        self.assertIn("start broken.service: FAILED", result.stdout)
        self.assertIn("health check FAILED", result.stdout)

    def test_service_that_dies_after_start_is_reported(self):
        self.write_config(services="flaky.service", containers="")
        write_stub(
            self.bin_dir,
            "systemctl",
            'if [ "$1" = cat ]; then exit 0; fi\n'
            'if [ "$1" = start ]; then exit 0; fi\n'
            'if [ "$1" = is-active ]; then exit 1; fi\n'
            "exit 0",
        )

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 1)
        self.assertIn("start flaky.service: ok", result.stdout)
        self.assertIn("service flaky.service: FAILED to stay active", result.stdout)

    def test_missing_container_is_named(self):
        self.write_config(services="", containers="ghost")
        write_stub(self.bin_dir, "systemctl", "exit 0")
        write_stub(self.bin_dir, "docker", 'exit 1')  # inspect fails

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 1)
        self.assertIn("container ghost: FAILED (not found)", result.stdout)

    def test_stopped_container_is_started_then_verified(self):
        self.write_config(services="", containers="api")
        write_stub(self.bin_dir, "systemctl", "exit 0")
        state = self.tmp / "state"
        write_stub(
            self.bin_dir,
            "docker",
            'if [ "$1" = inspect ] && [ "$2" = --format ]; then\n'
            f'  if [ -f "{state}" ]; then echo true; else echo false; fi\n'
            f'  exit 0\n'
            "fi\n"
            'if [ "$1" = inspect ]; then exit 0; fi\n'
            'if [ "$1" = start ]; then touch ' + str(state) + '; exit 0; fi\n'
            "exit 0",
        )

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("start container api: was not running", result.stdout)
        self.assertIn("start container api: ok", result.stdout)
        self.assertIn("container api: running", result.stdout)

    def test_container_that_will_not_start_is_reported(self):
        self.write_config(services="", containers="wedged")
        write_stub(self.bin_dir, "systemctl", "exit 0")
        write_stub(
            self.bin_dir,
            "docker",
            'if [ "$1" = inspect ] && [ "$2" = --format ]; then echo false; exit 0; fi\n'
            'if [ "$1" = inspect ]; then exit 0; fi\n'
            'if [ "$1" = start ]; then exit 1; fi\n'
            "exit 0",
        )

        result = self.run_watchdog()

        self.assertEqual(result.returncode, 1)
        self.assertIn("start container wedged: FAILED", result.stdout)

    def test_containers_requested_but_docker_missing_fails(self):
        self.write_config(services="", containers="api")

        result = self.run_watchdog(with_docker=False)

        self.assertEqual(result.returncode, 1)
        self.assertIn("containers requested but docker is unavailable", result.stdout)

    def test_output_also_reaches_syslog(self):
        self.write_config(services="docker.service", containers="")
        write_stub(self.bin_dir, "systemctl", "exit 0")

        self.run_watchdog()

        logged = self.syslog_text()
        self.assertIn("health check passed", logged)
        self.assertIn("docker.service", logged)

    def test_syslog_tolerates_missing_logger(self):
        """A minimal environment without logger must still run."""
        self.write_config(services="docker.service", containers="")
        write_stub(self.bin_dir, "systemctl", "exit 0")
        (self.bin_dir / "logger").unlink()

        result = self.run_watchdog(strip_path=True)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("health check passed", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
