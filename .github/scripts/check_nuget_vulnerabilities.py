#!/usr/bin/env python3
"""Fail CI on moderate-or-higher findings in a NuGet JSON audit report."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence


SEVERITY = {"Low": 0, "Moderate": 1, "High": 2, "Critical": 3}


class AuditError(RuntimeError):
    """The audit did not produce a trustworthy report."""


@dataclass(frozen=True)
class Finding:
    project: str
    framework: str
    package_id: str
    severity: str
    advisory_url: str


def parse_report(contents: str) -> dict[str, Any]:
    try:
        value = json.loads(contents.lstrip("\ufeff"))
    except json.JSONDecodeError as error:
        raise AuditError(f"NuGet returned invalid JSON: {error.msg}") from error
    if not isinstance(value, dict):
        raise AuditError("NuGet report root must be an object")
    return value


def _required_list(value: dict[str, Any], key: str) -> list[Any]:
    items = value.get(key)
    if not isinstance(items, list):
        raise AuditError(f"NuGet report field '{key}' must be an array")
    return items


def find_blocking_vulnerabilities(report: dict[str, Any]) -> list[Finding]:
    if report.get("version") != 1:
        raise AuditError("NuGet report version is missing or unsupported")

    sources = _required_list(report, "sources")
    if not sources or not all(isinstance(source, str) and source for source in sources):
        raise AuditError("NuGet report contains no valid audit source")

    projects = _required_list(report, "projects")
    if not projects:
        raise AuditError("NuGet report contains no projects")

    findings: list[Finding] = []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str):
            raise AuditError("NuGet report contains an invalid project")
        frameworks = project.get("frameworks", [])
        if not isinstance(frameworks, list):
            raise AuditError("NuGet project frameworks must be an array")
        for framework in frameworks:
            if not isinstance(framework, dict) or not isinstance(framework.get("framework"), str):
                raise AuditError("NuGet report contains an invalid framework")
            for package_group in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(package_group, [])
                if not isinstance(packages, list):
                    raise AuditError(f"NuGet field '{package_group}' must be an array")
                for package in packages:
                    findings.extend(_read_package(project, framework, package))
    return findings


def _read_package(
    project: dict[str, Any], framework: dict[str, Any], package: Any
) -> list[Finding]:
    if not isinstance(package, dict) or not isinstance(package.get("id"), str):
        raise AuditError("NuGet report contains an invalid package")
    vulnerabilities = package.get("vulnerabilities")
    if not isinstance(vulnerabilities, list) or not vulnerabilities:
        raise AuditError(f"NuGet listed vulnerable package '{package['id']}' without advisories")

    findings: list[Finding] = []
    for vulnerability in vulnerabilities:
        if not isinstance(vulnerability, dict):
            raise AuditError(f"NuGet package '{package['id']}' has an invalid advisory")
        severity = vulnerability.get("severity")
        advisory_url = vulnerability.get("advisoryurl")
        if severity not in SEVERITY:
            raise AuditError(f"NuGet package '{package['id']}' has unknown severity '{severity}'")
        if not isinstance(advisory_url, str) or not advisory_url:
            raise AuditError(f"NuGet package '{package['id']}' has no advisory URL")
        if SEVERITY[severity] >= SEVERITY["Moderate"]:
            findings.append(
                Finding(
                    project=project["path"],
                    framework=framework["framework"],
                    package_id=package["id"],
                    severity=severity,
                    advisory_url=advisory_url,
                )
            )
    return findings


def run_audit(solution: str, dotnet: str) -> dict[str, Any]:
    command = [
        dotnet,
        "list",
        solution,
        "package",
        "--vulnerable",
        "--include-transitive",
        "--format",
        "json",
        "--output-version",
        "1",
    ]
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "no diagnostic output"
        raise AuditError(f"NuGet audit command exited {result.returncode}: {detail}")
    return parse_report(result.stdout)


def main(arguments: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("solution", nargs="?")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    options = parser.parse_args(arguments)

    if bool(options.solution) == bool(options.report):
        parser.error("provide either a solution path or --report")

    try:
        report = (
            parse_report(options.report.read_text(encoding="utf-8"))
            if options.report
            else run_audit(options.solution, options.dotnet)
        )
        findings = find_blocking_vulnerabilities(report)
    except (AuditError, OSError) as error:
        print(f"package audit failed: {error}", file=sys.stderr)
        return 2

    for finding in findings:
        print(
            f"{finding.severity}: {finding.package_id} in {finding.project} "
            f"({finding.framework}) - {finding.advisory_url}",
            file=sys.stderr,
        )
    if findings:
        return 1

    print("no moderate-or-higher advisories")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
