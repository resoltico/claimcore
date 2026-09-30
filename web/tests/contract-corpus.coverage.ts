import {
  cliCases,
  cliEndpointInventory,
  endpointInventory,
  isObject,
  readUnknown,
  webCases,
} from "./contract-corpus.fixtures";

const localReference = (document: Record<string, unknown>, reference: string): unknown => {
  if (!reference.startsWith("#/")) {
    throw new Error("Only local generated schema references are valid.");
  }
  let current: unknown = document;
  for (const encoded of reference.slice(2).split("/")) {
    if (!isObject(current)) {
      throw new Error("Generated schema reference does not resolve.");
    }
    const segment = encoded.replaceAll("~1", "/").replaceAll("~0", "~");
    current = current[segment];
  }
  return current;
};

const reachableSchemaDiscriminators = (
  file: string,
  property: "kind" | "tag",
): ReadonlyArray<string> => {
  const document = readUnknown(file);
  if (!isObject(document)) {
    throw new Error("Generated response schema must be an object.");
  }
  const tags = new Set<string>();
  const visited = new Set<string>();
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) {
      for (const item of value) {
        visit(item);
      }
      return;
    }
    if (!isObject(value)) {
      return;
    }
    const reference = value["$ref"];
    if (typeof reference === "string" && !visited.has(reference)) {
      visited.add(reference);
      visit(localReference(document, reference));
    }
    const { properties } = value;
    const discriminator = isObject(properties) ? properties[property] : null;
    const constant = isObject(discriminator) ? discriminator["const"] : null;
    if (typeof constant === "string") {
      tags.add(constant);
    }
    for (const [name, child] of Object.entries(value)) {
      if (name !== "$defs" && name !== "$ref") {
        visit(child);
      }
    }
  };
  visit(document);
  return [...tags].sort();
};

const valueDiscriminators = (value: unknown, property: "kind" | "tag", tags: Set<string>): void => {
  if (Array.isArray(value)) {
    for (const item of value) {
      valueDiscriminators(item, property, tags);
    }
    return;
  }
  if (!isObject(value)) {
    return;
  }
  if (typeof value[property] === "string") {
    tags.add(value[property]);
  }
  for (const child of Object.values(value)) {
    valueDiscriminators(child, property, tags);
  }
};

export const webTagCoverage = () => {
  const cases = webCases().filter((item) => item.valid);
  return endpointInventory.map((endpoint) => {
    const actual = new Set<string>();
    for (const item of cases) {
      if (item.endpoint === endpoint) {
        valueDiscriminators(item.value, "tag", actual);
      }
    }
    return {
      endpoint,
      expected: reachableSchemaDiscriminators(
        `web-v3.endpoint.${endpoint}.response.schema.json`,
        "tag",
      ),
      actual: [...actual].sort(),
    };
  });
};

export const cliKindCoverage = () => {
  const cases = cliCases().filter((item) => item.valid);
  return cliEndpointInventory().map((endpoint) => {
    const actual = new Set<string>();
    for (const item of cases) {
      if (item.endpoint === endpoint) {
        valueDiscriminators(item.value, "kind", actual);
      }
    }
    return {
      endpoint,
      expected: reachableSchemaDiscriminators(
        `cli-v4.endpoint.${endpoint}.response.schema.json`,
        "kind",
      ),
      actual: [...actual].sort(),
    };
  });
};

export const webHostCoverage = () => {
  const statuses = new Set<number>();
  const phases = new Set<string>();
  const codes = new Set<string>();
  for (const item of webCases()) {
    if (!item.valid || item.endpoint !== null || !isObject(item.value)) {
      continue;
    }
    statuses.add(item.status);
    const phase = item.value["executionPhase"];
    if (phase === null || typeof phase === "string") {
      phases.add(String(phase));
    }
    const { code } = item.value;
    if (typeof code === "string" && code.length > 0) {
      codes.add(code);
    }
  }
  return {
    statuses: [...statuses].sort((left, right) => left - right),
    phases: [...phases].sort(),
    codes: [...codes].sort(),
  };
};
