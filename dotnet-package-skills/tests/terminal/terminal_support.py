"""Small platform adapter for the manual terminal regression driver."""

import codecs
import errno
import hashlib
import os
from pathlib import Path
import struct
import subprocess
import sys
import threading
import time

if sys.platform in ("darwin", "linux"):
    import fcntl
    import pty
    import select
    import signal
    import termios


class OutputDecoder:
    def __init__(self):
        self.decoder = codecs.getincrementaldecoder("utf-8")(errors="strict")

    def decode(self, data, final=False):
        return self.decoder.decode(data, final=final)


def winsize(rows, columns):
    if any(type(value) is not int or not 1 <= value <= 65535 for value in (rows, columns)):
        raise ValueError("Terminal rows and columns must be integers between 1 and 65535.")
    return struct.pack("HHHH", rows, columns, 0, 0)


def spawn_pty(command, cwd, env, dimensions):
    if sys.platform == "win32":
        from winpty import Backend, PtyProcess

        return PtyProcess.spawn(
            command, cwd=str(cwd), env=env, dimensions=dimensions, backend=Backend.ConPTY,
        )
    return PosixPtyProcess.spawn(command, cwd=cwd, env=env, dimensions=dimensions)


class PosixPtyProcess:
    # Exec in a fresh Python process instead of running Python preexec_fn after a
    # multithreaded fork. The apphost then retains this PID, session and terminal.
    CHILD = (
        "import fcntl,os,sys,termios\n"
        "fcntl.ioctl(0, termios.TIOCSCTTY, 0)\n"
        "os.execvpe(sys.argv[1], sys.argv[1:], os.environ)\n"
    )

    def __init__(self, process, master):
        self.process = process
        self.master = master
        self.pid = process.pid
        self.decoder = OutputDecoder()
        self.closed = threading.Event()
        self.fd_lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.eof = False

    @classmethod
    def spawn(cls, command, cwd, env, dimensions):
        if sys.platform not in ("darwin", "linux"):
            raise RuntimeError("POSIX terminal tests require macOS or Linux.")
        if not command:
            raise ValueError("A terminal command is required.")
        size = winsize(*dimensions)
        master, slave = pty.openpty()
        try:
            fcntl.ioctl(slave, termios.TIOCSWINSZ, size)
            attributes = termios.tcgetattr(slave)
            attributes[3] &= ~(termios.ECHO | termios.ICANON)
            attributes[6][termios.VMIN] = 1
            attributes[6][termios.VTIME] = 0
            termios.tcsetattr(slave, termios.TCSANOW, attributes)
            os.set_blocking(master, False)
            process = subprocess.Popen(
                [sys.executable, "-c", cls.CHILD, *command],
                cwd=str(cwd), env=env, stdin=slave, stdout=slave, stderr=slave,
                close_fds=True, start_new_session=True,
            )
        except BaseException:
            os.close(master)
            raise
        finally:
            os.close(slave)
        return cls(process, master)

    @property
    def exitstatus(self):
        return self.process.poll()

    def read(self, size):
        if size <= 0:
            raise ValueError("PTY read size must be positive.")
        while not self.closed.is_set():
            if self.eof:
                raise EOFError
            try:
                readable, _, _ = select.select([self.master], [], [], 0.1)
                if not readable:
                    continue
                with self.fd_lock:
                    if self.closed.is_set():
                        raise EOFError
                    try:
                        data = os.read(self.master, size)
                    except BlockingIOError:
                        continue
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
                        data = b""
            except (OSError, ValueError):
                if self.closed.is_set():
                    raise EOFError from None
                raise
            self.eof = not data
            text = self.decoder.decode(data, final=self.eof)
            if text:
                return text
        raise EOFError

    def write(self, text):
        data = memoryview(text.encode("utf-8"))
        deadline = time.monotonic() + 5
        with self.write_lock:
            while data:
                if self.closed.is_set():
                    raise OSError(errno.EBADF, "The test PTY is closed.")
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Writing to the test PTY timed out.")
                _, writable, _ = select.select([], [self.master], [], min(remaining, 0.1))
                if not writable:
                    continue
                with self.fd_lock:
                    if self.closed.is_set():
                        raise OSError(errno.EBADF, "The test PTY is closed.")
                    try:
                        written = os.write(self.master, data)
                    except BlockingIOError:
                        continue
                data = data[written:]

    def setwinsize(self, rows, columns):
        # The kernel sends SIGWINCH to the slave's foreground process group.
        size = winsize(rows, columns)
        with self.fd_lock:
            if self.closed.is_set():
                raise OSError(errno.EBADF, "The test PTY is closed.")
            fcntl.ioctl(self.master, termios.TIOCSWINSZ, size)

    def signal_group(self, number):
        try:
            os.killpg(self.pid, number)
        except ProcessLookupError:
            pass

    def close(self, force=False):
        if self.closed.is_set():
            return
        try:
            if force:
                self.signal_group(signal.SIGTERM)
                try:
                    self.process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    self.signal_group(signal.SIGKILL)
                    self.process.wait(timeout=3)
                # A shell/tool child can outlive its session leader.
                self.signal_group(signal.SIGKILL)
            else:
                try:
                    self.process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.signal_group(signal.SIGKILL)
                    self.process.wait(timeout=3)
                    raise
        finally:
            with self.fd_lock:
                if not self.closed.is_set():
                    self.closed.set()
                    os.close(self.master)


def shell_history_command(command, windows=None):
    if windows is None:
        windows = sys.platform == "win32"
    if windows:
        invocation = "& " + " ".join("'" + argument.replace("'", "''") + "'" for argument in command)
        return [
            "powershell.exe", "-NoLogo", "-NoProfile", "-Command",
            '1..90 | ForEach-Object { Write-Output "earlier console output $_" }; '
            + invocation + "; exit $LASTEXITCODE",
        ]
    return [
        "/bin/sh", "-c",
        'i=1; while [ "$i" -le 90 ]; do printf "earlier console output %s\\n" "$i"; '
        'i=$((i+1)); done; "$@"; exit "$?"',
        "terminal-test-shell", *command,
    ]


def destination_mutex_name(destination):
    canonical = os.path.realpath(destination).rstrip("\\/")
    if sys.platform == "win32":
        canonical = canonical.upper()
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()
    return ("Global\\" if sys.platform == "win32" else "") + "dotnet-package-skills-" + digest


def fixture_environment(directory):
    directory = Path(directory)
    return dict(
        os.environ,
        DOTNET_CLI_HOME=str(directory / "dotnet home"),
        NUGET_PACKAGES=str(directory / "package cache"),
        NUGET_HTTP_CACHE_PATH=str(directory / "http cache"),
        DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
        DOTNET_NOLOGO="1",
        DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="false",
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true",
    )
