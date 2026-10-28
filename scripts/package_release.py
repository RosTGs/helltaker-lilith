"""Build a redistributable mod ZIP from an explicit list of files."""
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.2.0'


def main():
    files = {}
    for name in ['HelltakerAILab.dll', 'Newtonsoft.Json.dll']:
        source = ROOT/'src/bin/Release/net472'/name
        assert source.is_file(), f'Build Release first: {source}'
        files['BepInEx/plugins/HelltakerAILab/'+name] = source.read_bytes()
    for pack in ['lilith', 'lilith-breathing', 'ru-lilith']:
        folder = ROOT/'packs'/pack
        target = 'localizations' if pack == 'ru-lilith' else 'packs'
        manifest = json.loads((folder/'pack.json').read_text(encoding='utf-8'))
        expected = {'pack.json'} | ({t['file'] for t in manifest['textures']} if target == 'packs'
                                   else {t['path'] for t in manifest['files']})
        for relative in sorted(expected):
            source = (folder/relative).resolve()
            assert source.is_relative_to(folder.resolve())
            files[f'HelltakerAILab/{target}/{pack}/{relative}'] = source.read_bytes()
    for name, pack in [('visuals', 'lilith'), ('localization', 'ru-lilith')]:
        files[f'HelltakerAILab/{name}.json'] = json.dumps({'active_pack':pack}).encode()
    for source in (ROOT/'distribution').glob('*.cmd'):
        files[source.name] = source.read_text(encoding='utf-8').replace('\r\n','\n').replace('\n','\r\n').encode('ascii')
    files['INSTALLATION.md'] = (ROOT/'docs/INSTALLATION.md').read_bytes()
    files['licenses/Newtonsoft.Json.LICENSE.md'] = (ROOT/'third-party/Newtonsoft.Json.LICENSE.md').read_bytes()
    hashes = {key:hashlib.sha256(data).hexdigest() for key,data in files.items()}
    files['mod-files.sha256.json'] = json.dumps(hashes, indent=2).encode()
    destination = ROOT/'dist'; destination.mkdir(exist_ok=True)
    archive = destination/f'Helltaker-Lilith-v{VERSION}.zip'
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for key,data in sorted(files.items()): z.writestr(key,data)
    checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix('.zip.sha256').write_text(f'{checksum}  {archive.name}\n')
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        assert all(hashlib.sha256(z.read(key)).hexdigest() == value for key,value in hashes.items())
    print(json.dumps({'archive':str(archive),'files':len(files),'bytes':archive.stat().st_size,'sha256':checksum}))


if __name__ == '__main__': main()
