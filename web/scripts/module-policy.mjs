import { parseSync } from "oxc-parser";
import { posix } from "node:path";

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
    [target, `${target}.ts`, `${target}.tsx`, `${target}/index.ts`, `${target}/index.tsx`].find(
      (candidate) => files.has(candidate),
    ) ?? target
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
/** @param {Record<string,string>} sources @param {Record<string,string[]>} aliases */
export function moduleViolations(sources, aliases) {
  const files = new Set(Object.keys(sources));
  /** @type {string[]} */
  const problems = [];
  const graph = new Map();
  for (const [file, source] of Object.entries(sources)) {
    const targets = imports(file, source).map((name) => resolve(file, name, aliases, files));
    graph.set(
      file,
      targets.filter((target) => files.has(target)),
    );
    if (/^(src\/protocol\/|\.\.\/contracts\/)/u.test(file)) {
      problems.push(...platformViolations(file, source));
    }
    problems.push(...edgeViolations(file, targets));
  }
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
      inspect(target);
    }
    active.delete(file);
    done.add(file);
  };
  for (const file of files) {
    inspect(file);
  }
  return problems;
}
/** @param {string} file @param {string} source */
export function testDiscoveryViolations(file, source) {
  const { program, errors } = parseSync(file, source);
  if (errors.length) {
    return [`Cannot parse ${file}`];
  }
  const identities = new Set(["it", "test", "describe", "suite"]);
  walk(program, (node) => {
    if (
      node["type"] === "ImportSpecifier" &&
      ["it", "test", "describe", "suite"].includes(node["imported"].name)
    ) {
      identities.add(node["local"].name);
    }
  });
  /** @type {string[]} */
  /** @type {string[]} */
  const failures = [];
  walk(program, (node) => {
    if (
      node["type"] === "MemberExpression" &&
      identities.has(node["object"]?.name) &&
      ["only", "skip", "todo", "skipIf", "runIf"].includes(
        node["property"]?.name ?? node["property"]?.value,
      )
    ) {
      failures.push(`${file}: incomplete test declaration`);
    }
  });
  return failures;
}

/** @param {string} file @param {string} source @returns {string[]} */
function platformViolations(file, source) {
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

/** @param {Record<string,any>} node */
function ambientMember(node) {
  if (node["type"] !== "MemberExpression") {
    return false;
  }
  const object = node["object"]?.name;
  const property = node["property"]?.name ?? node["property"]?.value;
  return (
    ["Intl", "crypto", "process", "window", "navigator", "localStorage", "sessionStorage"].includes(
      object,
    ) ||
    (object === "Date" && property === "now") ||
    (object === "Math" && property === "random")
  );
}
/** @param {Record<string,any>} node */
function ambientCall(node) {
  if (!["CallExpression", "NewExpression"].includes(node["type"])) {
    return false;
  }
  return (
    ["fetch", "XMLHttpRequest", "WebSocket", "EventSource", "setTimeout", "setInterval"].includes(
      node["callee"]?.name,
    ) ||
    (node["type"] === "NewExpression" &&
      node["callee"]?.name === "Date" &&
      node["arguments"]?.length === 0)
  );
}
