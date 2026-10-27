import type { CatalogDocument } from "@contracts/client";
import { isObject } from "./client";
import type { FormValues } from "./forms";

export type CatalogEntry = CatalogDocument["entries"][number];
export type CatalogInput = CatalogEntry["inputs"][number];
export const bindingPath = (input: CatalogInput): string => input.path.join(".");
export const applies = (binding: Pick<CatalogInput, "branches">, values: FormValues): boolean =>
  binding.branches.every((branch) => values[branch.path.join(".")] === branch.value);
export const atPath = (value: unknown, path: ReadonlyArray<string>): unknown => {
  let current = value;
  for (const segment of path) {
    if (!isObject(current)) {
      return undefined;
    }
    current = current[segment];
  }
  return current;
};
const assign = (
  root: Record<string, unknown>,
  path: ReadonlyArray<string>,
  value: unknown,
): void => {
  let target = root;
  for (const segment of path.slice(0, -1)) {
    const child = target[segment];
    const entries: Record<string, unknown> = isObject(child)
      ? Object.fromEntries(Object.entries(child))
      : {};
    target[segment] = entries;
    target = entries;
  }
  target[path.at(-1)!] = value;
};
export const assembleCatalogInput = (
  entry: CatalogEntry,
  values: FormValues,
): Record<string, unknown> => {
  const root: Record<string, unknown> = {};
  for (const constant of entry.constants.filter((item) => applies(item, values))) {
    assign(root, constant.path, constant.value);
  }
  for (const input of entry.inputs.filter((item) => applies(item, values))) {
    const value = values[bindingPath(input)] ?? input.default;
    if ((value === undefined || value === null || value === "") && !input.required) {
      continue;
    }
    assign(root, input.path, input.kind.kind === "INTEGER" ? Number(value) : (value ?? ""));
  }
  return root;
};

/** Bind a protocol member's semantic role to its core vocabulary. */
export const termVocabulary = (
  path: ReadonlyArray<string>,
):
  | "subject"
  | "act"
  | "correction"
  | "scope"
  | "principalKind"
  | "role"
  | "historyDetail"
  | "status"
  | null => {
  const key = path.join(".");
  switch (key) {
    case "kind":
      return "subject";
    case "act.kind":
      return "act";
    case "target.kind":
      return "principalKind";
    case "act.scope.kind":
      return "scope";
    case "act.role":
      return "role";
    case "detail":
      return "historyDetail";
    case "status":
      return "status";
    default:
      break;
  }
  return key.startsWith("act.groups.") && key.endsWith(".kind") ? "correction" : null;
};
