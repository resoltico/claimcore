import { humanReadReply } from "./human-read-reply.fixtures";
import { workBasis } from "./inputs";
import { expect } from "vitest";
import { fireEvent } from "@testing-library/react";
import { screen, waitFor } from "./presentation-test-support";
import { mockEndpoint } from "./endpoint-mocks";
import { authorityPreview } from "./authority-operation.fixtures";
import type { AuthorityAct, HumanReadDocument_authorityGrants } from "@contracts/client";
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
export const firstPerson: HumanReadDocument_authorityGrants = {
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
  context: {
    kind: "PERSON",
    actorId: firstActor.actor.actorId,
    principal: firstActor.principal,
    tasks: [
      "AUTHORITY:GRANT",
      "AUTHORITY:REVOKE",
      "AUTHORITY:ENABLE",
      "AUTHORITY:DISABLE",
      "AUTHORITY:SET_PROFILE",
      "AUTHORITY:ERASE_PROFILE",
    ],
    profileSeed: { displayName: "server-first", contact: null },
    revocations: [],
    responsibleCases: null,
    nextCursor: null,
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
export const secondPerson: HumanReadDocument_authorityGrants = {
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
  context: {
    ...firstPerson.context,
    actorId: secondActor.actor.actorId,
    principal: secondActor.principal,
    profileSeed: { displayName: "server-second", contact: null },
  },
};
export const grantsFor = (
  document = firstPerson,
): ApiResult<ServiceResponse<"authority.grants">> => ({
  kind: "outcome",
  status: 200,
  value: humanReadReply("authority.grants", document),
});
export const setupActorTargets = (
  offered: ReadonlyArray<AuthorityAct["kind"] | "OPEN"> = [
    "REGISTER",
    "ADMIT",
    "GRANT",
    "REVOKE",
    "ENABLE",
    "DISABLE",
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
          context: {
            ...context.outcome.document.context,
            views: ["AUTHORITY"],
            tasks: [
              ...offered.map((act) =>
                act === "OPEN" ? ("CASE:OPEN" as const) : taskForAct("AUTHORITY", act).id,
              ),
              "query:authorityList",
              "query:authorityGrants",
              "query:authorityHistory",
              "query:authorityRequests",
              "query:authorityRequestsDismiss",
            ],
          },
        },
      },
    },
  });
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("authority.list", {
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
                  values: [{ kind: "ACTOR", value: secondActor.actor }],
                },
              ],
            },
          },
        ],
      },
      context: {
        kind: "PEOPLE",
        actorIds: [firstActor.actor.actorId, secondActor.actor.actorId],
        nextCursor: null,
      },
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    }),
  });
  mockEndpoint("authority.grants").mockImplementation((_credential, input) => {
    const person = input.actorId === firstActor.actor.actorId ? firstPerson : secondPerson;
    return Promise.resolve(
      grantsFor({
        ...person,
        context: {
          ...person.context,
          tasks: offered
            .filter(
              (act): act is AuthorityAct["kind"] =>
                act !== "OPEN" && act !== "REGISTER" && act !== "ADMIT",
            )
            .map((act) => taskForAct("AUTHORITY", act).id),
          profileSeed: offered.includes("SET_PROFILE") ? person.context.profileSeed : null,
        },
      }),
    );
  });
  return authorityPreview();
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
