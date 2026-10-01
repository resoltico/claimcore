import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";

/** @param {string} file @returns {Promise<import("./tooling-types.mjs").JsonRecord>} */
const readJson = async (file) => JSON.parse(await readFile(file, "utf8"));

/** @param {import("./tooling-types.mjs").PackageLock} lock @param {string} name */
export const packageVersion = (lock, name) => {
  const version = lock.packages?.[`node_modules/${name}`]?.version;
  if (typeof version !== "string") {
    throw new Error(`The locked ${name} version is missing.`);
  }
  return version;
};

/** @param {string} webDirectory @param {import("./tooling-types.mjs").PackageLock} lock @param {string} name */
const packageNotice = async (webDirectory, lock, name) => {
  const packageDirectory = join(webDirectory, "node_modules", name);
  const manifest = await readJson(join(packageDirectory, "package.json"));
  const version = packageVersion(lock, name);
  if (
    manifest["name"] !== name ||
    manifest["version"] !== version ||
    typeof manifest["license"] !== "string"
  ) {
    throw new Error(`Embedded package metadata is invalid for ${name}.`);
  }
  const files = await readdir(packageDirectory, { withFileTypes: true });
  const licenses = files
    .filter((entry) => entry.isFile() && /^licen[cs]e(?:\.(?:md|txt))?$/iu.test(entry.name))
    .map((entry) => entry.name)
    .sort();
  if (licenses.length !== 1) {
    throw new Error(`Embedded package ${name} needs one license file.`);
  }
  const license = (await readFile(join(packageDirectory, licenses[0] ?? ""), "utf8")).trim();
  const repository =
    typeof manifest["repository"] === "string"
      ? manifest["repository"]
      : manifest["repository"]?.url;
  if (license.length === 0 || typeof repository !== "string" || repository.length === 0) {
    throw new Error(`Embedded package attribution is incomplete for ${name}.`);
  }
  return `Package: ${name}@${version}\nDeclared license: ${manifest["license"]}\nUpstream: ${repository}\n\n${license}`;
};

/** @param {string} webDirectory @param {import("./tooling-types.mjs").PackageLock} lock @param {string[]} names */
export const validatorNotice = async (webDirectory, lock, names) => {
  const notices = await Promise.all(names.map((name) => packageNotice(webDirectory, lock, name)));
  const header =
    "ClaimCore Web v3 standalone-validator third-party notices\n\n" +
    "Generated from the exact locked packages whose code is embedded in the split Web-v3 validator modules.";
  const separator = `\n\n${"-".repeat(80)}\n\n`;
  return `${header}\n\n${notices.join(separator)}\n`;
};
