from pathlib import Path
import json
import os
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts'


def windows_path(path):
    text = str(path)
    if os.name == 'nt':
        return text
    if text.startswith('/mnt/') and len(text) > 7 and text[6] == '/':
        return text[5].upper() + ':\\' + text[7:].replace('/', '\\')
    raise ValueError('A Windows-mounted path is required: ' + text)


def main():
    ARTIFACTS.mkdir(exist_ok=True)
    dotnet = 'C:/Program Files/dotnet/dotnet.exe' if os.name == 'nt' else '/mnt/c/Program Files/dotnet/dotnet.exe'
    env = os.environ.copy()
    settings = {
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_CLI_HOME': windows_path(ARTIFACTS / 'dotnet-home'),
        'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true',
    }
    env.update(settings)
    if os.name != 'nt':
        env['WSLENV'] = ':'.join(filter(None, [env.get('WSLENV', ''), *settings]))
    command = [dotnet, 'build', windows_path(ROOT / 'src/DynamicCampaignJapanese.csproj'),
               '-c', 'Release', '--disable-build-servers', '--nologo']
    process = subprocess.run(command, cwd=ROOT, capture_output=True, env=env, timeout=120)
    log = process.stdout.decode('utf-8-sig', errors='replace') + process.stderr.decode('utf-8-sig', errors='replace')
    (ARTIFACTS / 'build.log').write_text(log, encoding='utf-8')
    if process.returncode:
        raise RuntimeError(log)
    binary = ROOT / 'src/bin/Release/netstandard2.1/DynamicCampaignJapanese.dll'
    if not binary.is_file():
        raise RuntimeError('Build reported success without producing the DLL.')
    package = ARTIFACTS / 'package/DynamicCampaignJapanese'
    if package.exists():
        shutil.rmtree(package)
    package.mkdir(parents=True, exist_ok=True)
    shutil.copy2(binary, package / binary.name)
    shutil.copy2(ROOT / 'localization/translation-data.json', package / 'translation-data.json')
    for path in (ROOT / 'package').iterdir():
        if path.is_file():
            shutil.copy2(path, package / path.name)
    archive = ARTIFACTS / 'DynamicCampaignJapanese.zip'
    with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED) as target:
        for path in sorted(package.iterdir()):
            target.write(path, 'DynamicCampaignJapanese/' + path.name)
    print(json.dumps({'build': 'passed', 'dll': str(binary), 'package': str(package), 'zip': str(archive)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
