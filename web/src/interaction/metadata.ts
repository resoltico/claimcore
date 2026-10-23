import { productMetadata } from "../protocol/productMetadata";
export type CommandKind = (typeof productMetadata.descriptors.commands)[number]["kind"];
export const isCaseAct = (value: string): value is CommandKind =>
  productMetadata.descriptors.commands.some((command) => command.kind === value);
