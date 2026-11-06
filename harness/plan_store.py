"""Atomic, local queue journal. One harness owns a journal at a time."""
import fcntl
import json
from pathlib import Path
import sqlite3


class PlanStore:
    def __init__(self, path=':memory:'):
        self.lock_file = None
        if str(path) != ':memory:':
            path = Path(path).expanduser().resolve()
            path.parent.mkdir(parents=True, exist_ok=True)
            self.lock_file = open(str(path) + '.lock', 'a')
            try:
                fcntl.flock(self.lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                self.lock_file.close()
                raise RuntimeError('Another harness owns this queue journal') from None
        self.db = sqlite3.connect(str(path), check_same_thread=False)
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.execute('CREATE TABLE IF NOT EXISTS queue (id INTEGER PRIMARY KEY CHECK(id=1), body TEXT NOT NULL)')
        self.db.execute('CREATE TABLE IF NOT EXISTS operations (sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL, payload TEXT NOT NULL, result TEXT NOT NULL)')
        self.db.commit()

    def load(self):
        row = self.db.execute('SELECT body FROM queue WHERE id=1').fetchone()
        return json.loads(row[0]) if row else None

    def operation(self, ident, payload):
        row = self.db.execute('SELECT payload, result FROM operations WHERE id=?', (ident,)).fetchone()
        if row is None:
            return None
        if row[0] != payload:
            raise ValueError('Operation ID was already used with different arguments')
        return json.loads(row[1])

    def save(self, state, operation=None):
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO queue VALUES (1, ?)', (json.dumps(state, sort_keys=True),))
            if operation:
                ident, payload, result = operation
                self.db.execute('INSERT INTO operations (id, payload, result) VALUES (?, ?, ?)',
                                (ident, payload, json.dumps(result)))
                self.db.execute('DELETE FROM operations WHERE sequence <= (SELECT MAX(sequence)-512 FROM operations)')

    def close(self):
        if self.db is not None:
            self.db.close()
            self.db = None
        if self.lock_file:
            self.lock_file.close()
