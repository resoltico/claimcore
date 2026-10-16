/** @param {import("./tooling-types.mjs").JsonRecord} descriptors @returns {Record<string, import("./localization-policy.mjs").Message> } */
export const businessMessages = (descriptors) => {
  /** @type {Record<string, import("./localization-policy.mjs").Message> } */
  const result = {};
  /** @param {string} owner @param {string} name @param {{label: string, meaning: string}} descriptor */
  const add = (owner, name, descriptor) => {
    for (const part of /** @type {const} */ (["label", "meaning"])) {
      const key = `${owner}.${name}.${part}`;
      if (Object.hasOwn(result, key)) {
        throw new Error("Duplicate business presentation identity.");
      }
      result[key] = [{ literal: descriptor[part] }];
    }
  };
  for (const field of [...descriptors["fields"], ...(descriptors["inputFields"] ?? [])]) {
    add("field", field.name, field);
  }
  for (const command of descriptors["commands"]) {
    add("command", command.kind, command);
    for (const group of command.inputs.groups ?? []) {
      add("group", group.name, group);
    }
  }
  return result;
};

/** @param {import("./tooling-types.mjs").JsonRecord} descriptors @returns {Record<string, string>} */
export const correctionTargets = (descriptors) => {
  /** @type {Record<string, string>} */
  const result = {};
  for (const command of descriptors["commands"]) {
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
