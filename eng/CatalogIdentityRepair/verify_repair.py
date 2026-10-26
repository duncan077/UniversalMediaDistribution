"""Compare a repaired server copy without printing any private payloads."""
import argparse
import json
from pathlib import Path
import sqlite3


def verify(original: Path, repaired: Path) -> dict:
    connection = sqlite3.connect(repaired.resolve().as_uri() + "?mode=ro", uri=True)
    connection.execute("ATTACH DATABASE ? AS original", (original.resolve().as_uri() + "?mode=ro",))
    if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)] or connection.execute("PRAGMA foreign_key_check").fetchall():
        raise ValueError("Invalid repaired database")
    repair_id = connection.execute("SELECT item_id FROM catalog_identity_repair_archive").fetchone()[0]
    names = [row[0] for row in connection.execute("SELECT name FROM original.sqlite_master WHERE type='table'")]
    changed_tables = {"catalog_items", "catalog_names", "catalog_state", "catalog_redirects", "crawler_titles"}
    for name in names:
        if name in changed_tables:
            continue
        identifier = '"' + name.replace('"', '""') + '"'
        for left, right in (("original", "main"), ("main", "original")):
            if connection.execute(f"SELECT COUNT(*) FROM (SELECT * FROM {left}.{identifier} EXCEPT SELECT * FROM {right}.{identifier})").fetchone()[0]:
                raise ValueError("Unrelated table changed: " + name)
    if connection.execute("SELECT COUNT(*) FROM (SELECT * FROM original.catalog_items WHERE id<>? EXCEPT SELECT * FROM main.catalog_items)", (repair_id,)).fetchone()[0]:
        raise ValueError("Unrelated catalog row changed")
    count_before = connection.execute("SELECT COUNT(*) FROM original.catalog_items").fetchone()[0]
    count_after = connection.execute("SELECT COUNT(*) FROM main.catalog_items").fetchone()[0]
    result = {
        "integrity": "ok", "foreignKeys": "ok", "unrelatedTablesUnchanged": True,
        "unrelatedCatalogRowsUnchanged": True, "privateDocumentsUnchanged": True,
        "privateDocumentCount": connection.execute("SELECT COUNT(*) FROM server_documents").fetchone()[0],
        "catalogBefore": count_before, "catalogAfter": count_after,
        "oldAmbiguousRedirectsRemaining": connection.execute("SELECT COUNT(*) FROM catalog_redirects WHERE to_id=?", (repair_id,)).fetchone()[0],
    }
    connection.close()
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original", type=Path)
    parser.add_argument("repaired", type=Path)
    arguments = parser.parse_args()
    print(json.dumps(verify(arguments.original, arguments.repaired), indent=2))
