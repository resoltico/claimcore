import assert from "node:assert/strict";
import test from "node:test";
import { executable } from "./executable.mjs";

/** Synthetic module observations exercise selection, not native Windows execution.
 * @param {unknown} libraries @param {()=>void} body */
function observation(libraries, body) {
  const platform = Object.getOwnPropertyDescriptor(process, "platform");
  assert.ok(platform);
  const { report: declaredReport } = process;
  const report = /** @type {import('./executable.mjs').NativeReport} */ (declaredReport);
  const original = report.getReport;
  const root = process.env["SystemRoot"];
  const flags = [report.excludeEnv, report.excludeNetwork];
  try {
    Object.defineProperty(process, "platform", { value: "win32" });
    process.env["SystemRoot"] = "Z:\\forged-system-root";
    report.getReport = () => {
      assert.equal(report.excludeEnv, true);
      assert.equal(report.excludeNetwork, true);
      return { sharedObjects: libraries };
    };
    body();
  } finally {
    report.getReport = original;
    Object.defineProperty(process, "platform", platform);
    if (root === undefined) {
      delete process.env["SystemRoot"];
    } else {
      process.env["SystemRoot"] = root;
    }
    assert.deepEqual([report.excludeEnv, report.excludeNetwork], flags);
  }
}

test("Windows PowerShell selection follows the actual loaded system module, preserving drive and case", () => {
  observation(["D:\\Windows\\System32\\KERNEL32.DLL", "D:\\other\\library.dll"], () => {
    assert.equal(
      executable("powershell"),
      "D:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
    );
    assert.equal(executable("node"), process.execPath);
    assert.equal(executable("tar"), "D:\\Windows\\System32\\tar.exe");
  });
  observation(["E:\\windows\\system32\\kernel32.dll"], () => {
    assert.equal(
      executable("powershell"),
      "E:\\windows\\system32\\WindowsPowerShell\\v1.0\\powershell.exe",
    );
  });
});

test("Windows PowerShell refuses missing, ambiguous and non-system native module observations", () => {
  const failures = [
    undefined,
    [],
    [null],
    ["kernel32.dll"],
    ["D:\\private\\kernel32.dll"],
    ["D:\\Windows\\System32\\..\\System32\\kernel32.dll"],
    ["D:\\Windows\\System32\\kernel32.dll", "E:\\Windows\\System32\\kernel32.dll"],
  ];
  for (const libraries of failures) {
    observation(libraries, () => assert.throws(() => executable("powershell")));
  }
  if (process.platform !== "win32") {
    assert.throws(() => executable("powershell"), /requires Windows/u);
    assert.equal(executable("tar"), "tar");
  }
});
