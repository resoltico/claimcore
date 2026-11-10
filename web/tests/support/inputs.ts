import type {
  RequesterState_ACTIVE,
  ActorRef,
  Principal,
  Registration,
  CaseFields,
  WorkDateBasis,
} from "@contracts/client";
export const actorId = "10000000-0000-4000-8000-000000000001";
export const workBasis: WorkDateBasis = {
  observedAt: "2026-09-01T12:00:00.000000Z",
  businessDate: "2026-09-01",
  timeZone: "Etc/UTC",
};
const initialMaintenance = { active: false } as const;
export const actorRefInput = (value: Partial<ActorRef> = {}): ActorRef => ({
  actorId,
  displayName: { kind: "NAMED", value: "Named account" },
  principalKind: "HUMAN",
  availability: "AVAILABLE",
  ...value,
});

type RequesterState_ACTIVESeed = Omit<
  RequesterState_ACTIVE,
  | "hasScopedCases"
  | "actorId"
  | "installationId"
  | "maintenance"
  | "self"
  | "kind"
  | "observedAt"
  | "issuer"
  | "roles"
> &
  Partial<
    Pick<
      RequesterState_ACTIVE,
      | "hasScopedCases"
      | "actorId"
      | "installationId"
      | "maintenance"
      | "self"
      | "kind"
      | "observedAt"
      | "issuer"
      | "roles"
    >
  >;
export const actorContextInput = <T extends RequesterState_ACTIVESeed>(value: T) => ({
  ...value,
  observedAt: value.observedAt ?? `${value.businessDate}T12:00:00.000000Z`,
  issuer: value.issuer ?? "https://issuer.example/",
  roles: value.roles ?? [],
  kind: "ACTIVE" as const,
  self: value.self ?? actorRefInput(),
  maintenance: value.maintenance ?? initialMaintenance,
  hasScopedCases: value.hasScopedCases ?? false,
  actorId: value.actorId ?? actorId,
  installationId: value.installationId ?? "90000000-0000-4000-8000-000000000001",
});
// Build inputs only. Expected outputs and oracle values must stay literal.
export const registration = (
  changes: Partial<Registration> = {},
): Registration & CaseFields["registration"] => ({
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "LV",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  incidentDescription: null,
  ...changes,
});
export const principal = (changes: Partial<Principal> = {}): Principal => ({
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "synthetic-owner",
  ...changes,
});
