import { workBasis } from "./inputs";
import { expect } from "vitest";
import { fireEvent } from "@testing-library/react";
import { screen, waitFor } from "./presentation-test-support";
import { mockEndpoint } from "./endpoint-mocks";
import { authorityPreview } from "./authority-operation.fixtures";
import { installationOffers } from "./installation-advice.fixtures";
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
export const grantsFor = (actor = firstActor): ApiResult<ServiceResponse<"authority.grants">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: { kind: "SUCCEEDED", data: { basis: workBasis, actor, items: [], nextCursor: null } },
  },
});
export const setupActorTargets = (
  offered: Parameters<typeof installationOffers>[0] = [
    "REGISTER",
    "ADMIT",
    "GRANT",
    "REVOKE",
    "ENABLE",
    "DISABLE",
  ],
) => {
  const context = wireReply("context");
  if (context.outcome.kind !== "SUCCEEDED" || context.outcome.data.requester.kind !== "ACTIVE") {
    throw new Error("Context fixture absent");
  }
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...context,
      outcome: {
        kind: "SUCCEEDED",
        data: {
          requester: {
            ...context.outcome.data.requester,
            views: ["AUTHORITY"],
            offers: installationOffers(offered),
          },
        },
      },
    },
  });
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          basis: workBasis,
          items: [
            {
              ...firstActor,
              actor: {
                ...firstActor.actor,
                displayName: { kind: "NAMED", value: "stale-list-first" },
              },
              principal: { ...firstActor.principal, subject: "stale-list-first" },
            },
            secondActor,
          ],
          nextCursor: null,
        },
      },
    },
  });
  mockEndpoint("authority.grants").mockImplementation((_credential, input) =>
    Promise.resolve(
      grantsFor(input.actorId === firstActor.actor.actorId ? firstActor : secondActor),
    ),
  );
  return authorityPreview();
};
export const openActor = async (id = firstActor.actor.actorId) => {
  fireEvent.click(document.getElementById(`actor-grants-${id}`)!);
  await screen.findByRole("dialog");
};
export const authorDifferentTarget = async () => {
  fireEvent.click(await screen.findByRole("button", { name: "Add person" }));
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  fireEvent.change(document.querySelector('[name="act.profile.displayName"]')!, {
    target: { value: "Authored account" },
  });
  const role =
    document.querySelector<HTMLInputElement>('[name^="grants.row-"][value="CASE_READER"]') ??
    document.querySelector<HTMLInputElement>('[value="CASE_READER"]');
  if (role === null) {
    throw new Error("Admission role required");
  }
  fireEvent.click(role);
  fireEvent.change(document.querySelector('[name="target.subject"]')!, {
    target: { value: "authored-other" },
  });
};
export const actorRoute = () => {
  navigate(`/people/${firstActor.actor.actorId}`);
};
export const listRoute = () => {
  navigate("/people");
};
