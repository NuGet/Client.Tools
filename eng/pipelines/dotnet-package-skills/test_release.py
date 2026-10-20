"""Nonpublishing tests for the release YAML and its inline PowerShell."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

import yaml


class ReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).with_name("release.yml")
        assert path.is_file(), "The manual release pipeline must exist."
        cls.pipeline = yaml.safe_load(path.read_text(encoding="utf-8"))
        cls.jobs = {
            job["job"]: job
            for job in cls.pipeline["extends"]["parameters"]["stages"][0]["jobs"]
        }
        cls.scripts = {
            step["name"]: step["pwsh"]
            for job in cls.jobs.values()
            for step in job["steps"]
            if "pwsh" in step
        }

    def run_script(self, name, env, prelude=""):
        with tempfile.TemporaryDirectory(prefix="skills-release-test-") as directory:
            path = Path(directory, "step.ps1")
            path.write_text(prelude + "\n" + self.scripts[name], encoding="utf-8")
            return subprocess.run(
                ["pwsh", "-NoProfile", "-NonInteractive", "-File", str(path)],
                env={**os.environ, **env},
                capture_output=True,
                text=True,
                timeout=30,
            )

    def assert_result(self, result, error=None):
        output = result.stdout + result.stderr
        if error:
            self.assertNotEqual(result.returncode, 0, output)
            self.assertIn(error, output)
        else:
            self.assertEqual(result.returncode, 0, output)
        return output

    def test_manual_resource_and_template_contract(self):
        pipeline = self.pipeline
        self.assertEqual(pipeline["trigger"], "none")
        self.assertEqual(pipeline["pr"], "none")
        self.assertNotIn("schedules", pipeline)
        self.assertEqual(
            pipeline["resources"]["pipelines"],
            [{
                "pipeline": "official",
                "project": "internal",
                "source": r"NuGet\client.tools-ci",
                "branch": "refs/heads/main",
                "trigger": "none",
            }],
        )
        self.assertEqual(
            pipeline["extends"]["template"],
            "v1/1ES.Official.PipelineTemplate.yml@1esPipelines",
        )
        self.assertEqual(pipeline["resources"]["repositories"][0]["ref"], "refs/tags/release")
        settings = pipeline["extends"]["parameters"]
        self.assertEqual(settings["pool"]["name"], "NetCore1ESPool-Internal")
        self.assertEqual(settings["pool"]["demands"], "ImageOverride -equals windows.vs2026.amd64")
        policy = settings["settings"]["networkIsolationPolicy"].split(",")
        self.assertIn("NuGet", policy)
        self.assertIn("AzureStorage", policy)
        self.assertNotIn("Permissive", policy)
        self.assertEqual(
            {p["name"]: p["default"] for p in pipeline["parameters"]},
            {
                "PackageVersion": "REQUIRED",
                "NuGetOrgServiceConnection": "REQUIRED",
                "ReleaseApprovers": "REQUIRED",
            },
        )

    def test_approval_and_exact_publish_contract(self):
        stages = self.pipeline["extends"]["parameters"]["stages"]
        self.assertEqual([stage["stage"] for stage in stages], ["Release"])
        self.assertEqual(list(self.jobs), ["Validate", "Approve", "Publish"])
        validate, approve, publish = (self.jobs[n] for n in ("Validate", "Approve", "Publish"))
        self.assertEqual(approve["dependsOn"], "Validate")
        self.assertEqual(publish["dependsOn"], ["Validate", "Approve"])
        self.assertEqual(approve["pool"], "server")
        gate = approve["steps"][0]
        self.assertEqual(gate["task"], "ManualValidation@1")
        self.assertGreater(approve["timeoutInMinutes"], gate["timeoutInMinutes"])
        self.assertEqual(gate["inputs"]["onTimeout"], "reject")
        self.assertIs(gate["inputs"]["allowApproversToApproveTheirOwnRuns"], False)
        for field in ("approvers", "notifyUsers"):
            self.assertEqual(gate["inputs"][field], "${{ parameters.ReleaseApprovers }}")
        self.assertIn("$(ApprovedPackageSha256)", gate["inputs"]["instructions"])
        self.assertNotIn("templateContext", validate)
        download = next(s for s in validate["steps"] if s.get("task") == "DownloadPipelineArtifact@2")
        self.assertEqual(download["inputs"], {
            "buildType": "specific",
            "project": "7ea9116e-9fac-403d-b258-b31fcf1bb293",
            "definition": "1692",
            "buildVersionToDownload": "specific",
            "pipelineId": "$(resources.pipeline.official.runID)",
            "artifactName": "dotnet-package-skills-packages",
            "targetPath": "$(ReleasePackageRoot)",
        })
        for job in (validate, publish):
            self.assertEqual(job["condition"], "succeeded()")
            self.assertEqual(job["steps"][0], {"checkout": "none"})
            self.assertEqual(job["workspace"], {"clean": "all"})
        context = publish["templateContext"]
        self.assertEqual(context["type"], "releaseJob")
        self.assertIs(context["isProduction"], True)
        self.assertEqual(context["inputs"], [{
            "input": "pipelineArtifact",
            "pipeline": "official",
            "artifactName": "dotnet-package-skills-packages",
            "targetPath": "$(ReleasePackageRoot)",
        }])
        self.assertNotIn("outputs", context)
        self.assertFalse(any(s.get("task", "").startswith("NuGetToolInstaller") for s in publish["steps"]))
        for job in (approve, publish):
            self.assertEqual(
                job["variables"]["ApprovedPackageSha256"],
                "$[ dependencies.Validate.outputs['InspectPackage.PackageSha256'] ]",
            )
        tasks = [
            step for job in self.jobs.values() for step in job["steps"]
            if step.get("task", "").startswith("1ES.PublishNuGet@")
        ]
        self.assertEqual(len(tasks), 1)
        task = tasks[0]
        self.assertEqual(task["condition"], "succeeded()")
        self.assertEqual(task["inputs"], {
            "useDotNetTask": False,
            "packagesToPush": "$(ReleasePackagePath)",
            "packageParentPath": "$(ReleasePackageRoot)",
            "nuGetFeedType": "external",
            "publishPackageMetadata": True,
            "publishFeedCredentials": "${{ parameters.NuGetOrgServiceConnection }}",
        })
        for job in self.jobs.values():
            for step in job["steps"]:
                self.assertNotIn("continueOnError", step)
                self.assertNotIn("allowPackageConflicts", step.get("inputs", {}))
                if "pwsh" in step:
                    self.assertNotIn("${{", step["pwsh"], "Pass queue parameters via env, not script interpolation.")
        source_step = next(s for s in validate["steps"] if s.get("name") == "ValidateSource")
        self.assertEqual(source_step["env"], {
            "PACKAGE_VERSION": "${{ parameters.PackageVersion }}",
            "RELEASE_APPROVERS": "${{ parameters.ReleaseApprovers }}",
            "NUGET_SERVICE_CONNECTION": "${{ parameters.NuGetOrgServiceConnection }}",
            "RELEASE_REASON": "$(Build.Reason)",
            "RELEASE_SOURCE_BRANCH": "$(Build.SourceBranch)",
            "SOURCE_RUN_ID": "$(resources.pipeline.official.runID)",
            "SOURCE_BRANCH": "$(resources.pipeline.official.sourceBranch)",
            "SOURCE_COMMIT": "$(resources.pipeline.official.sourceCommit)",
            "SYSTEM_ACCESSTOKEN": "$(System.AccessToken)",
        })
        inspect = next(s for s in validate["steps"] if s.get("name") == "InspectPackage")
        self.assertEqual(inspect["env"], {
            "PACKAGE_VERSION": "${{ parameters.PackageVersion }}",
            "PACKAGE_ROOT": "$(ReleasePackageRoot)",
        })
        signature = next(s for s in validate["steps"] if s.get("name") == "VerifySignature")
        self.assertEqual(signature["env"], {"PACKAGE_PATH": "$(ReleasePackagePath)"})
        verify = next(s for s in publish["steps"] if s.get("name") == "VerifyApprovedPackage")
        self.assertEqual(verify["env"], {
            "PACKAGE_VERSION": "${{ parameters.PackageVersion }}",
            "PACKAGE_ROOT": "$(ReleasePackageRoot)",
            "APPROVED_SHA256": "$(ApprovedPackageSha256)",
            "NUGET_SERVICE_CONNECTION": "${{ parameters.NuGetOrgServiceConnection }}",
            "SYSTEM_ACCESSTOKEN": "$(System.AccessToken)",
        })
        self.assertLess(
            publish["steps"].index(next(s for s in publish["steps"] if s.get("name") == "VerifyApprovedPackage")),
            publish["steps"].index(task),
        )

    def test_powershell_parses(self):
        for name in self.scripts:
            with self.subTest(script=name):
                with tempfile.TemporaryDirectory(prefix="skills-release-parse-") as directory:
                    path = Path(directory, "step.ps1")
                    path.write_text(self.scripts[name], encoding="utf-8")
                    result = subprocess.run(
                        ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
                         "$errors = $null; "
                         "[System.Management.Automation.Language.Parser]::ParseFile("
                         "$env:SCRIPT_PATH, [ref]$null, [ref]$errors) | Out-Null; "
                         "if ($errors) { $errors | Write-Error; exit 1 }"],
                        env={**os.environ, "SCRIPT_PATH": str(path)},
                        capture_output=True, text=True, timeout=30,
                    )
                    self.assert_result(result)

    def source_env(self):
        return {
            "PACKAGE_VERSION": "0.1.0",
            "RELEASE_APPROVERS": "reviewer@example.com, second@example.com",
            "NUGET_SERVICE_CONNECTION": "owner-configured-nuget-org",
            "RELEASE_REASON": "Manual",
            "RELEASE_SOURCE_BRANCH": "refs/heads/main",
            "SOURCE_RUN_ID": "123",
            "SOURCE_BRANCH": "refs/heads/main",
            "SOURCE_COMMIT": "a" * 40,
            "SYSTEM_ACCESSTOKEN": "local-test-placeholder",
        }

    def build_metadata(self):
        return {
            "id": 123, "status": "completed", "result": "succeeded",
            "reason": "manual", "sourceBranch": "refs/heads/main",
            "sourceVersion": "a" * 40,
            "definition": {"id": 1692},
            "project": {"id": "7ea9116e-9fac-403d-b258-b31fcf1bb293"},
            "repository": {"id": "ed423c3e-c630-4205-8f89-b36ec762684b", "type": "TfsGit"},
        }

    def metadata_prelude(self, data):
        return (
            "function Invoke-RestMethod { param($Uri, $Headers, $Method)\n"
            "  $env:RESPONSE_JSON | ConvertFrom-Json -Depth 20\n"
            "}\n"
        ), {"RESPONSE_JSON": json.dumps(data)}

    def test_source_metadata_and_queue_inputs(self):
        prelude, response = self.metadata_prelude(self.build_metadata())
        env = {**self.source_env(), **response}
        self.assert_result(self.run_script("ValidateSource", env, prelude))
        for field, value, error in [
            ("PACKAGE_VERSION", "REQUIRED", "explicit package version"),
            ("PACKAGE_VERSION", "../0.1.0", "explicit package version"),
            ("PACKAGE_VERSION", "0.1.0'; throw 'injection", "explicit package version"),
            ("RELEASE_APPROVERS", "", "explicit approver"),
            ("RELEASE_APPROVERS", "REQUIRED", "explicit approver"),
            ("RELEASE_APPROVERS", "reviewer@example.com,", "explicit approver"),
            ("NUGET_SERVICE_CONNECTION", "REQUIRED", "service connection"),
            ("RELEASE_REASON", "IndividualCI", "manually from main"),
            ("RELEASE_SOURCE_BRANCH", "refs/heads/feature", "manually from main"),
            ("SOURCE_BRANCH", "refs/heads/feature", "official main build"),
            ("SOURCE_RUN_ID", "not-a-run", "official main build"),
        ]:
            with self.subTest(field=field, value=value):
                self.assert_result(
                    self.run_script("ValidateSource", {**env, field: value}, prelude), error,
                )
        for field, value in [
            ("result", "partiallySucceeded"), ("status", "inProgress"),
            ("sourceBranch", "refs/heads/feature"), ("reason", "pullRequest"),
            ("sourceVersion", "b" * 40), ("id", 456),
            ("definition", {"id": 1}), ("project", {"id": "other-project"}),
            ("repository", {"id": "other-repo", "type": "GitHub"}),
        ]:
            with self.subTest(metadata=field):
                prelude, response = self.metadata_prelude({**self.build_metadata(), field: value})
                self.assert_result(
                    self.run_script("ValidateSource", {**self.source_env(), **response}, prelude),
                    "successful official main build",
                )

    def write_package(self, directory, version="0.1.0", package_id="dotnet-package-skills",
                      filename=None, nuspec=None):
        path = Path(directory, filename or f"dotnet-package-skills.{version}.nupkg")
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("dotnet-package-skills.nuspec", nuspec or (
                '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
                f"<metadata><id>{package_id}</id><version>{version}</version></metadata></package>"
            ))
        return path

    def test_package_identity_accepts_stable_and_prerelease_without_changes(self):
        for version in ("0.1.0", "0.1.0-beta.26520.1"):
            with self.subTest(version=version), tempfile.TemporaryDirectory(prefix="skills-package-") as directory:
                path = self.write_package(directory, version)
                manifest = Path(directory, "_manifest", "spdx_2.2")
                manifest.mkdir(parents=True)
                (manifest / "manifest.spdx.json").write_text("{}", encoding="utf-8")
                before = path.read_bytes()
                output = self.assert_result(self.run_script("InspectPackage", {
                    "PACKAGE_VERSION": version, "PACKAGE_ROOT": directory,
                }))
                self.assertIn(hashlib.sha256(before).hexdigest().upper(), output)
                self.assertEqual(path.read_bytes(), before)

    def test_package_identity_rejects_missing_unexpected_and_mismatched_packages(self):
        cases = ("missing", "multiple", "unexpected-file", "nested", "wrong-id",
                 "wrong-version", "wrong-filename", "malformed-xml", "dtd", "extra-nuspec",
                 "package-in-manifest")
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory(prefix="skills-package-") as directory:
                if case != "missing":
                    path = self.write_package(
                        directory,
                        package_id="other-tool" if case == "wrong-id" else "dotnet-package-skills",
                        version="0.2.0" if case == "wrong-version" else "0.1.0",
                        filename="other.nupkg" if case == "wrong-filename" else "dotnet-package-skills.0.1.0.nupkg",
                        nuspec={
                            "malformed-xml": "<package>",
                            "dtd": '<!DOCTYPE package [<!ENTITY id "dotnet-package-skills">]><package/>',
                        }.get(case),
                    )
                    if case == "multiple":
                        self.write_package(directory, "0.2.0")
                    elif case == "unexpected-file":
                        Path(directory, "extra.snupkg").write_bytes(b"unexpected")
                    elif case == "nested":
                        folder = Path(directory, "nested")
                        folder.mkdir()
                        path.rename(folder / path.name)
                    elif case == "extra-nuspec":
                        with zipfile.ZipFile(path, "a") as archive:
                            archive.writestr("extra.nuspec", "<package/>")
                    elif case == "package-in-manifest":
                        manifest = Path(directory, "_manifest")
                        manifest.mkdir()
                        self.write_package(manifest, "0.2.0")
                result = self.run_script("InspectPackage", {
                    "PACKAGE_VERSION": "0.1.0", "PACKAGE_ROOT": directory,
                })
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertNotIn("variable=PackageSha256", result.stdout)

    def test_signature_step_rejects_unsigned_package(self):
        self.assertIsNotNone(shutil.which("nuget"), "nuget.exe is required for the unsigned-package test.")
        with tempfile.TemporaryDirectory(prefix="skills-package-") as directory:
            path = self.write_package(directory)
            self.assert_result(
                self.run_script("VerifySignature", {"PACKAGE_PATH": str(path)}),
                "NuGet signature verification failed",
            )

    def test_publish_checks_approved_hash_and_nuget_org_endpoint(self):
        endpoint = {
            "name": "owner-configured-nuget-org", "type": "externalnugetfeed",
            "url": "https://api.nuget.org/v3/index.json", "isReady": True,
        }
        with tempfile.TemporaryDirectory(prefix="skills-package-") as directory:
            path = self.write_package(directory)
            env = {
                "PACKAGE_ROOT": directory, "PACKAGE_VERSION": "0.1.0",
                "APPROVED_SHA256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                "NUGET_SERVICE_CONNECTION": endpoint["name"],
                "SYSTEM_ACCESSTOKEN": "local-test-placeholder",
            }
            prelude, response = self.metadata_prelude({"value": [endpoint]})
            self.assert_result(self.run_script("VerifyApprovedPackage", {**env, **response}, prelude))
            for endpoints in ([], [endpoint, endpoint],
                              [{**endpoint, "url": "https://other.example/v3/index.json"}],
                              [{**endpoint, "type": "Generic"}],
                              [{**endpoint, "isReady": False}]):
                with self.subTest(endpoints=endpoints):
                    prelude, response = self.metadata_prelude({"value": endpoints})
                    self.assert_result(
                        self.run_script("VerifyApprovedPackage", {**env, **response}, prelude),
                        "nuget.org service connection",
                    )
            prelude, response = self.metadata_prelude({"value": [endpoint]})
            self.assert_result(
                self.run_script("VerifyApprovedPackage", {**env, **response, "APPROVED_SHA256": ""}, prelude),
                "approved SHA-256",
            )
            with path.open("ab") as package:
                package.write(b"changed after approval")
            self.assert_result(
                self.run_script("VerifyApprovedPackage", {**env, **response}, prelude),
                "approved SHA-256",
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
