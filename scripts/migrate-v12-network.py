"""Initialize V12 networking or migrate the old packaged HTTP port once."""

import argparse
import os
from pathlib import Path
import shutil
import tempfile
import xml.etree.ElementTree as ET


def atomic_write(path, contents, preserve_mode=False):
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        if preserve_mode:
            shutil.copymode(path, temporary)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def migrate(path, default_port):
    if not 1 <= default_port <= 65535:
        raise ValueError("HTTP port must be between 1 and 65535")
    path = Path(path)
    marker = path.with_name(".v12-http-port-migrated")
    exists = path.exists()
    if exists:
        # Invalid XML must fail without replacing the user's configuration.
        parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
        tree = ET.parse(path, parser=parser)
        root = tree.getroot()
        if root.tag != "NetworkConfiguration":
            raise ValueError("Expected a NetworkConfiguration XML root")
    else:
        root = ET.fromstring(
            "<NetworkConfiguration>"
            "<InternalHttpPort>{0}</InternalHttpPort>"
            "<PublicHttpPort>{0}</PublicHttpPort>"
            "<AutoDiscovery>false</AutoDiscovery>"
            "<EnableRemoteAccess>false</EnableRemoteAccess>"
            "<LocalNetworkAddresses><string>127.0.0.1</string>"
            "</LocalNetworkAddresses></NetworkConfiguration>".format(default_port)
        )

    changed = not exists
    if exists and not marker.exists():
        for name in ("InternalHttpPort", "PublicHttpPort"):
            field = root.find(name)
            if field is not None and (field.text or "").strip() == "18096":
                field.text = str(default_port)
                changed = True

    if changed:
        path.parent.mkdir(parents=True, exist_ok=True)
        if exists:
            # Unique backups retain the exact original bytes and permissions.
            descriptor, backup = tempfile.mkstemp(
                prefix="network.xml.pre-port-migration-", suffix=".bak", dir=path.parent
            )
            os.close(descriptor)
            shutil.copy2(path, backup)
            print("Network configuration backup: " + backup)
        atomic_write(path, ET.tostring(root, encoding="utf-8", xml_declaration=True), exists)

    if not marker.exists():
        atomic_write(marker, b"HTTP default port migration completed\n")
    port = root.findtext("InternalHttpPort", default="8096")
    print("Configured HTTP port: " + port)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("default_port", type=int)
    args = parser.parse_args()
    migrate(args.config, args.default_port)
