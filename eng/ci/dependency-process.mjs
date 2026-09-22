import { spawnSync } from "node:child_process";

export function jsonProcess(
  file,
  args,
  cwd,
  allowed = [0],
  execute = spawnSync,
  pause = (ms) =>
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms),
) {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const result = execute(file, args, {
      cwd,
      encoding: "utf8",
      timeout: 120000,
      maxBuffer: 16 * 1024 * 1024,
      shell: false,
    });
    if (!result.error && allowed.includes(result.status)) {
      if (!result.stdout?.trim()) throw new Error("DEPENDENCY_METADATA_EMPTY");
      try {
        return JSON.parse(result.stdout);
      } catch {
        throw new Error("DEPENDENCY_METADATA_INVALID");
      }
    }
    const transient =
      ["ETIMEDOUT", "EAI_AGAIN", "ECONNRESET"].includes(result.error?.code) ||
      /\b(?:EAI_AGAIN|ETIMEDOUT|ECONNRESET|E429|E502|E503|E504)\b/u.test(
        result.stderr ?? "",
      );
    if (!transient || attempt === 2)
      throw new Error(
        transient
          ? "DEPENDENCY_METADATA_UNAVAILABLE"
          : "DEPENDENCY_COMMAND_FAILED",
      );
    pause((attempt + 1) * 1000);
  }
  throw new Error("DEPENDENCY_METADATA_UNAVAILABLE");
}
