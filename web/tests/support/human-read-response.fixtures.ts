import { endpointResponse } from "./transport-response.fixtures";
import type {
  EndpointId,
  HumanDocument,
  HumanReadDocument_caseGet,
  Screen,
  TaskId,
  WorkDateBasis,
} from "@contracts/client";
import { isResponseFor } from "../../src/protocol/client";
import type { ServiceResponse } from "../../src/protocol/types";
import { taskForAct } from "../../src/protocol/taskCatalog";
import { currentCaseInput } from "./inputs";
import { fixtureView } from "./read-view.fixtures";
import { workViewsDocument } from "./view-counts.fixtures";

const snapshot = (basis: WorkDateBasis) => ({ kind: "SNAPSHOT", observation: basis }) as const;
const page = (
  screen: Screen,
  context: HumanDocument["context"],
  basis: WorkDateBasis,
  disclosure: HumanDocument["disclosure"] = "CURRENT",
): HumanDocument => ({ screen, context, basis: snapshot(basis), disclosure });

/** Construct interaction inputs from authored facts, never expected policy or encoded byte oracles.
 * DATA facts are validated without repair; person write permissions must be supplied explicitly.
 */
export const humanReadResponse = <E extends EndpointId>(
  endpoint: E,
  _kind: "SUCCEEDED",
  data: unknown,
): ServiceResponse<E> => {
  const facts: unknown = { representation: "DATA", outcome: { kind: "SUCCEEDED", data } };
  const screen = fixtureView(endpoint, "SUCCEEDED", data);
  let document: HumanDocument | null = null;
  if (
    endpoint === "session" &&
    isResponseFor("session", "HUMAN", { outcome: { kind: "SUCCEEDED", data, view: screen } })
  ) {
    const reply: unknown = { outcome: { kind: "SUCCEEDED", data, view: screen } };
    if (isResponseFor(endpoint, "HUMAN", reply)) return reply;
  }
  if (
    endpoint === "context" &&
    isResponseFor("context", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  ) {
    const requester = facts.outcome.data.requester;
    if (requester.kind === "ACTIVE") {
      const tasks: TaskId[] = requester.offers.map((offer) =>
        offer.kind === "OPEN" ? "CASE:OPEN" : taskForAct("AUTHORITY", offer.act).id,
      );
      if (requester.capabilities.includes("READ_AUTHORITY"))
        tasks.push("query:authorityList", "query:authorityGrants", "query:authorityHistory");
      if (requester.capabilities.includes("MANAGE_AUTHORITY"))
        tasks.push("query:authorityRequests", "query:authorityRequestsDismiss");
      document = page(
        screen,
        {
          kind: "REQUESTER",
          state: "ACTIVE",
          installationId: requester.installationId,
          actorId: requester.actorId,
          views: requester.views,
          tasks,
          authorityIssuerSeed:
            tasks.includes("AUTHORITY:REGISTER") || tasks.includes("AUTHORITY:ADMIT")
              ? requester.issuer
              : null,
        },
        {
          observedAt: requester.observedAt,
          businessDate: requester.businessDate,
          timeZone: requester.timeZone,
        },
      );
    } else {
      document = {
        screen,
        context: {
          kind: "REQUESTER",
          state: requester.kind,
          installationId: requester.kind === "NO_ROLES" ? requester.installationId : null,
          actorId: requester.kind === "NOT_REGISTERED" ? null : requester.self.actorId,
          views: [],
          tasks: [],
          authorityIssuerSeed: null,
        },
        basis: { kind: "UNAVAILABLE" },
        disclosure: "CURRENT",
      };
    }
  }
  if (
    endpoint === "case.get" &&
    isResponseFor("case.get", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  ) {
    const value = facts.outcome.data;
    if (value.state.kind !== "ERASED")
      document = currentCaseInput({ ...value, state: value.state });
    else {
      const context: HumanReadDocument_caseGet["context"] = {
        kind: "CASE",
        caseId: value.caseId,
        revision: value.revision,
        workRevision: value.work.revision,
        history: value.advice.history,
        tasks: [],
        editableInputs: [],
        correction: null,
        seeds: { registration: null, decision: null, work: null, paymentCurrency: null },
        notes: [],
        payments: [],
        nextActions: [],
      };
      document = page(screen, context, value.work.basis, "ERASED");
    }
  }
  if (
    endpoint === "case.list" &&
    isResponseFor("case.list", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  ) {
    const value = facts.outcome.data;
    document = page(
      screen,
      {
        kind: "CASES",
        nextCursor: value.nextCursor,
        tasks: value.offers.map(() => "CASE:OPEN"),
        exactCaseId: value.exactReference?.caseId ?? null,
        cases: value.items.map((item) => ({
          caseId: item.caseId,
          assignment: item.canAssign
            ? { revision: item.workRevision, responsibleSeed: item.responsible?.actorId ?? null }
            : null,
        })),
      },
      value.basis,
    );
  }
  if (
    endpoint === "case.summary" &&
    isResponseFor("case.summary", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = page(screen, { kind: "EMPTY" }, facts.outcome.data.basis);
  if (
    endpoint === "case.timeline" &&
    isResponseFor("case.timeline", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = page(
      screen,
      { kind: "PAGE", tasks: [], nextCursor: facts.outcome.data.nextCursor },
      facts.outcome.data.basis,
      facts.outcome.data.disclosure,
    );
  if (
    endpoint === "work.views" &&
    isResponseFor("work.views", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = workViewsDocument(facts.outcome.data);
  if (
    endpoint === "reminder.list" &&
    isResponseFor("reminder.list", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  ) {
    const value = facts.outcome.data;
    document = page(
      screen,
      {
        kind: "REMINDERS",
        nextCursor: value.nextCursor,
        reminders: value.items.map((item) => ({
          caseId: item.reminder.caseId,
          reminderId: item.reminder.reminderId,
          revision: item.reminder.revision,
          tasks: item.acts.map((act) => taskForAct("REMINDER", `REMINDER_${act}`).id),
          actionSeed: item.acts.includes("UPDATE") ? item.reminder.action : null,
          dueDateSeed: item.acts.includes("UPDATE") ? item.reminder.dueDate : null,
        })),
      },
      value.basis,
    );
  }
  if (
    endpoint === "authority.list" &&
    isResponseFor("authority.list", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = page(
      screen,
      {
        kind: "PEOPLE",
        nextCursor: facts.outcome.data.nextCursor,
        actorIds: facts.outcome.data.items.map((item) => item.actor.actorId),
      },
      facts.outcome.data.basis,
    );
  if (
    endpoint === "authority.grants" &&
    isResponseFor("authority.grants", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  ) {
    const value = facts.outcome.data;
    const tasks = value.offers.map((act) => taskForAct("AUTHORITY", act).id);
    document = page(
      screen,
      {
        kind: "PERSON",
        actorId: value.actor.actor.actorId,
        principal: value.actor.principal,
        nextCursor: value.nextCursor,
        tasks,
        profileSeed: value.profileSeed,
        revocations: tasks.includes("AUTHORITY:REVOKE")
          ? value.items.map((item, index) => ({
              key: String(index),
              role: item.role,
              scope:
                item.scope.kind === "CASE"
                  ? { kind: "CASE", caseId: item.scope.caseId }
                  : { kind: "INSTALLATION" },
            }))
          : [],
        responsibleCases:
          value.actor.responsibleCases === undefined
            ? null
            : {
                view: value.actor.responsibleCases.view,
                responsibleActor: value.actor.responsibleCases.responsibleActor,
                lifecycle: value.actor.responsibleCases.lifecycle,
                status: value.actor.responsibleCases.status,
              },
      },
      value.basis,
    );
  }
  if (
    endpoint === "authority.history" &&
    isResponseFor("authority.history", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = page(
      screen,
      {
        kind: "AUTHORITY_HISTORY",
        nextCursor: facts.outcome.data.nextCursor,
        entries: facts.outcome.data.entries.map((item) => ({
          key: `${item.position.sequence}:${item.position.effectIndex}`,
          caseId:
            item.act.kind === "GRANT" || item.act.kind === "REVOKE"
              ? item.act.scope.kind === "CASE"
                ? item.act.scope.caseId
                : null
              : null,
        })),
      },
      facts.outcome.data.basis,
    );
  if (
    endpoint === "authority.requests" &&
    isResponseFor("authority.requests", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = page(
      screen,
      {
        kind: "ACCESS_REQUESTS",
        nextCursor: facts.outcome.data.nextCursor,
        requests: facts.outcome.data.items.map((item, index) => ({
          key: String(index),
          principal: item.principal,
          profileSeed: {
            displayName: item.claims.name ?? item.claims.preferredUsername ?? "",
            contact: item.claims.email ?? null,
          },
        })),
      },
      facts.outcome.data.basis,
    );
  if (
    endpoint === "authority.requests.dismiss" &&
    isResponseFor("authority.requests.dismiss", "DATA", facts) &&
    facts.outcome.kind === "SUCCEEDED"
  )
    document = {
      screen,
      context: { kind: "EMPTY" },
      basis: { kind: "UNAVAILABLE" },
      disclosure: "NONE",
    };
  const reply: unknown = { representation: "HUMAN", outcome: { kind: "SUCCEEDED", document } };
  if (document === null || !isResponseFor(endpoint, "HUMAN", reply))
    throw new Error(`Authored ${endpoint} facts or controls do not form a current HUMAN reply.`);
  return reply;
};

/** Current HUMAN envelope at the actual mocked HTTP boundary. */
export const humanReadHttpResponse = <E extends EndpointId>(
  endpoint: E,
  kind: "SUCCEEDED",
  data: unknown,
): Response => endpointResponse(endpoint, humanReadResponse(endpoint, kind, data));
