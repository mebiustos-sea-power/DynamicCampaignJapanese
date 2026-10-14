from pathlib import Path
import os
import subprocess
import json

from build import ROOT, ARTIFACTS, windows_path


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
    commands = [
        ('verifier-build.log', [dotnet, 'build', windows_path(ROOT / 'tools/Verifier/Verifier.csproj'),
                                '-c', 'Release', '--disable-build-servers', '--nologo']),
        ('verifier.log', [dotnet, windows_path(ROOT / 'tools/Verifier/bin/Release/net10.0/DynamicCampaignJapanese.Verifier.dll'),
                          windows_path(ROOT)]),
    ]
    for name, command in commands:
        process = subprocess.run(command, cwd=ROOT, capture_output=True, env=env, timeout=120)
        log = process.stdout.decode('utf-8-sig', errors='replace') + process.stderr.decode('utf-8-sig', errors='replace')
        (ARTIFACTS / name).write_text(log, encoding='utf-8')
        if process.returncode:
            raise RuntimeError(log)
    report = json.loads((ARTIFACTS / 'verification-report.json').read_text(encoding='utf-8-sig'))
    if not report['passed']:
        raise RuntimeError(report['errors'])
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__':
    main()
