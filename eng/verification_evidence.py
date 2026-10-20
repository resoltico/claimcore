"""Construct only fixed, numeric qualification summaries; raw reports never enter publication."""
import argparse
import datetime
import hashlib
import json
import os
import platform
from pathlib import Path
import re
import subprocess
import uuid
import xml.etree.ElementTree as ET

STAGES = {
    'ClaimCore.Tests': 'unit', 'ClaimCore.ArchitectureTests': 'architecture',
    'ClaimCore.IntegrationTests': 'postgres', 'ClaimCore.WebTests': 'web',
    'ClaimCore.AcceptanceTests': 'acceptance',
}
FIELDS = {'format', 'stage', 'source', 'run', 'job', 'platform', 'started', 'finished',
          'result', 'complete', 'counts', 'dimensions', 'inputs', 'growth', 'attempt'}

def command(*args):
    return subprocess.check_output(args, stderr=subprocess.DEVNULL).decode().strip()

def known_provenance():
    if subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard', '-z'], stderr=subprocess.DEVNULL):
        raise OSError('Untracked inputs have not been admitted to source provenance')
    paths = subprocess.check_output(['git', 'ls-files', '-z'], stderr=subprocess.DEVNULL).split(b'\0')
    digest = hashlib.sha256()
    for raw in sorted(filter(None, paths)):
        path = Path(os.fsdecode(raw))
        if not path.is_file() or path.is_symlink():
            continue
        digest.update(raw + b'\0' + hashlib.sha256(path.read_bytes()).digest())
    return {'revision': command('git', 'rev-parse', 'HEAD'),
            'dirty': bool(command('git', 'status', '--porcelain', '--untracked-files=no')),
            'inputSha256': digest.hexdigest(), 'scope': 'tracked-regular-files'}

def provenance():
    try:
        return known_provenance()
    except (OSError, subprocess.CalledProcessError):
        return {'revision': None, 'dirty': None, 'inputSha256': None, 'scope': 'unknown'}


def atomic(path, value):
    temporary = path.with_suffix('.writing')
    temporary.write_text(json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n')
    temporary.replace(path)

def begin(directory, stage):
    directory.mkdir(parents=True, exist_ok=True)
    baseline = json.loads(Path('db/postgresql-baseline.json').read_text())
    record = {'format': 1, 'stage': stage, 'source': {**provenance(), 'stable': None},
              'run': os.environ.get('GITHUB_RUN_ID', 'local-' + uuid.uuid4().hex),
              'job': os.environ.get('GITHUB_JOB', 'local'), 'platform': os.uname().sysname,
              'attempt': int(os.environ.get('GITHUB_RUN_ATTEMPT', '1')),
              'started': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'finished': None, 'result': 'incomplete', 'complete': False, 'growth': [],
              'counts': {'total': 0, 'passed': 0, 'failed': 0, 'skipped': 0, 'filtered': None, 'retried': None},
              'dimensions': {'postgresMajor': os.environ.get('CLAIMCORE_TEST_POSTGRES_MAJOR'),
                             'browserEngine': os.environ.get('CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE', 'all'),
                             'browserScope': os.environ.get('CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE', 'complete')},
              'inputs': {'architecture': platform.machine(), 'sdk': json.loads(Path('global.json').read_text())['sdk']['version'],
                         'node': Path('.node-version').read_text().strip(),
                         'postgresImages': [item['containerImage'] for item in baseline['supportedMajors']]}}
    atomic(directory / 'summary.json', record)

GROWTH_FIELDS = set("writes cases actors grants writers ledgerEntries seconds writesPerSecond p50Milliseconds p95Milliseconds maximumMilliseconds verificationMilliseconds managedBytesBeforeVerification managedBytesAfterVerification processPeakWorkingSetBytes sampledPeakManagedBytes sampledPeakWorkingSetBytes lockTimeoutMilliseconds unexpectedErrors".split())

GROWTH_COUNTS = GROWTH_FIELDS - set("seconds writesPerSecond p50Milliseconds p95Milliseconds maximumMilliseconds verificationMilliseconds lockTimeoutMilliseconds".split())

def validate_workload(row):
    if set(row) != GROWTH_FIELDS:
        raise ValueError('Unknown growth measurement fields')
    for name, value in row.items():
        if type(value) not in (int, float) or not 0 <= value < float('inf'):
            raise ValueError('Invalid growth measurement')
        if name in GROWTH_COUNTS and type(value) is not int:
            raise ValueError('Growth counts require integers')


def growth_records(root, native_run):
    records = []
    for run_file in sorted((root / 'growth').glob('*/run.json')):
        run = json.loads(run_file.read_text())
        if run.get('nativeRun') != native_run:
            continue
        if set(run) != {'nativeRun', 'result', 'completed', 'expected'} or run['result'] not in {'incomplete', 'completed', 'failed'}:
            raise ValueError('Unknown growth run schema')
        rows = [json.loads(path.read_text()) for path in sorted(run_file.parent.glob('workload-*.json'))]
        for row in rows:
            validate_workload(row)
        if type(run['completed']) is not int or type(run['expected']) is not int or not 0 <= run['completed'] <= run['expected'] or not run['completed'] <= len(rows) <= run['expected']:
            raise ValueError('Growth count mismatch')
        records.append({'result': run['result'], 'completed': len(rows), 'expected': run['expected'], 'workloads': rows})
    return records


def finish(directory, exit_code):
    record = json.loads((directory / 'summary.json').read_text())
    path = directory / 'results.trx'
    if path.exists():
        root = ET.parse(path).getroot()
        counters = next(root.iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters'))
        counts = counters.attrib
        record['counts'].update(total=int(counts['total']), passed=int(counts['passed']),
                                failed=int(counts['failed']), skipped=int(counts.get('notExecuted', 0)))
    record['growth'] = growth_records(directory.parent, directory.name)
    original_source = {key: value for key, value in record['source'].items() if key != 'stable'}
    record['source']['stable'] = None if original_source['scope'] == 'unknown' else original_source == provenance()
    record['finished'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    record['result'] = 'passed' if exit_code == 0 else 'failed'
    # These unfiltered native invocations have no retry configuration. Browser scope stays explicit.
    record['counts'].update(filtered=0, retried=0)
    record['complete'] = exit_code == 0 and record['counts']['total'] > 0 and record['counts']['skipped'] == 0 and record['source']['stable'] is True
    atomic(directory / 'summary.json', record)


def validate(record):
    if set(record) != FIELDS or record['format'] != 1 or record['stage'] not in STAGES.values():
        raise ValueError('Unknown evidence fields or stage')
    for run in record['growth']:
        if set(run) != {'result', 'completed', 'expected', 'workloads'} or run['result'] not in {'incomplete', 'completed', 'failed'}:
            raise ValueError('Invalid retained growth run')
        if type(run['completed']) is not int or type(run['expected']) is not int or not 0 <= run['completed'] <= run['expected'] or len(run['workloads']) != run['completed']:
            raise ValueError('Invalid retained growth counts')
        for row in run['workloads']:
            validate_workload(row)
    source = record['source']
    if set(source) != {'revision', 'dirty', 'inputSha256', 'scope', 'stable'}:
        raise ValueError('Unknown source fields')
    if source['scope'] == 'unknown':
        if any(source[key] is not None for key in ('revision', 'dirty', 'inputSha256', 'stable')):
            raise ValueError('Unknown provenance must remain explicit')
    elif source['scope'] == 'tracked-regular-files':
        if not re.fullmatch('[0-9a-f]{40}', source['revision']) or not re.fullmatch('[0-9a-f]{64}', source['inputSha256']):
            raise ValueError('Invalid source identity')
        if type(source['dirty']) is not bool or source['stable'] is not None and type(source['stable']) is not bool:
            raise ValueError('Invalid source scope')
    else:
        raise ValueError('Unknown provenance scope')
    if set(record['counts']) != {'total', 'passed', 'failed', 'skipped', 'filtered', 'retried'}:
        raise ValueError('Unknown counts')
    if any(value is not None and (type(value) is not int or value < 0) for value in record['counts'].values()):
        raise ValueError('Invalid counts')
    if record['result'] not in {'incomplete', 'passed', 'failed'} or type(record['complete']) is not bool:
        raise ValueError('Invalid completion')
    if type(record['attempt']) is not int or record['attempt'] < 1:
        raise ValueError('Invalid run attempt')
    if not re.fullmatch(r'(local-[0-9a-f]{32}|[0-9]+)', record['run']):
        raise ValueError('Invalid run identity')
    if record['job'] not in {'local', 'unit', 'integration', 'web', 'acceptance'} or record['platform'] not in {'Darwin', 'Linux'}:
        raise ValueError('Unknown job or platform')
    for key in ('started', 'finished'):
        if record[key] is not None:
            datetime.datetime.fromisoformat(record[key])
    if set(record['dimensions']) != {'postgresMajor', 'browserEngine', 'browserScope'}:
        raise ValueError('Unknown dimensions')
    dims = record['dimensions']
    if dims['browserEngine'] not in {'all', 'chromium', 'firefox', 'webkit'} or dims['browserScope'] not in {'complete', 'smoke'}:
        raise ValueError('Invalid browser dimensions')
    if dims['postgresMajor'] is not None and not re.fullmatch('[1-9][0-9]*', dims['postgresMajor']):
        raise ValueError('Invalid database dimension')
    inputs = record['inputs']
    if set(inputs) != {'sdk', 'node', 'postgresImages', 'architecture'} or not all(re.fullmatch('[0-9]+\.[0-9]+\.[0-9]+', inputs[key]) for key in ('sdk', 'node')):
        raise ValueError('Invalid tool inputs')
    if inputs['architecture'] not in {'arm64', 'aarch64', 'x86_64'}:
        raise ValueError('Unknown process architecture')
    if not inputs['postgresImages'] or not all(re.fullmatch('postgres:[0-9]+\.[0-9]+@sha256:[0-9a-f]{64}', image) for image in inputs['postgresImages']):
        raise ValueError('Invalid database images')


def publish(root, output):
    admitted = []
    for path in sorted(root.glob('native.*/summary.json')):
        if path.is_symlink():
            raise ValueError('Evidence must be a regular file')
        record = json.loads(path.read_text())
        validate(record)
        admitted.append(record)
    output.mkdir(parents=True, exist_ok=False)
    for index, record in enumerate(admitted):
        atomic(output / f'summary-{index:03d}.json', record)
    # The upload directory is constructed here, never populated by copying raw directories.
    atomic(output / 'manifest.json', {'format': 1, 'files': sorted(path.name for path in output.glob('summary-*.json'))})

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['begin', 'finish', 'publish'])
    parser.add_argument('directory', type=Path)
    parser.add_argument('argument')
    args = parser.parse_args()
    if args.action == 'begin':
        begin(args.directory, STAGES[Path(args.argument).stem])
    elif args.action == 'finish':
        finish(args.directory, int(args.argument))
    else:
        publish(args.directory, Path(args.argument))
