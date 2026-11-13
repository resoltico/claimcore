/** Client administration priority is explicit; supported people changes remain reviewed tasks. */
export const peoplePrimaryAction = (offered: ReadonlyArray<string>): string | undefined =>
  ["people.grant", "people.enable", "people.revoke", "people.disable"].find((kind) =>
    offered.includes(kind),
  );
