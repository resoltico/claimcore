import { productMetadata } from "../protocol/productMetadata";

/** Presentation admission comes from the domain-produced catalog; server authorization remains independent. */
export const roleAdmitted = (role: string, principalKind: string): boolean =>
  productMetadata.catalog.roles.some(
    (definition) =>
      definition.role === role &&
      definition.admittedPrincipalKinds.some((kind) => kind === principalKind),
  );
