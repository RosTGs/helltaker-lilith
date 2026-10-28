"""Build the Russian Lilith pack while preserving every dialogue line index."""
import hashlib
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True, help='Owned Helltaker installation or test copy')
    game = parser.parse_args().game
    source = ROOT/'localization/ru-lilith/translations.json'
    translated = json.loads(source.read_text(encoding='utf-8'))
    originals = {f'{folder}/{f.name}': f for folder in ['local', 'localHM']
                 for f in (game/folder).glob('*.json')}
    assert set(translated) == set(originals)
    pack = ROOT/'packs/ru-lilith'
    files = []
    for key, lines in translated.items():
        data = originals[key].read_bytes()
        original = data.decode('utf-16' if data.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig').splitlines()
        assert len(lines) == len(original), key
        assert all(isinstance(s, str) and '\n' not in s and '\r' not in s for s in lines), key
        assert [i for i,s in enumerate(lines) if not s] == [i for i,s in enumerate(original) if not s], key
        path = pack/key
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('\n'.join(lines)+'\n', encoding='utf-8')
        files.append(dict(path=key, lines=len(lines), source_sha256=hashlib.sha256(data).hexdigest()))
    manifest = dict(name='Лилит — русская история', version='1.0', language='ru', protagonist='Лилит',
                    font='Georgia', files=files)
    (pack/'pack.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    report = dict(files=len(files), lines=sum(len(s) for s in translated.values()),
                  line_indices_preserved=True, blank_lines_preserved=True, main_and_examtaker=True)
    (ROOT/'reports').mkdir(exist_ok=True)
    (ROOT/'reports/localization-coverage.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report))


if __name__ == '__main__': main()
