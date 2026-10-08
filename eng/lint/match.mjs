/**
 * Hold the occurrences found in the tree against the registry, in both directions.
 * @param {import("./model.mjs").Registry} registry
 * @param {import("./model.mjs").Occurrence[]} occurrences
 * @param {import("./model.mjs").Report} report
 */
export function reconcile(registry, occurrences, report) {
  const byId = new Map(registry.exceptions.map((entry) => [entry.id, entry]));
  /** @type {Map<string, number>} */
  const counts = new Map();
  /** @type {Map<string, Map<string, number>>} */
  const matchedRules = new Map();
  for (const occurrence of occurrences) {
    const entry =
      occurrence.kind === "inline"
        ? inlineEntry(occurrence, byId, report)
        : configEntry(occurrence, registry, report);
    if (entry) {
      counts.set(entry.id, (counts.get(entry.id) ?? 0) + 1);
      const rules = matchedRules.get(entry.id) ?? new Map();
      rules.set(occurrence.rule, (rules.get(occurrence.rule) ?? 0) + 1);
      matchedRules.set(entry.id, rules);
    }
  }
  checkCounts(registry, counts, matchedRules, report);
}

/**
 * @param {import("./model.mjs").Registry} registry
 * @param {Map<string, number>} counts
 * @param {Map<string, Map<string, number>>} matchedRules
 * @param {import("./model.mjs").Report} report
 */
function checkCounts(registry, counts, matchedRules, report) {
  for (const entry of registry.exceptions) {
    const seen = counts.get(entry.id) ?? 0;
    for (const rule of entry.rules) {
      const matched = matchedRules.get(entry.id)?.get(rule) ?? 0;
      const expected = entry.rules.length === 1 ? entry.count : 1;
      if (matched !== expected) {
        report.add(
          `Rule ${rule} in exception ${entry.id} requires ${expected} occurrence(s), found ${matched}.`,
        );
      }
    }
    if (seen === 0) {
      report.add(
        `Stale exception ${entry.id}: no ${entry.tool} suppression of ${entry.rules.join(", ")} remains in ${entry.file}.`,
      );
    } else if (seen !== entry.count) {
      report.add(
        `Exception ${entry.id} covers ${entry.count} occurrence(s) but ${entry.file} has ${seen}.`,
      );
    }
  }
}

/**
 * @param {import("./model.mjs").Occurrence} occurrence
 * @param {Map<string, import("./model.mjs").LintException>} byId
 * @param {import("./model.mjs").Report} report
 */
function inlineEntry(occurrence, byId, report) {
  const where = `${occurrence.file}:${occurrence.line} ${occurrence.tool} suppression of ${occurrence.rule}`;
  if (occurrence.rule === "*") {
    report.add(`${where} is a blanket suppression; name the exact rules.`);
  }
  if (occurrence.id === null) {
    report.add(
      `${where} needs a nearby 'lint-exception: LX-0000' reference to a registered exception.`,
    );
    return null;
  }
  const entry = byId.get(occurrence.id);
  if (!entry) {
    report.add(`${where} references unknown exception ${occurrence.id}.`);
    return null;
  }
  const problems = [
    [entry.kind !== "inline", "is not an inline exception"],
    [entry.file !== occurrence.file, `is registered for ${entry.file}`],
    [entry.tool !== occurrence.tool, `is registered for ${entry.tool}`],
    [!entry.rules.includes(occurrence.rule), `does not list ${occurrence.rule}`],
  ].filter(([failed]) => failed);
  for (const [, message] of problems) {
    report.add(`${where}: ${entry.id} ${String(message)}.`);
  }
  return problems.length === 0 ? entry : null;
}

/**
 * @param {import("./model.mjs").Occurrence} occurrence
 * @param {import("./model.mjs").Registry} registry
 * @param {import("./model.mjs").Report} report
 */
function configEntry(occurrence, registry, report) {
  const entry = registry.exceptions.find(
    (candidate) =>
      candidate.kind === "config" &&
      candidate.file === occurrence.file &&
      candidate.tool === occurrence.tool &&
      candidate.rules.includes(occurrence.rule),
  );
  if (!entry) {
    report.add(
      `${occurrence.file} configures an unregistered ${occurrence.tool} exception: ${occurrence.rule}.`,
    );
  }
  return entry ?? null;
}
