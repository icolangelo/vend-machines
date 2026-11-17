import json
import unittest
from io import BytesIO
from urllib.error import HTTPError

from ResolveProductionApiRevision import resolve_revision

LIVE_SHA = "a" * 40
BOOTSTRAP_SHA = "b" * 40
HEALTH_URL = "https://api.vendmachine.com.br/health/ready"
DEPLOY_READY_URL = "https://api.vendmachine.com.br/health/deploy-ready"


class FakeResponse:
    status = 200

    def __init__(self, payload):
        self.payload = json.dumps(payload).encode()

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self, _limit):
        return self.payload


class ProductionApiRevisionTests(unittest.TestCase):
    def test_uses_the_commit_reported_by_a_ready_production_api(self):
        result = resolve_revision(
            HEALTH_URL,
            DEPLOY_READY_URL,
            BOOTSTRAP_SHA,
            lambda *_args, **_kwargs: FakeResponse({"status": "Ready", "sourceCommit": LIVE_SHA}),
        )
        self.assertEqual((LIVE_SHA, "live"), result)

    def test_uses_bootstrap_ref_only_when_both_old_api_endpoints_return_404(self):
        def missing_endpoints(*_args, **_kwargs):
            raise HTTPError(HEALTH_URL, 404, "Not Found", {}, BytesIO(b""))

        self.assertEqual(
            (BOOTSTRAP_SHA, "bootstrap"),
            resolve_revision(HEALTH_URL, DEPLOY_READY_URL, BOOTSTRAP_SHA, missing_endpoints),
        )

    def test_rejects_partial_bootstrap_when_only_readiness_route_is_missing(self):
        responses = iter([
            HTTPError(HEALTH_URL, 404, "Not Found", {}, BytesIO(b"")),
            FakeResponse({"status": "Ready", "sourceCommit": LIVE_SHA}),
        ])

        def partial_bootstrap(*_args, **_kwargs):
            result = next(responses)
            if isinstance(result, Exception):
                raise result
            return result

        with self.assertRaisesRegex(ValueError, "bootstrap parcial"):
            resolve_revision(HEALTH_URL, DEPLOY_READY_URL, BOOTSTRAP_SHA, partial_bootstrap)

    def test_fails_closed_when_production_is_not_ready(self):
        with self.assertRaisesRegex(ValueError, "não está Ready"):
            resolve_revision(
                HEALTH_URL,
                DEPLOY_READY_URL,
                BOOTSTRAP_SHA,
                lambda *_args, **_kwargs: FakeResponse({"status": "NotReady", "sourceCommit": LIVE_SHA}),
            )

    def test_fails_closed_when_ready_endpoint_has_no_valid_source_commit(self):
        with self.assertRaisesRegex(ValueError, "sourceCommit"):
            resolve_revision(
                HEALTH_URL,
                DEPLOY_READY_URL,
                BOOTSTRAP_SHA,
                lambda *_args, **_kwargs: FakeResponse({"status": "Ready", "sourceCommit": "unknown"}),
            )

    def test_fails_closed_for_non_bootstrap_http_errors(self):
        def unavailable(*_args, **_kwargs):
            raise HTTPError(HEALTH_URL, 503, "Service Unavailable", {}, BytesIO(b""))

        with self.assertRaisesRegex(ValueError, "HTTP 503"):
            resolve_revision(HEALTH_URL, DEPLOY_READY_URL, BOOTSTRAP_SHA, unavailable)

    def test_rejects_insecure_or_wrong_health_urls(self):
        for url in ("http://api.vendmachine.com.br/health/ready", "https://api.vendmachine.com.br/health"):
            with self.subTest(url=url), self.assertRaisesRegex(ValueError, "HTTPS"):
                resolve_revision(url, DEPLOY_READY_URL, BOOTSTRAP_SHA, lambda *_args, **_kwargs: None)

    def test_rejects_deploy_readiness_url_on_a_different_host(self):
        with self.assertRaisesRegex(ValueError, "mesmo host"):
            resolve_revision(
                HEALTH_URL,
                "https://other.example/health/deploy-ready",
                BOOTSTRAP_SHA,
                lambda *_args, **_kwargs: None,
            )

    def test_rejects_invalid_bootstrap_sha(self):
        with self.assertRaisesRegex(ValueError, "bootstrap"):
            resolve_revision(HEALTH_URL, DEPLOY_READY_URL, "6018ead", lambda *_args, **_kwargs: None)


if __name__ == "__main__":
    unittest.main()
