import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("check_nuget_vulnerabilities.py")


def load_checker():
    spec = importlib.util.spec_from_file_location("check_nuget_vulnerabilities", SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load {SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def report(*, top_level=(), transitive=()):
    framework = {"framework": "net10.0"}
    if top_level:
        framework["topLevelPackages"] = list(top_level)
    if transitive:
        framework["transitivePackages"] = list(transitive)
    return {
        "version": 1,
        "parameters": "--vulnerable --include-transitive",
        "sources": ["https://api.nuget.org/v3/index.json"],
        "projects": [{"path": "fixture.csproj", "frameworks": [framework]}],
    }


def package(package_id, severity):
    return {
        "id": package_id,
        "resolvedVersion": "1.2.3",
        "vulnerabilities": [
            {
                "severity": severity,
                "advisoryurl": "https://github.com/advisories/GHSA-fixture",
            }
        ],
    }


class PackageAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.checker = load_checker()

    def test_no_findings_passes(self):
        self.assertEqual([], self.checker.find_blocking_vulnerabilities(report()))

    def test_top_level_moderate_fails(self):
        findings = self.checker.find_blocking_vulnerabilities(
            report(top_level=[package("Top.Level", "Moderate")])
        )
        self.assertEqual("Top.Level", findings[0].package_id)

    def test_transitive_high_fails(self):
        findings = self.checker.find_blocking_vulnerabilities(
            report(transitive=[package("Transitive.Package", "High")])
        )
        self.assertEqual("Transitive.Package", findings[0].package_id)

    def test_low_severity_does_not_block(self):
        self.assertEqual(
            [],
            self.checker.find_blocking_vulnerabilities(
                report(top_level=[package("Low.Risk", "Low")])
            ),
        )

    def test_invalid_json_fails_closed(self):
        with self.assertRaises(self.checker.AuditError):
            self.checker.parse_report("not json")

    def test_invalid_schema_fails_closed(self):
        with self.assertRaises(self.checker.AuditError):
            self.checker.find_blocking_vulnerabilities({"version": 1})

    def test_audit_command_failure_is_not_treated_as_clean(self):
        failed = subprocess.CompletedProcess(
            args=["dotnet"], returncode=42, stdout="", stderr="audit source unavailable"
        )
        with patch.object(self.checker.subprocess, "run", return_value=failed):
            with self.assertRaises(self.checker.AuditError):
                self.checker.run_audit("fixture.slnx", "dotnet")

    def test_main_reports_a_blocking_finding(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(
                json.dumps(report(top_level=[package("Blocked.Package", "Critical")])),
                encoding="utf-8",
            )
            self.assertEqual(1, self.checker.main(["--report", str(path)]))


if __name__ == "__main__":
    unittest.main()
