"""Small, non-evaluating .env reader. Environment values take precedence."""
from dataclasses import dataclass, field
import os
from pathlib import Path

MODEL = 'jev-1.14.0'
SCHEMA = 'sous-chef.decision.v1'


@dataclass(frozen=True)
class Settings:
    api_key: str = field(default='', repr=False)
    request_seconds: float = 15
    compile_seconds: float = 90
    max_requests: int = 48
    retries: int = 2
    clarification_seconds: float = 90
    preview_seconds: float = 60
    choice_probability: float = .90
    critical_probability: float = .97
    coverage_probability: float = .90


def load_settings(path=None, environ=None):
    path = Path(path) if path else Path(__file__).resolve().parents[2] / '.env'
    values = {}
    if path.exists():
        for line in path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            key, separator, value = line.removeprefix('export ').partition('=')
            if separator and key.strip() in ('JEV_API_KEY', 'TYPESAFE_API_KEY'):
                value = value.strip()
                if value[:1] in ('"', "'"):
                    if len(value) < 2 or value[-1] != value[0]:
                        raise ValueError('Unclosed API key quote in .env')
                    value = value[1:-1]
                else:
                    value = value.split(' #', 1)[0].rstrip()
                values[key.strip()] = value
    environment = os.environ if environ is None else environ
    values.update({k: environment[k] for k in ('JEV_API_KEY', 'TYPESAFE_API_KEY') if k in environment})
    key = values.get('JEV_API_KEY', values.get('TYPESAFE_API_KEY', '')).strip()
    if any(ord(c) < 33 or ord(c) > 126 for c in key):
        raise ValueError('API key must be a printable token without whitespace')
    return Settings(api_key=key)
