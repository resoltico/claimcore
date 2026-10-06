import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { privateFile } from "./local-files.mjs";

/** @param {string} authority @param {string} publication @param {number} uid @param {number} gid */
export function createRevocation(authority, publication, uid, gid) {
  privateFile(join(authority, "index"), "", uid, gid);
  privateFile(join(authority, "crl-number"), "01\n", uid, gid);
  privateFile(
    join(authority, "revocation.conf"),
    "[ca]\ndefault_ca=issuer\n[issuer]\ndatabase=index\ncertificate=ca.pem\nprivate_key=ca.key\ndefault_md=sha256\ndefault_crl_days=365\ncrlnumber=crl-number\n",
    uid,
    gid,
  );
  execFileSync("openssl", ["ca", "-config", "revocation.conf", "-gencrl", "-out", "ca.crl.pem"], {
    cwd: authority,
    stdio: "ignore",
  });
  execFileSync("openssl", ["crl", "-in", "ca.crl.pem", "-outform", "DER", "-out", "ca.crl"], {
    cwd: authority,
    stdio: "ignore",
  });
  privateFile(join(publication, "ca.crl"), readFileSync(join(authority, "ca.crl")), uid, gid);
}
