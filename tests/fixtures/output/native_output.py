"""Native Unix descriptor controls. Only synthetic captures are written, privately, by run()."""
import errno
import json
import os
from pathlib import Path
import pty
import select
import resource
import signal
import subprocess
import sys
import tempfile
import tty


def closed_pipe():
    reader, writer = os.pipe()
    os.close(reader)  # No reader exists before child creation; subprocess closes unrelated handles.
    try:
        os.write(writer, b'READER-CONTROL')
    except BrokenPipeError:
        return writer
    os.close(writer)
    raise AssertionError('Unexpected pipe reader survived')


def run(binary, command, destination, directory, source):
    owned = []
    stdout = subprocess.PIPE
    stderr = subprocess.PIPE
    if destination in ('broken', 'both'):
        stdout = closed_pipe()
        owned.append(stdout)
    if destination in ('stderr-broken', 'both'):
        stderr = closed_pipe()
        owned.append(stderr)
    if destination == 'full':
        stdout = os.open('/dev/full', os.O_WRONLY)
        owned.append(stdout)
    try:
        result = subprocess.run(['dotnet', binary, command], input=source,
                                stdout=stdout, stderr=stderr, timeout=45)
    finally:
        for descriptor in owned:
            os.close(descriptor)
    directory = Path(directory)
    for name, content in [('stdout', result.stdout or b''), ('stderr', result.stderr or b'')]:
        descriptor = os.open(directory / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(content)
    print(json.dumps({'exitCode': result.returncode}))


def controls(binary):
    payload = 'Rīga مطالبة 😀\n'.encode()
    seed = b'SYNTHETIC-PREFIX\n'
    rows = 0
    for method in ['selected', 'twice', 'lifetime', 'both']:
        for mode in ['pipe', 'broken', 'pty', 'truncate', 'append', 'positioned', 'shared', 'shared-append']:
            output = b''
            copies = 2 if method in ('twice', 'lifetime') else 1
            if mode == 'pipe':
                result = subprocess.run(['dotnet', binary, method], capture_output=True, timeout=5)
                output = result.stdout
            elif mode == 'broken':
                writer = closed_pipe()
                try:
                    result = subprocess.run(['dotnet', binary, method], stdout=writer, stderr=subprocess.PIPE, timeout=5)
                finally:
                    os.close(writer)
            elif mode == 'pty':
                master, slave = pty.openpty()
                tty.setraw(slave)
                try:
                    result = subprocess.run(['dotnet', binary, method], stdout=slave, stderr=subprocess.PIPE, timeout=5)
                    # Keep the last slave open while draining, independently of child lifetime.
                    while select.select([master], [], [], 0.1)[0]:
                        output += os.read(master, 4096)
                finally:
                    os.close(slave)
                    os.close(master)
            else:
                with tempfile.NamedTemporaryFile() as file:
                    file.write(seed)
                    file.flush()
                    flags = os.O_WRONLY | (os.O_APPEND if 'append' in mode else os.O_TRUNC if mode in ('truncate', 'shared') else 0)
                    descriptor = os.open(file.name, flags)
                    if mode == 'positioned':
                        os.lseek(descriptor, len(seed), os.SEEK_SET)
                    if 'append' in mode:
                        assert os.lseek(descriptor, 0, os.SEEK_CUR) == 0
                    try:
                        result = subprocess.run(['dotnet', binary, method], stdout=descriptor,
                                                stderr=descriptor if mode.startswith('shared') else subprocess.PIPE, timeout=5)
                    finally:
                        os.close(descriptor)
                    output = Path(file.name).read_bytes()
            if mode == 'broken':
                assert result.returncode == 70, 'Selected sink must surface native EPIPE'
            else:
                prefix = seed if mode in ('append', 'positioned', 'shared-append') else b''
                suffix = (b'STREAM_SECOND_WRITER\n' if method == 'both' else b'STREAM_WRITE_RETURNED\n') if mode.startswith('shared') else b''
                assert result.returncode == 0 and output == prefix + payload * copies + suffix, 'Native output bytes/offsets differ'
            rows += 1
    def no_file_bytes():
        signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
        resource.setrlimit(resource.RLIMIT_FSIZE, (0, 0))
    with tempfile.TemporaryFile() as file:
        result = subprocess.run(['dotnet', binary, 'selected'], stdout=file, stderr=subprocess.PIPE,
                                preexec_fn=no_file_bytes, timeout=5)
    assert result.returncode in (70, 71), 'Native regular-file failure must remain observable'
    rows += 1
    # Rejecting control proves why the native Console pipe alone is insufficient.
    writer = closed_pipe()
    try:
        result = subprocess.run(['dotnet', binary, 'console'], stdout=writer, stderr=subprocess.PIPE, timeout=5)
    finally:
        os.close(writer)
    assert result.returncode == 0, 'Pinned Console EPIPE control changed; re-challenge sink selection'
    if sys.platform.startswith('linux'):
        with open('/dev/full', 'wb', buffering=0) as full:
            result = subprocess.run(['dotnet', binary, 'selected'], stdout=full, stderr=subprocess.PIPE, timeout=5)
        assert result.returncode == 70, 'Linux ENOSPC must fail'
        rows += 1
    print(json.dumps({'passed': rows, 'consoleRejectingControl': True}))


if __name__ == '__main__':
    if sys.argv[1] == 'controls':
        controls(sys.argv[2])
    else:
        run(*sys.argv[2:], sys.stdin.buffer.read())
