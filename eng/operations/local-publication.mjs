import { lstatSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { privateDirectory, privateFile } from "./local-files.mjs";

/** @param {string} directory */
export function emptyOperatorDirectory(directory) {
  const info = lstatSync(directory);
  if (!info.isDirectory() || info.isSymbolicLink() || readdirSync(directory).length !== 0) {
    throw new Error("Operator publication requires an empty physical directory.");
  }
}

/** @param {string} directory @param {number} uid @param {number} gid */
export function installationDirectory(directory, uid, gid) {
  const info = lstatSync(directory);
  if (
    !info.isDirectory() ||
    info.isSymbolicLink() ||
    info.uid !== 0 ||
    (info.mode & 0o022) !== 0 ||
    readdirSync(directory).length !== 0
  ) {
    throw new Error("Configuration creation requires an empty Linux volume owned by root.");
  }
  const root = join(directory, "installation");
  privateDirectory(root, uid, gid);
  return root;
}

/** @param {string} source @param {string} destination @param {number} uid @param {number} gid */
export function publishOperatorFiles(source, destination, uid, gid) {
  privateDirectory(join(destination, "web"), uid, gid);
  for (const file of ["ca.pem", "web.env"]) {
    privateFile(join(destination, "web", file), readFileSync(join(source, "web", file)), uid, gid);
  }
  privateFile(
    join(destination, "owner.password"),
    readFileSync(join(source, "administration", "owner.password")),
    uid,
    gid,
  );
  privateFile(
    join(destination, "installation.json"),
    readFileSync(join(source, "installation.json")),
    uid,
    gid,
  );
}
