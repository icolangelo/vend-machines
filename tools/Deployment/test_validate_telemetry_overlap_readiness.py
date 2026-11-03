import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from threading import Thread
import unittest


SCRIPT = Path(__file__).with_name("Validate-TelemetryOverlapReadiness.py")
TOKEN = "temporary-test-token"
COMMIT = "a" * 40


class Handler(BaseHTTPRequestHandler):
    legacy_count = 2
    legacy_count_sequence = None
    legacy_reads = 0
    current_legacy_count = 2
    redirect_health = False
    followed_redirect = False
    source_commit = COMMIT
    health_clock_offset_seconds = 0

    def date_time_string(self, timestamp=None):
        import time

        if timestamp is None:
            timestamp = time.time()
        timestamp += type(self).health_clock_offset_seconds
        return super().date_time_string(timestamp)

    def do_GET(self):
        if self.path == "/health" and type(self).redirect_health:
            self.send_response(302)
            self.send_header("Location", "/redirect-target")
            self.end_headers()
            return
        if self.path == "/redirect-target":
            type(self).followed_redirect = True
        if self.path.startswith("/api/admin/") and self.headers.get("Authorization") != f"Bearer {TOKEN}":
            self.send_response(401)
            self.end_headers()
            return

        if self.path == "/health":
            server_now = datetime.now(timezone.utc) + timedelta(
                seconds=type(self).health_clock_offset_seconds
            )
            payload = {
                "status": "Healthy",
                "timestamp": server_now.isoformat(timespec="milliseconds").replace("+00:00", "Z"),
            }
        elif self.path in {"/health/ready", "/health/deploy-ready"}:
            payload = {"status": "Ready", "sourceCommit": type(self).source_commit}
        elif self.path == "/api/admin/legacy-machine-connections":
            sequence = type(self).legacy_count_sequence
            if sequence:
                index = min(type(self).legacy_reads, len(sequence) - 1)
                type(self).current_legacy_count = sequence[index]
                type(self).legacy_reads += 1
            else:
                type(self).current_legacy_count = type(self).legacy_count
            payload = {
                "totalItems": type(self).current_legacy_count,
                "relay": {"configured": True, "healthy": True},
            }
        elif self.path == "/api/admin/worker-health":
            payload = {
                "queueMetrics": {
                    "machineConnections": {
                        "legacyRelay": {"eligibleConnections": type(self).current_legacy_count, "healthy": True},
                        "readinessGate": {"unroutable": 0},
                    }
                }
            }
        else:
            self.send_response(404)
            self.end_headers()
            return

        body = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


class OverlapReadinessPreflightTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.thread = Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base_url = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)

    def setUp(self):
        Handler.legacy_count = 2
        Handler.legacy_count_sequence = None
        Handler.legacy_reads = 0
        Handler.current_legacy_count = 2
        Handler.redirect_health = False
        Handler.followed_redirect = False
        Handler.source_commit = COMMIT
        Handler.health_clock_offset_seconds = 0

    def run_preflight(self, *extra_args):
        import os

        env = os.environ.copy()
        env["VENDMACHINES_ADMIN_BEARER_TOKEN"] = TOKEN
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                "--api-base-url",
                self.base_url,
                "--expected-commit",
                COMMIT,
                "--minimum-legacy-connections",
                "2",
                *extra_args,
            ],
            capture_output=True,
            text=True,
            env=env,
            check=False,
        )

    def test_accepts_healthy_relay_and_matching_legacy_connection_metrics(self):
        result = self.run_preflight()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("overlap_runtime_preflight=passed", result.stdout)
        self.assertIn("legacy_connections_preserved=2", result.stdout)
        self.assertNotIn(TOKEN, result.stdout + result.stderr)

    def test_blocks_when_local_clock_is_out_of_sync_with_api(self):
        Handler.health_clock_offset_seconds = 120

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("diferença entre o relógio da API e o relógio local", result.stderr)
        self.assertIn("máximo permitido: 30s", result.stderr)

    def test_reports_api_clock_skew_for_each_sample(self):
        result = self.run_preflight()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertRegex(result.stdout, r"api_clock_skew_seconds=[+-]?\d+\.\d")

    def test_observes_legacy_connections_repeatedly_for_requested_window(self):
        result = self.run_preflight(
            "--observation-seconds",
            "0.3",
            "--sample-interval-seconds",
            "0.02",
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("observation_samples=1\n", result.stdout)
        self.assertIn("observation_seconds=0.3", result.stdout)
        self.assertRegex(result.stdout, r"observed_at_utc=\d{4}-\d{2}-\d{2}T[^ ]+Z")

    def test_blocks_when_legacy_connections_drop_during_observation(self):
        Handler.legacy_count_sequence = [2, 1]

        result = self.run_preflight(
            "--observation-seconds",
            "0.5",
            "--sample-interval-seconds",
            "0.01",
        )

        self.assertEqual(result.returncode, 1)
        self.assertIn("Foram observadas 1 conexões legadas", result.stderr)

    def test_rejects_negative_observation_duration(self):
        result = self.run_preflight("--observation-seconds", "-1")

        self.assertEqual(result.returncode, 2)
        self.assertIn("finito e não negativo", result.stderr)

    def test_rejects_non_finite_observation_and_request_timeouts(self):
        for option, value in (
            ("--observation-seconds", "nan"),
            ("--sample-interval-seconds", "inf"),
            ("--timeout-seconds", "nan"),
            ("--maximum-clock-skew-seconds", "inf"),
        ):
            with self.subTest(option=option, value=value):
                result = self.run_preflight(option, value)

                self.assertEqual(result.returncode, 2)
                self.assertIn("finito", result.stderr)

    def test_blocks_when_no_legacy_machine_connection_is_observed(self):
        Handler.legacy_count = 0

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("mínimo exigido: 2", result.stderr)

    def test_blocks_boolean_connection_count_instead_of_treating_it_as_one(self):
        Handler.legacy_count = True

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("formato inesperado", result.stderr)

    def test_rejects_invalid_url_before_requesting_admin_token(self):
        command = [
            sys.executable,
            str(SCRIPT),
            "--api-base-url",
            "https://[invalid-ipv6/",
            "--expected-commit",
            COMMIT,
            "--minimum-legacy-connections",
            "1",
        ]
        import os

        env = os.environ.copy()
        env.pop("VENDMACHINES_ADMIN_BEARER_TOKEN", None)
        result = subprocess.run(command, capture_output=True, text=True, env=env, check=False)

        self.assertEqual(result.returncode, 2)
        self.assertIn("host ou porta inválidos", result.stderr)
        self.assertNotIn("Token bearer", result.stderr)

    def test_requires_full_git_commit_sha(self):
        command = [
            sys.executable,
            str(SCRIPT),
            "--api-base-url",
            self.base_url,
            "--expected-commit",
            "a" * 39,
            "--minimum-legacy-connections",
            "1",
        ]
        import os

        env = os.environ.copy()
        env["VENDMACHINES_ADMIN_BEARER_TOKEN"] = TOKEN
        result = subprocess.run(command, capture_output=True, text=True, env=env, check=False)

        self.assertEqual(result.returncode, 2)
        self.assertIn("SHA Git completo", result.stderr)

    def test_requires_an_explicit_fleet_connection_minimum(self):
        command = [
            sys.executable,
            str(SCRIPT),
            "--api-base-url",
            self.base_url,
            "--expected-commit",
            COMMIT,
        ]
        import os

        env = os.environ.copy()
        env["VENDMACHINES_ADMIN_BEARER_TOKEN"] = TOKEN
        result = subprocess.run(command, capture_output=True, text=True, env=env, check=False)

        self.assertEqual(result.returncode, 2)
        self.assertIn("--minimum-legacy-connections", result.stderr)

    def test_rejects_commit_with_matching_prefix_but_different_revision(self):
        Handler.source_commit = "a" * 39 + "b"

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("sourceCommit não corresponde", result.stderr)

    def test_does_not_follow_redirects(self):
        Handler.redirect_health = True

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("redirects não são seguidos", result.stderr)
        self.assertFalse(Handler.followed_redirect)


if __name__ == "__main__":
    unittest.main()
