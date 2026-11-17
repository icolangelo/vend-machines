import json
import unittest
from io import BytesIO
from urllib.error import HTTPError

from UpdateProductionBackendRevision import update_repository_variable

REVISION = "a" * 40
OLD_REVISION = "b" * 40
REPOSITORY = "owner/repository"
TOKEN = "synthetic-test-token"


class FakeResponse:
    def __init__(self, status, payload=None):
        self.status = status
        self.content = json.dumps(payload or {}).encode()

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self, _limit):
        return self.content


class UpdateProductionBackendRevisionTests(unittest.TestCase):
    def test_updates_existing_variable_and_reads_back_the_new_value(self):
        calls = []
        values = iter([
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": OLD_REVISION}),
            FakeResponse(204),
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": REVISION}),
        ])

        def send(request, **_kwargs):
            calls.append(request)
            return next(values)

        update_repository_variable(REPOSITORY, TOKEN, REVISION, send)
        self.assertEqual(["GET", "PATCH", "GET"], [call.get_method() for call in calls])
        self.assertEqual("PRODUCTION_BACKEND_REF", calls[1].full_url.rsplit("/", 1)[-1])
        self.assertEqual({"value": REVISION}, json.loads(calls[1].data))

    def test_creates_variable_when_bootstrap_reference_does_not_exist(self):
        calls = []
        values = iter([
            HTTPError("https://api.github.com/example", 404, "Not Found", {}, BytesIO(b"")),
            FakeResponse(201),
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": REVISION}),
        ])

        def send(request, **_kwargs):
            calls.append(request)
            result = next(values)
            if isinstance(result, Exception):
                raise result
            return result

        update_repository_variable(REPOSITORY, TOKEN, REVISION, send)
        self.assertEqual(["GET", "POST", "GET"], [call.get_method() for call in calls])
        self.assertEqual(
            {"name": "PRODUCTION_BACKEND_REF", "value": REVISION},
            json.loads(calls[1].data),
        )

    def test_does_not_write_when_the_variable_is_already_current(self):
        calls = []
        values = iter([
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": REVISION}),
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": REVISION}),
        ])

        def send(request, **_kwargs):
            calls.append(request)
            return next(values)

        update_repository_variable(REPOSITORY, TOKEN, REVISION, send)
        self.assertEqual(["GET", "GET"], [call.get_method() for call in calls])

    def test_fails_if_github_does_not_confirm_the_deployed_revision(self):
        values = iter([
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": OLD_REVISION}),
            FakeResponse(204),
            FakeResponse(200, {"name": "PRODUCTION_BACKEND_REF", "value": OLD_REVISION}),
        ])

        with self.assertRaisesRegex(ValueError, "não confirmou a revisão"):
            update_repository_variable(REPOSITORY, TOKEN, REVISION, lambda *_args, **_kwargs: next(values))

    def test_rejects_invalid_repository_token_and_revision(self):
        for repository, token, revision in (
            ("owner", TOKEN, REVISION),
            (REPOSITORY, "", REVISION),
            (REPOSITORY, TOKEN, "short-sha"),
        ):
            with self.subTest(repository=repository, revision=revision), self.assertRaises(ValueError):
                update_repository_variable(repository, token, revision, lambda *_args, **_kwargs: None)


if __name__ == "__main__":
    unittest.main()
