import { installationActions, requesterLinks } from "./human-requester.fixtures";
import { operationAffordance } from "./affordances.fixtures";
import { humanReadReply } from "./human-read-reply.fixtures";
import { workBasis } from "./inputs";
import { expect } from "vitest";
import { fireEvent } from "@testing-library/react";
import { screen, waitFor } from "./presentation-test-support";
import { mockEndpoint } from "./endpoint-mocks";
import { peoplePreview } from "./people-operation.fixtures";
import type { OperationTaskDefinition, HumanReadDocument_people_grants } from "@contracts/client";
import { taskForAct } from "../../src/protocol/taskCatalog";
import { wireReply } from "./reply.fixtures";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { navigate } from "../../src/routing/navigation";
export const firstActor = {
  actor: {
    actorId: "10000000-0000-4000-8000-000000000001",
    displayName: { kind: "NAMED" as const, value: "server-first" },
    principalKind: "HUMAN" as const,
    availability: "AVAILABLE" as const,
  },
  contact: null,
  state: "NO_ROLES" as const,
  roles: [],
  principal: { kind: "HUMAN" as const, issuer: "https://issuer.example/", subject: "server-first" },
};
export const secondActor = {
  ...firstActor,
  actor: {
    ...firstActor.actor,
    actorId: "10000000-0000-4000-8000-000000000002",
    displayName: { kind: "NAMED" as const, value: "server-second" },
  },
  principal: { ...firstActor.principal, subject: "server-second" },
};
export const firstPerson: HumanReadDocument_people_grants = {
  screen: {
    regions: [
      {
        role: "ACCOUNT",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "actor",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              value: { kind: "ACTOR", value: firstActor.actor },
            },
          ],
        },
      },
      {
        role: "DETAILS",
        titleId: "label.recordDetails",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "issuer",
              labelId: "field.principal.issuer.label",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "https://issuer.example/" },
            },
            {
              key: "subject",
              labelId: "field.principal.subject.label",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "server-first" },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "PERSON", actorId: firstActor.actor.actorId },
  actions: [
    operationAffordance(
      "people.grant",
      { kind: "PEOPLE", actorId: firstActor.actor.actorId },
      { "input:scope": "INSTALLATION" },
      undefined,
      {},
      true,
    ),
    operationAffordance(
      "people.revoke",
      { kind: "PEOPLE", actorId: firstActor.actor.actorId },
      { "input:scope": "INSTALLATION" },
    ),
    operationAffordance("people.enable", { kind: "PEOPLE", actorId: firstActor.actor.actorId }),
    operationAffordance("people.disable", { kind: "PEOPLE", actorId: firstActor.actor.actorId }),
    operationAffordance(
      "people.setProfile",
      { kind: "PEOPLE", actorId: firstActor.actor.actorId },
      { "input:profile.displayName": "server-first" },
    ),
    operationAffordance("people.eraseProfile", {
      kind: "PEOPLE",
      actorId: firstActor.actor.actorId,
    }),
  ],
  links: [],
  history: null,
  nextCursor: null,
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
export const secondPerson: HumanReadDocument_people_grants = {
  ...firstPerson,
  screen: {
    regions: [
      {
        role: "ACCOUNT",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "actor",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              value: { kind: "ACTOR", value: secondActor.actor },
            },
          ],
        },
      },
      {
        role: "DETAILS",
        titleId: "label.recordDetails",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "issuer",
              labelId: "field.principal.issuer.label",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "https://issuer.example/" },
            },
            {
              key: "subject",
              labelId: "field.principal.subject.label",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "server-second" },
            },
          ],
        },
      },
    ],
  },
  actions: [
    operationAffordance(
      "people.grant",
      { kind: "PEOPLE", actorId: secondActor.actor.actorId },
      { "input:scope": "INSTALLATION" },
      undefined,
      {},
      true,
    ),
    operationAffordance("people.enable", { kind: "PEOPLE", actorId: secondActor.actor.actorId }),
    operationAffordance("people.disable", { kind: "PEOPLE", actorId: secondActor.actor.actorId }),
    operationAffordance(
      "people.setProfile",
      { kind: "PEOPLE", actorId: secondActor.actor.actorId },
      { "input:profile.displayName": "server-second" },
    ),
    operationAffordance("people.eraseProfile", {
      kind: "PEOPLE",
      actorId: secondActor.actor.actorId,
    }),
  ],
  subject: { ...firstPerson.subject, actorId: secondActor.actor.actorId },
};
export const grantsFor = (document = firstPerson): ApiResult<ServiceResponse<"people.grants">> => ({
  kind: "outcome",
  status: 200,
  value: humanReadReply("people.grants", document),
});
export const setupActorTargets = (
  offered: ReadonlyArray<
    Extract<OperationTaskDefinition, { subject: "PEOPLE" }>["act"] | "case.open"
  > = [
    "people.register",
    "people.admit",
    "people.grant",
    "people.revoke",
    "people.enable",
    "people.disable",
  ],
) => {
  const context = wireReply("context");
  if (context.outcome.kind !== "SUCCEEDED") {
    throw new Error("Context fixture absent");
  }
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...context,
      outcome: {
        kind: "SUCCEEDED",
        document: {
          ...context.outcome.document,
          basis: { kind: "SNAPSHOT", observation: workBasis },
          actions: installationActions([
            ...offered.map((act) =>
              act === "case.open" ? ("case.open" as const) : taskForAct("PEOPLE", act).id,
            ),
            "people.list",
            "people.grants",
            "people.history",
            "people.requests",
            "people.requests.dismiss",
          ]),
          links: requesterLinks(
            ["PEOPLE"],
            [
              ...offered.map((act) =>
                act === "case.open" ? ("case.open" as const) : taskForAct("PEOPLE", act).id,
              ),
              "people.list",
              "people.grants",
              "people.history",
              "people.requests",
              "people.requests.dismiss",
            ],
          ),
        },
      },
    },
  });
  mockEndpoint("people.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("people.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "actor",
                  labelId: "field.profile.displayName.label",
                  prominence: "PRIMARY",
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: firstActor.actor.actorId,
                  link: {
                    endpoint: "people.grants",
                    input: {
                      actorId: {
                        ...firstActor.actor,
                        displayName: { kind: "NAMED", value: "stale-list-first" },
                      }.actorId,
                      limit: 20,
                    },
                  },
                  values: [
                    {
                      kind: "ACTOR",
                      value: {
                        ...firstActor.actor,
                        displayName: { kind: "NAMED", value: "stale-list-first" },
                      },
                    },
                  ],
                },
                {
                  key: secondActor.actor.actorId,
                  link: {
                    endpoint: "people.grants",
                    input: { actorId: secondActor.actor.actorId, limit: 20 },
                  },
                  values: [{ kind: "ACTOR", value: secondActor.actor }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  });
  mockEndpoint("people.grants").mockImplementation((_credential, input) => {
    const person = input.actorId === firstActor.actor.actorId ? firstPerson : secondPerson;
    return Promise.resolve(
      grantsFor(
        (() => {
          const selected = person.actions.filter((action) =>
            offered.some((task) => task === action.task),
          );
          const primary = ["people.grant", "people.enable", "people.revoke", "people.disable"].find(
            (task) => selected.some((action) => action.task === task),
          );
          return {
            ...person,
            actions: selected.flatMap((action) =>
              action.task === "people.requests.dismiss"
                ? []
                : [
                    operationAffordance(
                      action.task,
                      { kind: "PEOPLE", actorId: person.subject.actorId },
                      action.seeds,
                      action.editable,
                      action.choices,
                      action.task === primary,
                    ),
                  ],
            ),
          };
        })(),
      ),
    );
  });
  return peoplePreview();
};
export const openActor = async (id = firstActor.actor.actorId) => {
  fireEvent.click(document.getElementById(`actor-grants-${id}`)!);
  await screen.findByRole("dialog");
};
export const authorDifferentTarget = async () => {
  fireEvent.click(await screen.findByRole("button", { name: "Add person" }));
  await waitFor(() => {
    expect(document.querySelector('[name="input:principal.subject"]')).not.toBeNull();
  });
  fireEvent.change(document.querySelector('[name="input:profile.displayName"]')!, {
    target: { value: "Authored account" },
  });
  const role =
    document.querySelector<HTMLInputElement>('[name^="grants.row-"][value="CASE_READER"]') ??
    document.querySelector<HTMLInputElement>('[value="CASE_READER"]');
  if (role === null) {
    throw new Error("Admission role required");
  }
  fireEvent.click(role);
  fireEvent.change(document.querySelector('[name="input:principal.subject"]')!, {
    target: { value: "authored-other" },
  });
};
export const actorRoute = () => {
  navigate(`/people/${firstActor.actor.actorId}`);
};
export const listRoute = () => {
  navigate("/people");
};
