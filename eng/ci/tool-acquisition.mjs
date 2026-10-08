// Only owned, finite acquisition phases may cross privacy-safe scanner diagnostics.
/** @typedef {"MANIFEST" | "CACHE" | "DOWNLOAD" | "INTEGRITY" | "UNPACK" | "PUBLISH"} AcquisitionPhase */
const phases = new Set(["MANIFEST", "CACHE", "DOWNLOAD", "INTEGRITY", "UNPACK", "PUBLISH"]);
/** @type {WeakMap<Error, AcquisitionPhase>} */
const failures = new WeakMap();

class ToolAcquisitionError extends Error {
  /** @param {AcquisitionPhase} phase */
  constructor(phase) {
    super(
      phase === "INTEGRITY"
        ? "Pinned tool failed integrity verification at INTEGRITY."
        : `Pinned tool acquisition failed at ${phase}.`,
    );
    failures.set(this, phase);
  }
}

/** @template T @param {AcquisitionPhase} phase @param {() => T | Promise<T>} action @returns {Promise<T>} */
export async function acquisitionStep(phase, action) {
  if (!phases.has(phase)) {
    throw new Error("Unknown tool acquisition phase.");
  }
  try {
    return await action();
  } catch {
    // Discard provider details: exception messages, causes and paths may contain sensitive data.
    throw new ToolAcquisitionError(phase);
  }
}

/** @param {unknown} error @returns {string} */
export function acquisitionDiagnostic(error) {
  const phase = failures.get(/** @type {Error} */ (error));
  return phase === undefined ? "" : ` (${phase})`;
}
