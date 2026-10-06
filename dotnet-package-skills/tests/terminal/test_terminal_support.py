"""Focused adapter tests; POSIX cases launch real, owned Python processes."""

import importlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch


class SupportTest(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(
            importlib.util.find_spec("terminal_support"),
            "The portable terminal adapter has not been implemented.",
        )
        self.support = importlib.import_module("terminal_support")


class PortableHelpers(SupportTest):
    def test_utf8_decoder_retains_characters_split_at_every_byte(self):
        text = "Caf\u00e9 \u6e2c\u8a66 \U0001f9ea e\u0301\x1b[6n"
        decoder = self.support.OutputDecoder()
        actual = "".join(decoder.decode(bytes([byte])) for byte in text.encode("utf-8"))
        self.assertEqual(text, actual + decoder.decode(b"", final=True))

    def test_utf8_decoder_rejects_invalid_and_incomplete_output(self):
        with self.assertRaises(UnicodeDecodeError):
            self.support.OutputDecoder().decode(b"\xff")
        decoder = self.support.OutputDecoder()
        self.assertEqual("", decoder.decode(b"\xf0\x9f"))
        with self.assertRaises(UnicodeDecodeError):
            decoder.decode(b"", final=True)

    def test_dimensions_use_rows_then_columns(self):
        self.assertEqual(
            (24, 100, 0, 0),
            struct.unpack("HHHH", self.support.winsize(24, 100)),
        )

    def test_invalid_dimensions_are_rejected_before_opening_a_terminal(self):
        for value in (0, -1, 65536, 1.5, True):
            for rows, columns in ((value, 80), (24, value)):
                with self.subTest(rows=rows, columns=columns):
                    with self.assertRaises(ValueError):
                        self.support.winsize(rows, columns)

    def test_shell_history_preserves_unix_arguments_without_interpolation(self):
        command = ["/a path/tool", "install", "--destination", "a 'quoted' path", "; exit 99"]
        wrapped = self.support.shell_history_command(command, windows=False)
        self.assertEqual(["/bin/sh", "-c"], wrapped[:2])
        self.assertIn('"$@"', wrapped[2])
        self.assertEqual(command, wrapped[4:])

    def test_shell_history_preserves_the_existing_powershell_invocation(self):
        command = [r"C:\a path\tool.exe", "install", "a 'quoted' path"]
        wrapped = self.support.shell_history_command(command, windows=True)
        self.assertEqual(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"],
            wrapped[:4],
        )
        self.assertIn("'a ''quoted'' path'", wrapped[4])
        self.assertTrue(wrapped[4].endswith("; exit $LASTEXITCODE"))

    def test_mutex_name_matches_the_existing_platform_canonicalization(self):
        import hashlib

        with tempfile.TemporaryDirectory(prefix="terminal lock ") as scratch:
            destination = Path(scratch) / "Skills"
            canonical = os.path.realpath(destination).rstrip("\\/")
            if sys.platform == "win32":
                canonical = canonical.upper()
            digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()
            prefix = "Global\\" if sys.platform == "win32" else ""
            self.assertEqual(
                prefix + "dotnet-package-skills-" + digest,
                self.support.destination_mutex_name(destination),
            )
            self.assertEqual(
                self.support.destination_mutex_name(destination),
                self.support.destination_mutex_name(destination / ".." / "Skills"),
            )

    def test_fixture_environment_disables_sdk_first_run_global_mutations_and_network_checks(self):
        with tempfile.TemporaryDirectory(prefix="terminal sdk ") as scratch:
            environment = self.support.fixture_environment(scratch)
            self.assertEqual("false", environment.get("DOTNET_GENERATE_ASPNET_CERTIFICATE"))
            self.assertEqual("false", environment.get("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"))
            self.assertEqual("true", environment.get("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"))


@unittest.skipUnless(sys.platform in ("darwin", "linux"), "Real POSIX PTYs require macOS or Linux.")
class PosixProcesses(SupportTest):
    def setUp(self):
        super().setUp()
        self.scratch = tempfile.TemporaryDirectory(prefix="terminal adapter ")
        self.addCleanup(self.scratch.cleanup)

    def spawn(self, source, *arguments, dimensions=(24, 80)):
        environment = dict(os.environ, TERM="xterm-256color", TERMINAL_TEST="isolated")
        process = self.support.PosixPtyProcess.spawn(
            [sys.executable, "-c", source, *arguments],
            cwd=self.scratch.name, env=environment, dimensions=dimensions,
        )
        output = queue.Queue()

        def read():
            try:
                while True:
                    output.put(process.read(65536))
            except EOFError:
                output.put(None)
            except Exception as error:
                output.put(error)

        reader = threading.Thread(target=read, daemon=True)
        reader.start()

        def close():
            process.close(force=True)
            reader.join(timeout=3)
            self.assertFalse(reader.is_alive(), "PTY reader did not stop after close.")

        self.addCleanup(close)
        return process, output

    def read_until(self, output, expected=None):
        text = ""
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            data = output.get(timeout=max(0.01, deadline - time.monotonic()))
            if isinstance(data, Exception):
                raise data
            if data is None:
                if expected is not None:
                    self.assertIn(expected, text)
                return text
            text += data
            if expected is not None and expected in text:
                return text
        self.fail(f"PTY output timed out: {text!r}")

    def test_child_has_a_controlling_terminal_dimensions_environment_and_cwd(self):
        process, output = self.spawn(
            "import json,os; print(json.dumps({"
            "'tty': [os.isatty(fd) for fd in (0,1,2)],"
            "'foreground': os.tcgetpgrp(0) == os.getpgrp(),"
            "'session': os.getsid(0) == os.getpid(),"
            "'size': list(os.get_terminal_size(0)),"
            "'env': os.environ['TERMINAL_TEST'], 'cwd': os.getcwd()}), flush=True)",
            dimensions=(29, 91),
        )
        observed = json.loads(self.read_until(output).strip())
        self.assertEqual([True, True, True], observed["tty"])
        self.assertTrue(observed["foreground"])
        self.assertTrue(observed["session"])
        self.assertEqual([91, 29], observed["size"])
        self.assertEqual("isolated", observed["env"])
        self.assertEqual(os.path.realpath(self.scratch.name), observed["cwd"])
        process.close()
        self.assertEqual(0, process.exitstatus)

    def test_read_decodes_real_utf8_chunks_and_drains_output_after_exit(self):
        process, output = self.spawn(
            "import os,time; data='Caf\\u00e9 \\U0001f9ea'.encode();"
            "\nfor byte in data: os.write(1, bytes([byte])); time.sleep(.005)"
            "\nos.write(1, b'x' * 131072 + b'FINAL'); os._exit(7)"
        )
        self.assertEqual("Caf\u00e9 \U0001f9ea" + "x" * 131072 + "FINAL", self.read_until(output))
        process.close()
        self.assertEqual(7, process.exitstatus)

    def test_live_resize_delivers_sigwinch_and_new_dimensions(self):
        process, output = self.spawn(
            "import json,os,signal;"
            "\ndef resized(*_): print(json.dumps(list(os.get_terminal_size(0))), flush=True)"
            "\nsignal.signal(signal.SIGWINCH, resized); print('READY', flush=True); signal.pause()"
        )
        self.read_until(output, "READY")
        process.setwinsize(17, 63)
        self.assertIn("[63, 17]", self.read_until(output))
        process.close()
        self.assertEqual(0, process.exitstatus)

    def test_keys_are_utf8_without_echo_or_canonical_line_buffering(self):
        process, output = self.spawn(
            "import os; print('READY', flush=True); data=b''"
            "\nwhile len(data) < 3: data += os.read(0, 3-len(data))"
            "\nprint('KEY:' + data.decode(), flush=True)"
        )
        self.read_until(output, "READY")
        process.write("q\u00e9")
        self.assertEqual("KEY:q\u00e9", self.read_until(output).strip())
        process.close()
        self.assertEqual(0, process.exitstatus)

    def test_control_c_is_input_when_the_application_disables_isig(self):
        process, output = self.spawn(
            "import os,termios; attrs=termios.tcgetattr(0); attrs[3] &= ~termios.ISIG;"
            "termios.tcsetattr(0, termios.TCSANOW, attrs); print('READY', flush=True);"
            "print('KEY:' + str(os.read(0,1)[0]), flush=True)"
        )
        self.read_until(output, "READY")
        process.write("\x03")
        self.assertIn("KEY:3", self.read_until(output))
        process.close()
        self.assertEqual(0, process.exitstatus)

    def test_control_c_reports_signal_exit_when_isig_is_enabled(self):
        process, output = self.spawn(
            "import signal,time; signal.signal(signal.SIGINT, signal.SIG_DFL);"
            "print('READY', flush=True); time.sleep(30)"
        )
        self.read_until(output, "READY")
        process.write("\x03")
        self.read_until(output)
        process.close()
        self.assertEqual(-signal.SIGINT, process.exitstatus)

    def test_force_close_kills_an_unresponsive_owned_group_and_is_idempotent(self):
        process, output = self.spawn(
            "import signal,time; signal.signal(signal.SIGTERM, signal.SIG_IGN);"
            "print('READY', flush=True); time.sleep(30)"
        )
        self.read_until(output, "READY")
        process.close(force=True)
        process.close(force=True)
        self.assertEqual(-signal.SIGKILL, process.exitstatus)
        self.read_until(output)

    def test_force_close_does_not_signal_an_unrelated_process(self):
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        try:
            process, output = self.spawn("import time; print('READY', flush=True); time.sleep(30)")
            self.read_until(output, "READY")
            process.close(force=True)
            self.assertIsNone(unrelated.poll())
        finally:
            unrelated.terminate()
            unrelated.wait(timeout=5)

    def test_force_close_cleans_owned_children_even_after_the_session_leader_exits(self):
        worker = (
            "import signal,time; signal.signal(signal.SIGHUP, signal.SIG_IGN);"
            "signal.signal(signal.SIGTERM, signal.SIG_IGN); print('READY', flush=True); time.sleep(30)"
        )
        process, output = self.spawn(
            "import os,subprocess,sys; child=subprocess.Popen("
            "[sys.executable, '-c', sys.argv[1]], stdin=subprocess.DEVNULL,"
            "stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True);"
            "print('WORKER:' + str(child.pid) + ':' + child.stdout.readline().strip(), flush=True);"
            "os._exit(0)",
            worker,
        )
        text = self.read_until(output, "READY")
        pid = int(text.split("WORKER:")[1].split(":")[0])
        try:
            process.process.wait(timeout=5)
            self.read_until(output)
            process.close(force=True)
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                state = subprocess.run(
                    ["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True, timeout=3,
                ).stdout.strip()
                if not state or state.startswith("Z"):
                    break
                time.sleep(0.05)
            self.assertTrue(not state or state.startswith("Z"), f"Owned child {pid} is still running.")
        finally:
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass

    def test_failed_launch_reports_an_error_exit_instead_of_success(self):
        process = self.support.PosixPtyProcess.spawn(
            [str(Path(self.scratch.name) / "missing apphost")],
            cwd=self.scratch.name, env=dict(os.environ), dimensions=(24, 80),
        )
        self.addCleanup(process.close, force=True)
        text = ""
        while True:
            try:
                text += process.read(65536)
            except EOFError:
                break
        process.close()
        self.assertNotEqual(0, process.exitstatus)
        self.assertIn("FileNotFoundError", text)

    def test_spawn_failure_closes_both_owned_descriptors(self):
        before = set(os.listdir("/dev/fd"))
        with self.assertRaises(FileNotFoundError):
            self.support.PosixPtyProcess.spawn(
                [sys.executable, "-c", "pass"],
                cwd=str(Path(self.scratch.name) / "missing directory"),
                env=dict(os.environ), dimensions=(24, 80),
            )
        self.assertEqual(before, set(os.listdir("/dev/fd")))

    def test_reader_cannot_consume_another_file_after_its_master_descriptor_is_closed(self):
        process = self.support.PosixPtyProcess.spawn(
            [sys.executable, "-c", "import time; time.sleep(30)"],
            cwd=self.scratch.name, env=dict(os.environ), dimensions=(24, 80),
        )
        self.addCleanup(process.close, force=True)
        selected, resume = threading.Event(), threading.Event()
        output = queue.Queue()
        replacement = Path(self.scratch.name) / "not terminal output.txt"
        replacement.write_bytes(b"not terminal output")

        def delayed_select(*_):
            selected.set()
            if not resume.wait(timeout=5):
                raise TimeoutError("The test did not resume the PTY reader.")
            return [process.master], [], []

        def read():
            try:
                output.put(process.read(65536))
            except EOFError:
                output.put("EOF")
            except Exception as error:
                output.put(error)

        with patch.object(self.support.select, "select", side_effect=delayed_select):
            reader = threading.Thread(target=read, daemon=True)
            reader.start()
            self.assertTrue(selected.wait(timeout=5))
            process.close(force=True)
            descriptor = os.open(replacement, os.O_RDONLY)
            try:
                self.assertEqual(process.master, descriptor, "The regression requires descriptor reuse.")
                resume.set()
                self.assertEqual("EOF", output.get(timeout=5))
            finally:
                resume.set()
                reader.join(timeout=3)
                os.close(descriptor)
                self.assertFalse(reader.is_alive())

    def test_symlink_and_dot_paths_share_a_case_sensitive_posix_mutex_name(self):
        destination = Path(self.scratch.name) / "Skills"
        destination.mkdir()
        alias = Path(self.scratch.name) / "alias"
        alias.symlink_to(destination, target_is_directory=True)
        self.assertEqual(
            self.support.destination_mutex_name(destination),
            self.support.destination_mutex_name(alias / "." / "child" / ".."),
        )
        self.assertNotEqual(
            self.support.destination_mutex_name(destination),
            self.support.destination_mutex_name(destination.with_name("skills")),
        )

    def test_shell_history_executes_the_exact_command_and_preserves_failure(self):
        command = self.support.shell_history_command(
            [sys.executable, "-c", "import sys; print(sys.argv[1]); sys.exit(7)", "a 'quoted' path"],
            windows=False,
        )
        result = subprocess.run(command, capture_output=True, text=True, timeout=10)
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertEqual(90, result.stdout.count("earlier console output "))
        self.assertTrue(result.stdout.endswith("a 'quoted' path\n"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
