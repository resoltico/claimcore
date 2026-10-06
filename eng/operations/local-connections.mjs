import { randomBytes } from "node:crypto";
import { join } from "node:path";
import { privateFile } from "./local-files.mjs";

const password = () => randomBytes(32).toString("hex");

/** @param {string} host @param {string} database @param {string} user @param {string} secret @param {string} ca */
function connection(host, database, user, secret, ca) {
  return `Host=${host};Port=5432;Database=${database};Username=${user};Password=${secret};SSL Mode=VerifyFull;Root Certificate=${ca}\n`;
}

/** @param {string} root @param {number} uid @param {number} gid @param {{owner: string, witnessOwner: string, auditor: string}} credentials */
function ownerConnections(root, uid, gid, { owner, witnessOwner, auditor }) {
  /** @type {[string, string, string, string, string][]} */
  const owners = [
    ["primary-owner.connection", "primary", "claimcore", "claimcore_primary_owner", owner],
    [
      "witness-owner.connection",
      "witness",
      "claimcore_witness",
      "claimcore_witness_owner",
      witnessOwner,
    ],
    [
      "witness-auditor.connection",
      "witness",
      "claimcore_witness",
      "claimcore_witness_auditor",
      auditor,
    ],
  ];
  for (const [name, host, database, user, value] of owners) {
    privateFile(
      join(root, "administration", name),
      connection(host, database, user, value, "/etc/claimcore/administration/ca.pem"),
      uid,
      gid,
    );
  }
}

/** @param {string} root @param {number} uid @param {number} gid */
export function connectionFiles(root, uid, gid) {
  const owner = password();
  const app = password();
  const witnessOwner = password();
  const writer = password();
  const auditor = password();
  /** @type {[string, string, string][]} */
  const passwords = [
    ["primary", "owner.password", owner],
    ["primary", "app.password", app],
    ["witness", "owner.password", witnessOwner],
    ["witness", "writer.password", writer],
    ["witness", "auditor.password", auditor],
  ];
  for (const [folder, name, value] of passwords) {
    privateFile(join(root, folder, name), `${value}\n`, 999, 999);
  }
  ownerConnections(root, uid, gid, { owner, witnessOwner, auditor });
  privateFile(
    join(root, "web", "primary.connection"),
    connection("primary", "claimcore", "claimcore_app", app, "/etc/claimcore/ca.pem"),
    uid,
    gid,
  );
  privateFile(
    join(root, "web", "witness.connection"),
    connection(
      "witness",
      "claimcore_witness",
      "claimcore_witness_writer",
      writer,
      "/etc/claimcore/ca.pem",
    ),
    uid,
    gid,
  );
}
