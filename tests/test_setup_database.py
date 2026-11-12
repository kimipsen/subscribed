import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("setup_database", ROOT / "scripts/setup-database.py")
setup = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(setup)


class DatabaseSetupTests(unittest.TestCase):
    def setUp(self):
        self.values = {
            "POSTGRES_USER": "admin", "POSTGRES_PASSWORD": "admin-secret",
            "POSTGRES_DB": "admin_db",
            "SUBSCRIBED_APP_PASSWORD": "app's;secret",
            "SUBSCRIBED_MIGRATOR_PASSWORD": 'migrator"secret',
        }

    def test_dotenv_values_are_literal_and_support_quotes_comments_and_export(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / ".env"
            path.write_text(
                "# comment\nexport POSTGRES_USER=admin # comment\n"
                "PASSWORD='a $b $(touch /not-executed) # c'\n"
                'OTHER="a;b=c" # trailing comment\nEMPTY=\n',
            )
            with patch.object(setup, "ROOT", Path(folder)):
                values = setup.read_env(path)
        self.assertEqual(values["POSTGRES_USER"], "admin")
        self.assertEqual(values["PASSWORD"], "a $b $(touch /not-executed) # c")
        self.assertEqual(values["OTHER"], "a;b=c")
        self.assertEqual(values["EMPTY"], "")

    def test_dotenv_rejects_malformed_and_duplicate_assignments_without_echoing_values(self):
        for content in ("PASSWORD='secret", "bad assignment secret", "A=one\nA=secret"):
            with self.subTest(content=content), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / ".env"
                path.write_text(content)
                with patch.object(setup, "ROOT", Path(folder)):
                    with self.assertRaises(ValueError) as error:
                        setup.read_env(path)
                self.assertNotIn("secret", str(error.exception))

    def test_dotenv_rejects_outside_paths_other_filenames_and_symlink_escapes(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            repository = folder / "repository"
            repository.mkdir()
            outside = folder / ".env"
            outside.write_text("PASSWORD=secret")
            other = repository / "settings.txt"
            other.write_text("PASSWORD=secret")
            link = repository / ".env"
            link.symlink_to(outside)
            with patch.object(setup, "ROOT", repository):
                for path in (outside, other, link, repository):
                    with self.subTest(path=path), self.assertRaises(ValueError):
                        setup.read_env(path)

    def test_connection_string_quotes_special_characters(self):
        self.assertIn('Password="a;""b\'c"', setup.connection_string("localhost", "5432", "db", "app", 'a;"b\'c'))
        self.assertEqual(setup.literal("a'b"), "'a''b'")

    @patch.object(setup, "run")
    def test_invalid_settings_fail_before_running_commands(self, run):
        for changes in (
            {"SUBSCRIBED_APP_PASSWORD": ""},
            {"SUBSCRIBED_APP_PASSWORD": "CHANGE_ME_APP_PASSWORD"},
            {"SUBSCRIBED_DB": "postgres"}, {"SUBSCRIBED_DB": "admin_db"},
            {"SUBSCRIBED_DB": "bad;name"}, {"POSTGRES_PORT": "0"},
            {"SUBSCRIBED_APP_USER": "admin"}, {"SUBSCRIBED_SCHEMA": "public"},
            {"SUBSCRIBED_MIGRATOR_PASSWORD": self.values["SUBSCRIBED_APP_PASSWORD"]},
        ):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                setup.setup(self.values | changes)
        run.assert_not_called()

    def runner(self, existing=False, unsafe=False, wrong_owner=False, wrong_schema=False):
        def execute(command, environment, secrets, input_text=None):
            if command[0] != "psql":
                return ""
            if "FROM pg_database" in input_text:
                return "unrelated_owner" if wrong_owner else ("subscribed_migrator" if existing else "")
            if "FROM pg_roles WHERE rolname" in input_text and "rolsuper" in input_text:
                return "1" if unsafe else ""
            if "FROM pg_namespace" in input_text:
                return "unrelated_owner" if wrong_schema else "subscribed_migrator"
            return ""
        return execute

    def test_fresh_setup_migrates_verifies_and_saves_only_runtime_credentials(self):
        with patch.object(setup, "run", side_effect=self.runner()) as run:
            setup.setup(self.values)
        calls = run.call_args_list
        sql = "\n".join(call.args[3] for call in calls if call.args[0][0] == "psql")
        self.assertIn("CREATE DATABASE subscribed OWNER subscribed_migrator", sql)
        self.assertIn("PASSWORD 'app''s;secret'", sql)
        self.assertNotIn("DROP ", sql)
        migrate = next(call for call in calls if call.args[0][-2:] == ["db", "migrate"])
        self.assertIn("ConnectionStrings__SubscribedMigrator", migrate.args[1])
        self.assertTrue(any(call.args[0][-2:] == ["household", "show"] for call in calls))
        saved = json.loads(calls[-1].args[3])
        self.assertEqual(saved["Database:AppRole"], "subscribed_app")
        self.assertEqual(saved["App:PublicOrigin"], "http://localhost:5080")
        self.assertIn("subscribed_app", saved["ConnectionStrings:Subscribed"])
        self.assertNotIn("ConnectionStrings:SubscribedMigrator", saved)

    def test_rerun_does_not_recreate_database(self):
        with patch.object(setup, "run", side_effect=self.runner(existing=True)) as run:
            setup.setup(self.values)
        self.assertFalse(any("CREATE DATABASE" in (call.args[3] or "") for call in run.call_args_list if len(call.args) > 3))

    def test_refuses_unrelated_or_elevated_existing_objects_before_mutation(self):
        for options in ({"wrong_owner": True}, {"unsafe": True}, {"existing": True, "wrong_schema": True}):
            with self.subTest(options=options):
                with patch.object(setup, "run", side_effect=self.runner(**options)) as run:
                    with self.assertRaises(ValueError):
                        setup.setup(self.values)
                self.assertFalse(any("ALTER ROLE" in (call.args[3] or "") for call in run.call_args_list if len(call.args) > 3))

    def test_command_failures_redact_secrets_and_missing_tools_are_explicit(self):
        result = subprocess.CompletedProcess(["psql"], 1, "", "failure secret")
        with patch.object(setup.subprocess, "run", return_value=result):
            with self.assertRaises(RuntimeError) as error:
                setup.run(["psql"], {}, ["secret"])
            self.assertNotIn("secret", str(error.exception))
            self.assertIn("[redacted]", str(error.exception))
        with patch.object(setup.subprocess, "run", side_effect=FileNotFoundError):
            with self.assertRaisesRegex(RuntimeError, "Required tool not found: psql"):
                setup.run(["psql"], {}, [])

    def test_command_failures_redact_sql_and_json_escaped_passwords(self):
        password = "a'b\"c"
        for encoded in (password.replace("'", "''"), json.dumps(password)[1:-1]):
            result = subprocess.CompletedProcess(["psql"], 1, "", encoded)
            with self.subTest(encoded=encoded), patch.object(setup.subprocess, "run", return_value=result):
                with self.assertRaises(RuntimeError) as error:
                    setup.run(["psql"], {}, [password])
                self.assertNotIn(encoded, str(error.exception))
                self.assertIn("[redacted]", str(error.exception))


if __name__ == "__main__":
    unittest.main()
