from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from native_test_report import require_completed


class NativeCompletionTests(unittest.TestCase):
    def test_requires_fresh_nonempty_complete_results(self):
        with TemporaryDirectory() as directory:
            report = Path(directory) / "results.trx"
            with self.assertRaises(FileNotFoundError):
                require_completed(report)

            def write(total, executed, passed):
                report.write_text(
                    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    f'<ResultSummary><Counters total="{total}" executed="{executed}" passed="{passed}"/>'
                    '</ResultSummary></TestRun>',
                    encoding="utf-8",
                )

            write(2, 2, 2)
            require_completed(report)
            for counts in ((0, 0, 0), (2, 1, 1), (2, 2, 1)):
                with self.subTest(counts=counts), self.assertRaisesRegex(ValueError, "incomplete"):
                    write(*counts)
                    require_completed(report)

            report.write_text("<TestRun />", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "one TRX result summary"):
                require_completed(report)
