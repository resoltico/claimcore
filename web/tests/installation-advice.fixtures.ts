import type { InstallationOffer } from "@contracts/client";
type Act = "REGISTER" | "GRANT" | "REVOKE" | "ENABLE" | "DISABLE";
export const installationOffers = (
  acts: ReadonlyArray<Act | "OPEN">,
): ReadonlyArray<InstallationOffer> =>
  acts.map((act) => (act === "OPEN" ? { kind: "OPEN" } : { kind: "AUTHORITY_ACT", act }));
