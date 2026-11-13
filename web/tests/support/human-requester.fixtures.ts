import type { ActiveRequester } from "../../src/hooks/useRequesterContext";

/** Authored HUMAN fixture: presentation and control handles, with no saved requester DTO. */
export const activeRequester: ActiveRequester = {
  screen: { regions: [] },
  context: {
    kind: "REQUESTER",
    state: "ACTIVE",
    installationId: "90000000-0000-4000-8000-000000000001",
    actorId: "10000000-0000-4000-8000-000000000001",
    views: ["CASES", "PEOPLE", "OPERATIONS"],
    tasks: ["case.open", "people.list", "people.grants", "people.history"],
    peopleIssuerSeed: null,
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-09-09T12:00:00.000000Z",
      businessDate: "2026-09-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
