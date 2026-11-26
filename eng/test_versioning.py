import json
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest

from versioning import ROOT, check_project_locks, check_web_toolchain, product_version, release_notes


class VersioningTests(unittest.TestCase):
    def test_release_notes_require_exact_version_and_nonblank_unique_section(self):
        notes = "## [0.22.0] - 2026-11-26\n\n- Current note."
        self.assertEqual(
            release_notes(notes + "\n\n## [0.21.0] - 2026-11-22\n- Older note.", "v0.22.0", "0.22.0"),
            "- Current note.",
        )
        for invalid in (
            notes.replace("0.22.0", "0x22x0"),
            notes.replace("- Current note.", "  "),
            notes + "\n" + notes,
        ):
            with self.subTest(changelog=invalid), self.assertRaises(ValueError):
                release_notes(invalid, "v0.22.0", "0.22.0")
        with self.assertRaisesRegex(ValueError, "Tag .* differs"):
            release_notes(notes, "v0.21.0", "0.22.0")

    def test_stale_project_ranges_fail_while_external_package_ranges_are_independent(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "src/App/App.fsproj"
            project.parent.mkdir(parents=True)
            project.touch()
            lock = project.with_name("packages.lock.json")
            graph = {"dependencies": {"net11.0": {
                "library": {"type": "Project"},
                "middle": {"type": "Project", "dependencies": {
                    "Library": "[0.21.0, )", "External": "[0.21.0, )",
                }},
                "External": {"type": "Transitive"},
            }}}
            lock.write_text(json.dumps(graph), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Library requests"):
                check_project_locks(root, "0.22.0")
            graph["dependencies"]["net11.0"]["middle"]["dependencies"]["Library"] = "[0.22.0, )"
            lock.write_text(json.dumps(graph), encoding="utf-8")
            check_project_locks(root, "0.22.0")

    def test_web_rejects_drift_in_either_toolchain_pin(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".node-version").write_text("26.13.1\n", encoding="utf-8")
            package = root / "web/package.json"
            package.parent.mkdir()
            metadata = {"packageManager": "npm@12.2.3", "engines": {"node": "26.13.1", "npm": "12.2.3"}}
            package.write_text(json.dumps(metadata), encoding="utf-8")
            check_web_toolchain(root)
            for field, rejected in (("node", "26.13.0"), ("npm", "12.2.2")):
                with self.subTest(tool=field):
                    engines = metadata["engines"] | {field: rejected}
                    package.write_text(json.dumps(metadata | {"engines": engines}), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        check_web_toolchain(root)

    def test_effective_product_version_ignores_comments_and_detects_split_builds(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "global.json").write_bytes((ROOT / "global.json").read_bytes())
            for component in ("Cli", "Server"):
                project = root / f"src/ClaimCore.{component}/ClaimCore.{component}.fsproj"
                project.parent.mkdir(parents=True)
                project.write_text(
                    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework>'
                    '<!-- <Version>0.21.0</Version> --><Version>0.22.0</Version></PropertyGroup></Project>',
                    encoding="utf-8",
                )
            self.assertEqual(product_version(root), "0.22.0")
            project.write_text(project.read_text(encoding="utf-8").replace("0.22.0", "0.23.0"), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Expected one product version"):
                product_version(root)

    def test_version_only_incremental_build_replaces_the_fsharp_reference(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("global.json", "Directory.Build.targets"):
                (root / name).write_bytes((ROOT / name).read_bytes())
            (root / "Probe.fs").write_text("module Probe\nlet value = 1\n", encoding="utf-8")
            project = root / "Probe.fsproj"
            project.write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework>'
                '<ProduceReferenceAssembly>true</ProduceReferenceAssembly>'
                '<NuGetAudit>false</NuGetAudit></PropertyGroup>'
                '<ItemGroup><Compile Include="Probe.fs" /></ItemGroup></Project>',
                encoding="utf-8",
            )

            def build(version, *options):
                run = subprocess.run(
                    ["dotnet", "build", "--disable-build-servers", f"-p:Version={version}", *options],
                    cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90,
                )
                self.assertEqual(run.returncode, 0, run.stdout)

            build("0.21.0")
            reference = root / "obj/Debug/net11.0/ref/Probe.dll"
            previous = reference.read_bytes()
            build("0.22.0", "--no-restore")
            updated = reference.read_bytes()
            self.assertNotEqual(previous, updated)
            self.assertEqual(updated, (root / "obj/Debug/net11.0/refint/Probe.dll").read_bytes())
