import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from backup_sqlite import backup

class BackupTests(unittest.TestCase):
    def test_live_wal_backup_restores_records(self):
        with tempfile.TemporaryDirectory(prefix='parking-backup-test-') as folder:
            source, target = Path(folder)/'source.db', Path(folder)/'backup.db'
            with closing(sqlite3.connect(source)) as db:
                db.execute('PRAGMA journal_mode=WAL')
                db.execute('CREATE TABLE sample (name TEXT)')
                db.execute("INSERT INTO sample VALUES ('test record')"); db.commit()
                backup(source, target)
                with closing(sqlite3.connect(target)) as restored:
                    self.assertEqual(restored.execute('SELECT name FROM sample').fetchone()[0], 'test record')
                with self.assertRaises(ValueError): backup(source, target)
            with self.assertRaises(ValueError): backup(Path(folder)/'missing.db', Path(folder)/'other.db')

if __name__ == '__main__': unittest.main(verbosity=2)
