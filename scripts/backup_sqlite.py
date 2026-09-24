"""Consistent database backup using SQLite's backup API. Never overwrites a destination."""
import argparse
import sqlite3
from contextlib import closing
from pathlib import Path

def backup(source, target):
    source, target = Path(source).resolve(), Path(target).resolve()
    if not source.is_file() or source == target or target.exists():
        raise ValueError('Source must exist and destination must be a new file.')
    # URI mode=ro prevents accidentally creating a blank source database.
    with closing(sqlite3.connect(source.as_uri() + '?mode=ro', uri=True)) as src:
        with closing(sqlite3.connect(str(target))) as dst:
            src.backup(dst)
            if dst.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                raise RuntimeError('Backup integrity check failed')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source'); parser.add_argument('destination')
    args = parser.parse_args()
    backup(args.source, args.destination)
    print('Backup verified. Evidence images require a separate protected backup.')
