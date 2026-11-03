import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import sys
from threading import Thread
import unittest


SCRIPT = Path(__file__).with_name("Validate-TelemetryOverlapReadiness.py")
TOKEN = "temporary-test-token"
COMMIT = "a" * 40


class Handler(BaseHTTPRequestHandler):
    legacy_count = 2
    redirect_health = False
    followed_redirect = False
    source_commit = COMMIT

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
            payload = {"status": "Healthy"}
        elif self.path in {"/health/ready", "/health/deploy-ready"}:
            payload = {"status": "Ready", "sourceCommit": type(self).source_commit}
        elif self.path == "/api/admin/legacy-machine-connections":
            payload = {
                "totalItems": type(self).legacy_count,
                "relay": {"configured": True, "healthy": True},
            }
        elif self.path == "/api/admin/worker-health":
            payload = {
                "queueMetrics": {
                    "machineConnections": {
                        "legacyRelay": {"eligibleConnections": type(self).legacy_count, "healthy": True},
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
        Handler.redirect_health = False
        Handler.followed_redirect = False
        Handler.source_commit = COMMIT

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

    def test_blocks_when_no_legacy_machine_connection_is_observed(self):
        Handler.legacy_count = 0

        result = self.run_preflight()

        self.assertEqual(result.returncode, 1)
        self.assertIn("mínimo exigido: 1", result.stderr)

    def test_requires_full_git_commit_sha(self):
        command = [
            sys.executable,
            str(SCRIPT),
            "--api-base-url",
            self.base_url,
            "--expected-commit",
            "a" * 39,
        ]
        import os

        env = os.environ.copy()
        env["VENDMACHINES_ADMIN_BEARER_TOKEN"] = TOKEN
        result = subprocess.run(command, capture_output=True, text=True, env=env, check=False)

        self.assertEqual(result.returncode, 2)
        self.assertIn("SHA Git completo", result.stderr)

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
