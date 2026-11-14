import { productMetadata } from "../protocol/productMetadata";

/** Presentation admission comes from the domain-produced catalog; server authorization remains independent. */
export const roleAdmitted = (role: string, principalKind: string, scope?: string): boolean =>
  productMetadata.catalog.roles.some(
    (definition) =>
      definition.role === role &&
      definition.admittedPrincipalKinds.some((kind) => kind === principalKind) &&
      (scope === undefined || definition.admittedScopes.some((kind) => kind === scope)),
  );

/** Scope guidance also comes from the Core catalog; a bound person's role choices are already narrowed. */
export const roleScopeAdmitted = (role: string, scope: string): boolean =>
  productMetadata.catalog.roles.some(
    (definition) =>
      definition.role === role && definition.admittedScopes.some((value) => value === scope),
  );
