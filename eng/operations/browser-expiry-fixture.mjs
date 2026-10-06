import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { chownSync, readFileSync } from "node:fs";
import { createServer } from "node:https";
import { join } from "node:path";
import { privateDirectory, privateFile } from "./local-files.mjs";

const root = "/configuration/installation";
const directory = join(root, "browser-expiry");
function issue() {
  assert.equal(process.getuid?.(), 0);
  assert.equal(
    JSON.parse(readFileSync(join(root, "installation.json"), "utf8")).scope,
    "SYNTHETIC_ONLY",
  );
  privateDirectory(directory, 1000, 0);
  privateFile(
    join(directory, "server.key"),
    readFileSync(join(root, "web", "server.key")),
    1000,
    0,
  );
  execFileSync(
    "openssl",
    [
      "x509",
      "-req",
      "-in",
      join(root, "web", "server.csr"),
      "-CA",
      join(root, "authority", "ca.pem"),
      "-CAkey",
      join(root, "authority", "ca.key"),
      "-CAcreateserial",
      "-days",
      "-1",
      "-sha256",
      "-extfile",
      join(root, "web", "server.ext"),
      "-out",
      join(directory, "server.pem"),
    ],
    { stdio: "ignore" },
  );
  chownSync(join(directory, "server.pem"), 1000, 0);
}
function serve() {
  assert.equal(process.getuid?.(), 1000);
  createServer(
    {
      key: readFileSync("/certificate/server.key"),
      cert: readFileSync("/certificate/server.pem"),
    },
    (_, response) => response.end("Synthetic expired-certificate negative control."),
  ).listen(5445, "0.0.0.0");
}
try {
  if (process.argv.length !== 3) {
    throw new Error("Invalid expiry qualification action.");
  }
  if (process.argv[2] === "issue") {
    issue();
  } else if (process.argv[2] === "serve") {
    serve();
  } else {
    throw new Error("Invalid expiry qualification action.");
  }
} catch {
  process.stderr.write("Expired-certificate fixture failed.\n");
  process.exitCode = 1;
}
