/** Ephemeral requester identity. Credentials remain separate transport secrets. */
export type OwnerScope = Readonly<{
  serviceOrigin: string;
  installationId: string;
  actorId: string;
  sessionEpoch: number;
}>;
export type PrincipalScope = Pick<OwnerScope, "serviceOrigin" | "installationId" | "actorId">;
export const ownerScope = (
  principal: PrincipalScope | undefined,
  sessionEpoch: number,
): OwnerScope | null => (principal === undefined ? null : { ...principal, sessionEpoch });
export const sameOwnerScope = (left: OwnerScope | null, right: OwnerScope | null): boolean =>
  left === right ||
  (left !== null &&
    right !== null &&
    left.serviceOrigin === right.serviceOrigin &&
    left.installationId === right.installationId &&
    left.actorId === right.actorId &&
    left.sessionEpoch === right.sessionEpoch);
