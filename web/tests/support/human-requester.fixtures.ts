import type { Affordance, NamedLink } from "@contracts/client";
import { operationAffordance } from "./affordances.fixtures";
import type { ActiveRequester } from "../../src/hooks/useRequesterContext";

/** Authored HUMAN fixture: presentation and control handles, with no saved requester DTO. */
export const activeRequester: ActiveRequester = {
  screen: { regions: [] },
  subject: {
    kind: "REQUESTER",
    state: "ACTIVE",
    installationId: "90000000-0000-4000-8000-000000000001",
    actorId: "10000000-0000-4000-8000-000000000001",
  },
  actions: [
    operationAffordance("case.open", {
      kind: "CASE",
      target: { kind: "REFERENCE", caseReference: "" },
      expectedRevision: 0,
    }),
  ],
  links: [
    {
      rel: "VIEW",
      labelId: "view.CASES.label",
      link: { endpoint: "case.list", input: { limit: 20 } },
    },
    {
      rel: "VIEW",
      labelId: "view.PEOPLE.label",
      link: { endpoint: "people.list", input: { limit: 20 } },
    },
    {
      rel: "VIEW",
      labelId: "view.OPERATIONS.label",
      link: { endpoint: "operation.get", input: {} },
    },
  ],
  history: null,
  nextCursor: null,
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
/** Creation controls have installation scope; selected people controls belong to their person document. */
export const installationActions = (tasks: ReadonlyArray<string>, issuer: string | null = null) =>
  tasks.flatMap<Affordance>((task) => {
    if (task === "case.open") {
      return [
        operationAffordance(task, {
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: "" },
          expectedRevision: 0,
        }),
      ];
    }
    if (task !== "people.register" && task !== "people.admit") {
      return [];
    }
    return [
      operationAffordance(
        task,
        { kind: "PEOPLE" },
        {
          "input:principal.kind": "HUMAN",
          ...(task === "people.admit"
            ? { "input:grants": [{ "input:scope": "INSTALLATION" }] }
            : {}),
          ...(issuer === null ? {} : { "input:principal.issuer": issuer }),
        },
      ),
    ];
  });
/** Callers author the permitted destinations; the builder supplies only their ready query inputs. */
export const requesterLinks = (
  views: ReadonlyArray<string>,
  tasks: ReadonlyArray<string> = [],
): ReadonlyArray<NamedLink> => [
  ...activeRequester.links.filter((link) =>
    views.some((view) => link.labelId === `view.${view}.label`),
  ),
  ...(views.includes("CASES")
    ? [
        {
          rel: "VIEW" as const,
          labelId: "label.reminders",
          link: {
            endpoint: "reminder.list" as const,
            input: { limit: 20, view: "PENDING" as const },
          },
        },
      ]
    : []),
  ...(tasks.includes("people.history")
    ? [
        {
          rel: "VIEW" as const,
          labelId: "endpoint.people.history.label",
          link: { endpoint: "people.history" as const, input: { limit: 20 } },
        },
      ]
    : []),
  ...(tasks.includes("people.requests")
    ? [
        {
          rel: "VIEW" as const,
          labelId: "endpoint.people.requests.label",
          link: { endpoint: "people.requests" as const, input: { limit: 20 } },
        },
      ]
    : []),
];
