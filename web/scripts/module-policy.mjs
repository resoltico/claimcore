import { parseSync } from "oxc-parser";
import { posix, join, relative } from "node:path";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

/** @param {unknown} value @param {(node:Record<string,any>)=>void} visit */
function walk(value, visit) {
  if (Array.isArray(value)) {
    for (const item of value) {
      walk(item, visit);
    }
    return;
  }
  if (value === null || typeof value !== "object") {
    return;
  }
  visit(value);
  for (const child of Object.values(value)) {
    walk(child, visit);
  }
}
/** @param {string} file @param {string} source */
function imports(file, source) {
  if (file.endsWith(".json")) {
    JSON.parse(source);
    return [];
  }
  const { program, errors } = parseSync(file, source);
  if (errors.length) {
    throw new Error(`Cannot inspect ${file}`);
  }
  /** @type {string[]} */
  const names = [];
  walk(program, (node) => {
    if (
      [
        "ImportDeclaration",
        "ExportNamedDeclaration",
        "ExportAllDeclaration",
        "ImportExpression",
      ].includes(node["type"]) &&
      node["source"]
    ) {
      if (typeof node["source"].value !== "string") {
        throw new Error(`Unresolvable module input in ${file}`);
      }
      names.push(node["source"].value);
    }
  });
  return names;
}
/** @param {string} file @param {string} name @param {Record<string,string[]>} aliases @param {Set<string>} files */
function resolve(file, name, aliases, files) {
  let target = name.startsWith(".") ? posix.join(posix.dirname(file), name) : name;
  for (const [pattern, paths] of Object.entries(aliases)) {
    const prefix = pattern.replace(/\*$/u, "");
    if (name.startsWith(prefix)) {
      target = posix.normalize((paths[0] ?? "").replace(/\*$/u, "") + name.slice(prefix.length));
    }
  }
  target = posix.relative("/workspace/web", posix.resolve("/workspace/web", target));
  return (
    [
      target,
      ...["ts", "tsx", "js", "mjs"].flatMap((extension) => [
        `${target}.${extension}`,
        `${target}/index.${extension}`,
      ]),
    ].find((candidate) => files.has(candidate)) ?? target
  );
}
/** @param {string} file @param {string[]} targets @returns {string[]} */
function edgeViolations(file, targets) {
  /** @type {string[]} */
  const problems = [];
  for (const target of targets) {
    if (target === "src/api/transport.ts" && file !== "src/api/service.ts") {
      problems.push(`${file} bypasses service facade`);
    }
    if (
      /^src\/(api|hooks|interaction|protocol)\//u.test(file) &&
      /^src\/(views|components|presentation)\//u.test(target)
    ) {
      problems.push(`${file} imports upper layer ${target}`);
    }
    if (
      /^(src\/protocol\/|\.\.\/contracts\/)/u.test(file) &&
      /^(react|node:|src\/views\/|src\/presentation\/|src\/api\/(service|transport|responseBytes))/u.test(
        target,
      )
    ) {
      problems.push(`${file} imports platform module ${target}`);
    }
  }
  return problems;
}
/** @param {Map<string,string[]>} graph @returns {string[]} */
function cycleViolations(graph) {
  /** @type {string[]} */
  const problems = [];
  const active = new Set();
  const done = new Set();
  /** @param {string} file */
  const inspect = (file) => {
    if (active.has(file)) {
      problems.push(`Module cycle at ${file}`);
      return;
    }
    if (done.has(file)) {
      return;
    }
    active.add(file);
    for (const target of graph.get(file) ?? []) {
      if (graph.has(target)) {
        inspect(target);
      }
    }
    active.delete(file);
    done.add(file);
  };
  for (const file of graph.keys()) {
    inspect(file);
  }
  return problems;
}
/** @param {Record<string,string>} sources @param {Map<string,string[]>} graph */
function purityViolations(sources, graph) {
  /** @type {string[]} */
  const failures = [];
  const visited = new Set();
  /** @param {string} file */
  const inspect = (file) => {
    if (visited.has(file)) {
      return;
    }
    visited.add(file);
    failures.push(...platformViolations(file, sources[file] ?? ""));
    for (const target of graph.get(file) ?? []) {
      if (graph.has(target)) {
        failures.push(...edgeViolations("src/protocol/closure.ts", [target]));
        inspect(target);
      } else {
        failures.push(`${file} imports unavailable or external protocol dependency ${target}`);
      }
    }
  };
  for (const file of graph.keys()) {
    if (/^(src\/protocol\/|\.\.\/contracts\/)/u.test(file)) {
      inspect(file);
    }
  }
  return failures;
}
/** @param {Record<string,string>} sources @param {Record<string,string[]>} aliases */
export function moduleViolations(sources, aliases) {
  const files = new Set(Object.keys(sources));
  /** @type {string[]} */
  const problems = [];
  const graph = new Map();
  for (const [file, source] of Object.entries(sources)) {
    const targets = imports(file, source).map((name) => resolve(file, name, aliases, files));
    graph.set(file, targets);
    problems.push(...edgeViolations(file, targets));
  }
  return [...problems, ...cycleViolations(graph), ...purityViolations(sources, graph)];
}
/** @param {string} file @param {string} source @returns {string[]} */
function platformViolations(file, source) {
  if (file.endsWith(".json")) {
    return [];
  }
  const { program } = parseSync(file, source);
  /** @type {string[]} */
  const failures = [];
  walk(program, (node) => {
    if (ambientMember(node) || ambientCall(node)) {
      failures.push(`${file} uses platform state or I/O`);
    }
  });
  return failures;
}

/** @param {Record<string,any>|undefined} node @returns {string[]} */
function memberPath(node) {
  if (node?.["type"] === "Identifier") {
    return [node["name"]];
  }
  if (node?.["type"] !== "MemberExpression") {
    return [];
  }
  return [...memberPath(node["object"]), node["property"]?.name ?? node["property"]?.value];
}
/** @param {Record<string,any>} node */
function ambientMember(node) {
  if (node["type"] !== "MemberExpression") {
    return false;
  }
  const path = memberPath(node);
  if (path[0] === "globalThis") {
    path.shift();
  }
  const [object = "", property] = path;
  return (
    ["Intl", "crypto", "process", "window", "navigator", "localStorage", "sessionStorage"].includes(
      object,
    ) ||
    (["Date", "performance"].includes(object) && property === "now") ||
    (object === "Math" && property === "random")
  );
}
/** @param {Record<string,any>} node */
function ambientCall(node) {
  if (!["CallExpression", "NewExpression"].includes(node["type"])) {
    return false;
  }
  const path = memberPath(node["callee"]);
  if (path[0] === "globalThis") {
    path.shift();
  }
  return (
    [
      "fetch",
      "XMLHttpRequest",
      "WebSocket",
      "EventSource",
      "setTimeout",
      "setInterval",
      "require",
    ].includes(path[0] ?? "") ||
    (path[0] === "Date" && node["arguments"]?.length === 0)
  );
}

/** @param {string} directory @returns {string[]} */
function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((item) => {
    const file = join(directory, item.name);
    if (item.isDirectory()) {
      return sourceFiles(file);
    }
    return /\.(ts|tsx|js|mjs|json)$/u.test(item.name) ? [file] : [];
  });
}

function checkModules() {
  const root = fileURLToPath(new URL("..", import.meta.url));
  const sources = Object.fromEntries(
    ["src", "../contracts"]
      .flatMap((directory) => sourceFiles(join(root, directory)))
      .map((file) => [relative(root, file).split("\\").join("/"), readFileSync(file, "utf8")]),
  );
  const config = JSON.parse(readFileSync(join(root, "tsconfig.base.json"), "utf8"));
  const failures = moduleViolations(sources, config.compilerOptions.paths);
  if (failures.length) {
    throw new Error(failures.join("\n"));
  }
}

if (import.meta.main) {
  checkModules();
}
