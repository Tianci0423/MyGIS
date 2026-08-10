import argparse
import json
import os
import sqlite3


def load_catalog(database_path):
    if not os.path.isfile(database_path):
        raise FileNotFoundError(f"PROJ database not found: {database_path}")

    connection = sqlite3.connect(f"file:{database_path}?mode=ro", uri=True)
    try:
        cursor = connection.cursor()
        geographic = cursor.execute(
            """
            SELECT CAST(code AS TEXT), name
            FROM geodetic_crs
            WHERE auth_name = 'EPSG'
              AND type = 'geographic 2D'
              AND deprecated = 0
            """
        ).fetchall()
        projected = cursor.execute(
            """
            SELECT CAST(code AS TEXT), name
            FROM projected_crs
            WHERE auth_name = 'EPSG'
              AND deprecated = 0
            """
        ).fetchall()
    finally:
        connection.close()

    entries = [
        {"Code": code, "Name": name, "Category": "geographic"}
        for code, name in geographic
    ]
    entries.extend(
        {"Code": code, "Name": name, "Category": "projected"}
        for code, name in projected
    )
    entries.sort(key=lambda item: (item["Category"], item["Name"].casefold(), item["Code"]))
    return entries


def main():
    parser = argparse.ArgumentParser(description="List EPSG coordinate reference systems")
    parser.add_argument("--database", required=True)
    args = parser.parse_args()
    print(json.dumps(load_catalog(args.database), ensure_ascii=False))


if __name__ == "__main__":
    main()
