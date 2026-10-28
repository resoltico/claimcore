import inventory from "../../../tests/fixtures/inventory.json";

// The JSON fixture is hand-authored, independent of generated contracts.
export { inventory };
export const businessFieldCount =
  Object.values(inventory.groups).flat().length + inventory.specialFields.length;
