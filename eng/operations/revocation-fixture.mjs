// Synthetic qualification only: cloned CA state never changes installation authority or case data.
import { execFileSync } from "node:child_process";
import { createHash, X509Certificate } from "node:crypto";
import { cpSync, lstatSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import { createAuthority } from "./local-certificates.mjs";

const root = "/configuration/installation";
const output = join(root, "revocation-qualification");
export const variants = [
  "valid",
  "malformed",
  "wrong-signature",
  "expired",
  "primary-revoked",
  "witness-revoked",
];
/** @param {string} cwd @param {string[]} args */
const openssl = (cwd, args) =>
  execFileSync("openssl", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
/** @param {string} variant */
function issue(variant) {
  const directory = join(output, variant);
  mkdirSync(directory, { mode: 0o700 });
  cpSync(join(root, "authority"), directory, { recursive: true });
  if (variant === "wrong-signature") {
    createAuthority(directory);
  }
  if (variant.endsWith("-revoked")) {
    const [leaf] = variant.split("-");
    openssl(directory, [
      "ca",
      "-config",
      "revocation.conf",
      "-revoke",
      join(root, leaf ?? "", "server.pem"),
    ]);
  }
  const bounds =
    variant === "expired"
      ? ["-crl_lastupdate", "20000101000000Z", "-crl_nextupdate", "20000102000000Z"]
      : [];
  openssl(directory, [
    "ca",
    "-config",
    "revocation.conf",
    "-gencrl",
    ...bounds,
    "-out",
    "ca.crl.pem",
  ]);
  openssl(directory, ["crl", "-in", "ca.crl.pem", "-outform", "DER", "-out", "ca.crl"]);
  if (variant === "malformed") {
    writeFileSync(join(directory, "ca.crl"), "invalid synthetic CRL", { mode: 0o600 });
  }
}
/** @param {Uint8Array} bytes */
function replacePublication(bytes) {
  const destination = join(root, "revocation", "ca.crl");
  const metadataBefore = lstatSync(destination);
  if (
    !metadataBefore.isFile() ||
    metadataBefore.isSymbolicLink() ||
    (metadataBefore.mode & 0o077) !== 0
  ) {
    throw new Error("Owned synthetic CRL is not a private regular file.");
  }
  // Replace only the fixture-owned publication bytes, preserving the existing owner and mode.
  writeFileSync(destination, bytes, { flag: "w" });
  const after = statSync(destination);
  if (
    after.uid !== metadataBefore.uid ||
    after.gid !== metadataBefore.gid ||
    after.mode !== metadataBefore.mode
  ) {
    throw new Error("Synthetic CRL ownership changed.");
  }
}
/** @param {string} directory @param {string} variant */
function crlFields(directory, variant) {
  const metadata =
    variant === "malformed"
      ? null
      : openssl(directory, [
          "crl",
          "-in",
          "ca.crl",
          "-inform",
          "DER",
          "-noout",
          "-nameopt",
          "RFC2253",
          "-issuer",
          "-lastupdate",
          "-nextupdate",
        ]);
  /** @param {string} prefix */
  const field = (prefix) =>
    metadata
      ?.split("\n")
      .find((line) => line.startsWith(prefix))
      ?.slice(prefix.length) ?? null;
  return {
    issuer: field("issuer="),
    thisUpdate: field("lastUpdate="),
    nextUpdate: field("nextUpdate="),
  };
}
/** @param {string} variant */
function publish(variant) {
  if (!variants.includes(variant)) {
    throw new Error("Unknown synthetic CRL variant.");
  }
  const directory = join(output, variant);
  const bytes = readFileSync(join(directory, "ca.crl"));
  replacePublication(bytes);
  return {
    variant,
    sha256: createHash("sha256").update(bytes).digest("hex"),
    ...crlFields(directory, variant),
    primarySerial: new X509Certificate(readFileSync(join(root, "primary", "server.pem")))
      .serialNumber,
    witnessSerial: new X509Certificate(readFileSync(join(root, "witness", "server.pem")))
      .serialNumber,
  };
}
function main() {
  const [mode, variant] = process.argv.slice(2);
  if (mode === "issue" && variant === undefined) {
    mkdirSync(output, { mode: 0o700 });
    for (const name of variants) {
      issue(name);
    }
  } else if (mode === "publish" && variant !== undefined) {
    process.stdout.write(`${JSON.stringify(publish(variant))}\n`);
  } else {
    throw new Error("Unknown revocation fixture action.");
  }
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch {
    process.stderr.write("Synthetic revocation fixture failed; private evidence retained.\n");
    process.exitCode = 1;
  }
}
