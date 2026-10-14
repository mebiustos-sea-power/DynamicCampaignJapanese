from pathlib import Path
import json
import os
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts'


def windows_path(path):
    text = str(path)
    if os.name == 'nt':
        return text
    if text.startswith('/mnt/') and len(text) > 7 and text[6] == '/':
        return text[5].upper() + ':\\' + text[7:].replace('/', '\\')
    raise ValueError('A Windows-mounted path is required: ' + text)


def dotnet_environment():
    configured = os.environ.get('DOTNET_EXE')
    dotnet = shutil.which(configured) if configured else shutil.which('dotnet.exe')
    if not dotnet:
        raise FileNotFoundError('Windows .NET SDK was not found. Add dotnet.exe to PATH or set DOTNET_EXE to its executable path.')
    ARTIFACTS.mkdir(exist_ok=True)
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
    return dotnet, env


def run_logged(command, env, log_name):
    process = subprocess.run(command, cwd=ROOT, capture_output=True, env=env, timeout=120)
    log = process.stdout.decode('utf-8-sig', errors='replace') + process.stderr.decode('utf-8-sig', errors='replace')
    (ARTIFACTS / log_name).write_text(log, encoding='utf-8')
    if process.returncode:
        raise RuntimeError(log)
    return process.stdout.decode('utf-8-sig')


def reference_paths(dotnet, env):
    output = run_logged([dotnet, 'msbuild', windows_path(ROOT / 'tools/Verifier/Verifier.csproj'),
                         '-nologo', '-getProperty:SeaPowerDir,AnchorChainDir,OriginalModDir'],
                        env, 'reference-paths.log')
    properties = json.loads(output)['Properties']
    for name in ('SeaPowerDir', 'AnchorChainDir', 'OriginalModDir'):
        if not properties.get(name):
            raise ValueError('Set ' + name + ' in Directory.Build.local.props. See README.md for setup.')
    return properties
