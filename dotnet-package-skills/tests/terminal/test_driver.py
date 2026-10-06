"""Focused shared-driver coverage without launching the tool."""

from pathlib import Path
import queue
import subprocess
import tempfile
import threading
from types import SimpleNamespace
import unittest

import pyte

from verify_picker import Terminal
import verify_picker as driver


class DriverTests(unittest.TestCase):
    def test_fixture_environment_keeps_sdk_and_http_caches_inside_owned_scratch(self):
        with tempfile.TemporaryDirectory(prefix="terminal environment ") as scratch:
            previous = driver.OPTIONS
            driver.OPTIONS = SimpleNamespace(artifacts=Path(scratch))
            fixture = driver.PickerRegression("test_empty_uninstall_does_not_prompt")
            try:
                fixture.setUp()
                for key, folder in (
                    ("DOTNET_CLI_HOME", "dotnet home"),
                    ("NUGET_HTTP_CACHE_PATH", "http cache"),
                ):
                    self.assertEqual(str(fixture.root / folder), fixture.environment.get(key))
                self.assertEqual("1", fixture.environment.get("DOTNET_CLI_TELEMETRY_OPTOUT"))
            finally:
                fixture.doCleanups()
                driver.OPTIONS = previous

    def terminal(self):
        terminal = Terminal.__new__(Terminal)
        self.replies = []
        terminal.process = SimpleNamespace(write=self.replies.append)
        terminal.screen = pyte.Screen(80, 24)
        terminal.stream = pyte.Stream(terminal.screen)
        terminal.output = queue.Queue()
        terminal.raw = []
        terminal.emulator = None
        terminal.ended = False
        return terminal

    def test_cursor_queries_split_between_reads_receive_the_current_position(self):
        terminal = self.terminal()
        for chunk in ("\x1b[3;7H\x1b[6", "n", "\x1b[5n"):
            terminal.output.put(chunk)
            terminal.pump()
        self.assertEqual(["\x1b[3;7R", "\x1b[0n"], self.replies)

    def test_replaced_history_screen_still_answers_queries_after_resize(self):
        terminal = self.terminal()
        terminal.screen = pyte.HistoryScreen(40, 12, history=2000)
        terminal.stream = pyte.Stream(terminal.screen)
        terminal.output.put("\x1b[10;30H\x1b[6n")
        terminal.pump()
        self.assertEqual(["\x1b[10;30R"], self.replies)

    def test_eof_is_processed_after_all_final_control_output(self):
        terminal = self.terminal()
        for data in ("final output", "\x1b[?25h", None):
            terminal.output.put(data)
            terminal.pump()
        self.assertTrue(terminal.ended)
        self.assertEqual("final output", terminal.text)
        self.assertFalse(terminal.screen.cursor.hidden)
        self.assertEqual("final output\x1b[?25h", "".join(terminal.raw))


class MutexFixtureTests(unittest.TestCase):
    def test_dotnet_fixture_holds_the_named_mutex_until_released(self):
        self.assertTrue(
            callable(getattr(driver, "mutex_holder", None)),
            "The portable named-mutex fixture has not been implemented.",
        )
        with tempfile.TemporaryDirectory(prefix="terminal mutex ") as scratch:
            previous = driver.OPTIONS
            driver.OPTIONS = SimpleNamespace(artifacts=Path(scratch), mutex_holder=None)
            try:
                assembly = driver.mutex_holder()
                name = "terminal-test-" + Path(scratch).name
                first = self.holder(assembly, name)
                self.assertEqual("locked\n", self.line(first))
                second = self.holder(assembly, name)
                with self.assertRaises(subprocess.TimeoutExpired):
                    second.wait(timeout=0.3)
                self.assertEqual(("", ""), first.communicate(input="\n", timeout=5))
                self.assertEqual(0, first.returncode)
                self.assertEqual("locked\n", self.line(second))
                self.assertEqual(("", ""), second.communicate(input="\n", timeout=5))
                self.assertEqual(0, second.returncode)
            finally:
                driver.OPTIONS = previous

    def holder(self, assembly, name):
        process = subprocess.Popen(
            ["dotnet", str(assembly), name],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8",
        )

        def close():
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)

        self.addCleanup(close)
        return process

    def line(self, process):
        output = queue.Queue()
        reader = threading.Thread(target=lambda: output.put(process.stdout.readline()), daemon=True)
        reader.start()
        line = output.get(timeout=15)
        reader.join(timeout=3)
        return line


if __name__ == "__main__":
    unittest.main(verbosity=2)
