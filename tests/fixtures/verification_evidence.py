"""Reject unknown publication facts and prove interrupted numeric records survive."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('evidence', root / 'eng/verification_evidence.py')
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)
with tempfile.TemporaryDirectory() as temporary:
    directory = Path(temporary)
    native = directory / 'native.synthetic'
    evidence.begin(native, 'postgres')
    record = json.loads((native / 'summary.json').read_text())
    evidence.validate(record)
    for location in [record, record['source'], record['counts'], record['dimensions'], record['inputs']]:
        location['private-canary'] = 'secret\n/path/token'
        try:
            evidence.validate(record)
        except ValueError:
            pass
        else:
            raise AssertionError('Unknown canary field was admitted')
        del location['private-canary']
    growth = directory / 'growth/owned'
    growth.mkdir(parents=True)
    evidence.atomic(growth / 'run.json', {'nativeRun': native.name, 'result': 'incomplete', 'completed': 0, 'expected': 12})
    row = dict.fromkeys(evidence.GROWTH_FIELDS, 1)
    evidence.atomic(growth / 'workload-00.json', row)
    # Simulate interruption between atomic workload completion and run progress update.
    retained = evidence.growth_records(directory, native.name)
    assert retained[0]['completed'] == 1 and retained[0]['result'] == 'incomplete'
    evidence.finish(native, 19)
    finished = json.loads((native / 'summary.json').read_text())
    assert finished['result'] == 'failed' and not finished['complete']
    assert finished['growth'][0]['workloads'] == [row]
    (directory / 'private-canary.log').write_text('secret')
    (native / 'results.trx').write_text('private-canary')
    output = directory / 'upload'
    evidence.publish(directory, output)
    assert sorted(path.name for path in output.iterdir()) == ['manifest.json', 'summary-000.json']
    assert 'private-canary' not in ''.join(path.read_text() for path in output.iterdir())
    poisoned = copy.deepcopy(finished)
    poisoned['growth'][0]['workloads'][0]['private-canary'] = 1
    evidence.atomic(native / 'summary.json', poisoned)
    try:
        evidence.publish(directory, directory / 'refused')
    except ValueError:
        pass
    else:
        raise AssertionError('Final upload admitted a poisoned measurement')
    assert not (directory / 'refused').exists()

    evidence.atomic(native / 'summary.json', finished)
    preexisting = directory / 'preexisting'
    preexisting.mkdir()
    (preexisting / 'private-canary.json').write_text('secret')
    try:
        evidence.publish(directory, preexisting)
    except (ValueError, FileExistsError):
        pass
    else:
        raise AssertionError('Existing upload content must refuse')
    assert (preexisting / 'private-canary.json').read_text() == 'secret'
    action = (root / '.github/actions/qualification-evidence/action.yml').read_text()
    assert "if: always() && steps.evidence.outcome == 'success'" in action
    assert 'id: evidence' in action
