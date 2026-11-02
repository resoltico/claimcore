/** Client administration priority is explicit; supported authority changes remain reviewed tasks. */
export const authorityPrimaryAction = (offered: ReadonlyArray<string>): string | undefined =>
  ["GRANT", "ENABLE", "REVOKE", "DISABLE"].find((kind) => offered.includes(kind));
