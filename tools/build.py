import json
import shutil
import zipfile

from environment import ROOT, ARTIFACTS, dotnet_environment, run_logged, windows_path


def main():
    dotnet, env = dotnet_environment()
    command = [dotnet, 'build', windows_path(ROOT / 'src/DynamicCampaignJapanese.csproj'),
               '-c', 'Release', '--disable-build-servers', '--nologo']
    run_logged(command, env, 'build.log')
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
