"""Regression coverage for user configuration preservation during port migration."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location(
    "network_migration", Path(__file__).with_name("migrate-v12-network.py")
)
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class NetworkMigrationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name) / "config" / "network.xml"

    def write_config(self, internal="18096", public="18096"):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            '<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
            "<!-- user settings -->"
            "<InternalHttpPort>{}</InternalHttpPort>"
            "<PublicHttpPort>{}</PublicHttpPort>"
            "<EnableRemoteAccess>true</EnableRemoteAccess>"
            "<EnableHttps>true</EnableHttps>"
            "<CertificatePath>/custom/server.pfx</CertificatePath>"
            "<KnownProxies><string>10.0.0.2</string></KnownProxies>"
            "</NetworkConfiguration>".format(internal, public), encoding="utf-8"
        )
        return self.path.read_bytes()

    def test_fresh_install_and_custom_build_port(self):
        for port in (8096, 8097, 1, 65535):
            with self.subTest(port=port):
                path = self.path.parent / str(port) / "network.xml"
                migration.migrate(path, port)
                root = ET.parse(path).getroot()
                self.assertEqual(root.findtext("InternalHttpPort"), str(port))
                self.assertEqual(root.findtext("PublicHttpPort"), str(port))
                self.assertEqual(root.findtext("EnableRemoteAccess"), "false")

    def test_old_ports_migrate_with_backup_and_other_settings_preserved(self):
        original = self.write_config()
        self.path.chmod(0o640)
        migration.migrate(self.path, 8096)
        root = ET.parse(self.path).getroot()
        self.assertEqual(root.findtext("InternalHttpPort"), "8096")
        self.assertEqual(root.findtext("PublicHttpPort"), "8096")
        self.assertEqual(root.findtext("EnableRemoteAccess"), "true")
        self.assertEqual(root.findtext("EnableHttps"), "true")
        self.assertEqual(root.findtext("CertificatePath"), "/custom/server.pfx")
        self.assertEqual(root.findtext("KnownProxies/string"), "10.0.0.2")
        self.assertIn(b"<!-- user settings -->", self.path.read_bytes())
        backups = list(self.path.parent.glob("*.bak"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), original)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o640)

    def test_existing_custom_ports_remain_byte_identical(self):
        original = self.write_config("9000", "443")
        migration.migrate(self.path, 8097)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertFalse(list(self.path.parent.glob("*.bak")))

    def test_custom_public_port_preserved_when_internal_port_migrates(self):
        self.write_config("18096", "443")
        migration.migrate(self.path, 8097)
        root = ET.parse(self.path).getroot()
        self.assertEqual(root.findtext("InternalHttpPort"), "8097")
        self.assertEqual(root.findtext("PublicHttpPort"), "443")

    def test_second_launch_does_not_rewrite_configuration(self):
        self.write_config()
        migration.migrate(self.path, 8096)
        original = self.path.read_bytes()
        modified = self.path.stat().st_mtime_ns
        migration.migrate(self.path, 8097)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.path.stat().st_mtime_ns, modified)
        self.assertEqual(len(list(self.path.parent.glob("*.bak"))), 1)

    def test_user_can_select_old_port_after_migration(self):
        migration.migrate(self.path, 8096)
        original = self.write_config()
        migration.migrate(self.path, 8096)
        self.assertEqual(self.path.read_bytes(), original)

    def test_invalid_xml_and_wrong_root_fail_without_replacing_config(self):
        for contents in (b"<broken", b"<WrongRoot />"):
            with self.subTest(contents=contents):
                self.path.parent.mkdir(parents=True, exist_ok=True)
                self.path.write_bytes(contents)
                with self.assertRaises((ET.ParseError, ValueError)):
                    migration.migrate(self.path, 8096)
                self.assertEqual(self.path.read_bytes(), contents)
                self.assertFalse(self.path.with_name(".v12-http-port-migrated").exists())

    def test_atomic_replace_failure_preserves_original_and_backup(self):
        original = self.write_config()
        with patch.object(migration.os, "replace", side_effect=OSError("disk error")):
            with self.assertRaises(OSError):
                migration.migrate(self.path, 8096)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(next(self.path.parent.glob("*.bak")).read_bytes(), original)
        self.assertFalse(self.path.with_name(".v12-http-port-migrated").exists())

    def test_invalid_ports_do_not_create_config(self):
        for port in (0, -1, 65536):
            with self.subTest(port=port):
                with self.assertRaises(ValueError):
                    migration.migrate(self.path, port)
                self.assertFalse(self.path.exists())


if __name__ == "__main__":
    unittest.main()
