"""Apply a generated public-catalog patch without replacing private server state."""
import argparse
import hashlib
import os
from pathlib import Path
import sqlite3


def apply(database: Path, patch: Path, backup: Path) -> None:
    database, patch, backup = database.resolve(), patch.resolve(), backup.resolve()
    if not database.is_file() or not patch.is_file():
        raise ValueError("Missing database or patch")
    if backup.exists() or backup == database or backup == patch:
        raise ValueError("Choose a new backup filename, different from the input files")
    patch_bytes = patch.read_bytes()
    sql = patch_bytes.decode("utf-8")
    if not sql.startswith("-- Public-catalog repair only."):
        raise ValueError("Not a generated catalog repair patch")
    with sqlite3.connect(database, timeout=30) as connection:
        connection.execute("PRAGMA foreign_keys=ON")
        if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError("Database integrity check failed; no patch applied")
        backup.parent.mkdir(parents=True, exist_ok=True)
        descriptor = os.open(backup, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(descriptor)
        with sqlite3.connect(backup) as saved:
            connection.backup(saved)
            if saved.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
                raise ValueError("Backup integrity check failed")
        try:
            connection.executescript(sql)
        except Exception:
            connection.rollback()
            raise
        if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)] or connection.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("Post-check failed; keep services stopped and restore the verified backup")
    print("Catalog repair completed and verified. Private documents were not replaced.")
    print("Private backup:", backup)
    print("Patch SHA-256:", hashlib.sha256(patch_bytes).hexdigest())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--patch", type=Path, required=True)
    parser.add_argument("--backup", type=Path, required=True)
    parser.add_argument("--apply", action="store_true", help="Required: deploy the identity fix and stop server/crawler first")
    arguments = parser.parse_args()
    if not arguments.apply:
        parser.error("No changes made. --apply is required; deploy the fix and stop server/crawler first.")
    apply(arguments.database, arguments.patch, arguments.backup)
