from pathlib import Path
import argparse
import json

from environment import ROOT, ARTIFACTS, dotnet_environment, reference_paths, run_logged, windows_path


def main():
    parser = argparse.ArgumentParser(description='Compare localization behavior with a separately retained migration baseline.')
    parser.add_argument('--baseline', type=Path, default=ARTIFACTS / 'baseline',
                        help='Baseline directory (default: artifacts/baseline). See docs/verification.md.')
    arguments = parser.parse_args()
    baseline = arguments.baseline.resolve()
    for name in ('runtime-output.json', 'installed-package/DynamicCampaignJapanese.dll', 'installed-package/rules.json',
                 'installed-package/texts.json', 'installed-package/campaign-texts.json'):
        if not (baseline / name).is_file():
            parser.error('Comparison data is missing: ' + str(baseline / name) + '. See docs/verification.md.')
    if not (baseline / 'saves').is_dir():
        parser.error('Baseline saves directory is missing: ' + str(baseline / 'saves') + '. See docs/verification.md.')
    dotnet, env = dotnet_environment()
    references = reference_paths(dotnet, env)
    commands = [
        ('verifier-build.log', [dotnet, 'build', windows_path(ROOT / 'tools/Verifier/Verifier.csproj'),
                                '-c', 'Release', '--disable-build-servers', '--nologo']),
        ('verifier.log', [dotnet, windows_path(ROOT / 'tools/Verifier/bin/Release/net10.0/DynamicCampaignJapanese.Verifier.dll'),
                          windows_path(ROOT), references['SeaPowerDir'], references['AnchorChainDir'],
                          references['OriginalModDir'], windows_path(baseline)]),
    ]
    for name, command in commands:
        run_logged(command, env, name)
    report = json.loads((ARTIFACTS / 'verification-report.json').read_text(encoding='utf-8-sig'))
    if not report['passed']:
        raise RuntimeError(report['errors'])
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__':
    main()
