/** Preserve descriptor literals when exporting them to ICU message text. @param {string} text */
export const icuLiteral = (text) =>
  text.replaceAll("'", "''").replace(/[{}]+/gu, (braces) => `'${braces}'`);

/** @param {import("./tooling-types.mjs").JsonRecord} semantic @returns {Record<string, string>} */
export const businessMessages = (semantic) => {
  /** @type {Record<string, string>} */
  const result = {};
  /** @param {string} owner @param {string} name @param {{label: string, meaning: string}} descriptor */
  const add = (owner, name, descriptor) => {
    for (const part of /** @type {const} */ (["label", "meaning"])) {
      const key = `${owner}.${name}.${part}`;
      if (Object.hasOwn(result, key)) {
        throw new Error("Duplicate business presentation identity.");
      }
      result[key] = icuLiteral(descriptor[part]);
    }
  };
  for (const field of semantic["fields"]) {
    add("field", field.name, field);
  }
  for (const command of semantic["commands"]) {
    add("command", command.kind, command);
    for (const group of command.inputs.groups ?? []) {
      add("group", group.name, group);
    }
  }
  return result;
};

/** @param {import("./tooling-types.mjs").JsonRecord} semantic @returns {Record<string, string>} */
export const correctionTargets = (semantic) => {
  /** @type {Record<string, string>} */
  const result = {};
  for (const command of semantic["commands"]) {
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
