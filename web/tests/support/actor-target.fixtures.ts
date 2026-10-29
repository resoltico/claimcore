import { expect } from "vitest";
import { fireEvent } from "@testing-library/react";
import { screen, waitFor } from "./presentation-test-support";
import { mockEndpoint } from "./endpoint-mocks";
import { authorityPreview } from "./authority-operation.fixtures";
import { installationOffers } from "./installation-advice.fixtures";
import { wireReply } from "./contract-corpus.fixtures";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { navigate } from "../../src/routing/navigation";
export const firstActor = {
  actorId: "10000000-0000-4000-8000-000000000001",
  enabled: true,
  principal: { kind: "HUMAN" as const, issuer: "https://issuer.example/", subject: "server-first" },
};
export const secondActor = {
  ...firstActor,
  actorId: "10000000-0000-4000-8000-000000000002",
  principal: { ...firstActor.principal, subject: "server-second" },
};
export const grantsFor = (actor = firstActor): ApiResult<ServiceResponse<"authority.grants">> => ({
  kind: "outcome",
  status: 200,
  value: { outcome: { kind: "SUCCEEDED", data: { actor, items: [], nextCursor: null } } },
});
export const setupActorTargets = (
  offered: Parameters<typeof installationOffers>[0] = [
    "REGISTER",
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
        data: {
          ...context.outcome.data,
          views: ["AUTHORITY"],
          offers: installationOffers(offered),
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
          items: [
            { ...firstActor, principal: { ...firstActor.principal, subject: "stale-list-first" } },
            secondActor,
          ],
          nextCursor: null,
        },
      },
    },
  });
  mockEndpoint("authority.grants").mockImplementation((_credential, input) =>
    Promise.resolve(grantsFor(input.actorId === firstActor.actorId ? firstActor : secondActor)),
  );
  return authorityPreview();
};
export const openActor = async (id = firstActor.actorId) => {
  fireEvent.click(document.getElementById(`actor-grants-${id}`)!);
  await screen.findByRole("heading", { name: "Actor grants", level: 1 });
};
export const authorDifferentTarget = async () => {
  fireEvent.change(await screen.findByRole("combobox", { name: "Authority action" }), {
    target: { value: "GRANT" },
  });
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  fireEvent.change(document.querySelector('[name="target.issuer"]')!, {
    target: { value: "https://issuer.example/" },
  });
  fireEvent.change(document.querySelector('[name="target.subject"]')!, {
    target: { value: "authored-other" },
  });
};
export const actorRoute = () => {
  navigate(`/authority/${firstActor.actorId}`);
};
export const listRoute = () => {
  navigate("/authority");
};
