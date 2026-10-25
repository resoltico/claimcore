/** @param {import("./tooling-types.mjs").JsonRecord} operationCatalog @returns {Record<string, string>} */
export const correctionTargets = (operationCatalog) => {
  /** @type {Record<string, string>} */
  const result = {};
  for (const command of operationCatalog["acts"]) {
    for (const group of command.inputs.groups ?? []) {
      const target = `${group.name}.action`;
      if (Object.hasOwn(result, target)) {
        throw new Error("Duplicate correction target identity.");
      }
      result[target] = group.name;
    }
  }
  return result;
};
