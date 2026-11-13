import { requestAddress, requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, TestSession } from "../../support/presentation-test-support";
import { RequesterContext, type ActiveRequester } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";
import { PeoplePanel } from "../../../src/views/PeoplePanel";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { navigate } from "../../../src/routing/navigation";
import {
  isRequest,
  type HumanReadDocument_people_grants,
  type HumanReview,
  type HumanReceiptDocument,
} from "@contracts/client";

const actorId = "00000000-0000-4000-8000-000000000001";
const principal = {
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "selected-person",
} as const;
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const owner: ActiveRequester = {
  ...activeRequester,
  context: {
    ...activeRequester.context,
    views: ["PEOPLE", "OPERATIONS"],
    tasks: [
      "people.enable",
      "people.list",
      "people.grants",
      "people.history",
    ],
  },
};
const person: HumanReadDocument_people_grants = {
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
              value: { kind: "TEXT", value: "Core selected person Ω" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "PERSON",
    actorId,
    principal,
    tasks: ["people.enable"],
    profileSeed: null,
    revocations: [],
    responsibleCases: null,
    nextCursor: null,
  },
  basis,
  disclosure: "CURRENT",
};
const receipt = (caption: string): HumanReceiptDocument => ({
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "outcome",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: caption },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "RECEIPT",
    recordedFields: [],
    target: { kind: "PEOPLE", actorId },
    caseLink: null,
    refreshCase: null,
  },
  basis,
  disclosure: "CURRENT",
});
const response = (outcome: unknown, operationId?: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      ...(operationId === undefined ? {} : { operationId }),
      outcome,
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
const Refresh = () => {
  const cache = useEndpointCache();
  return <button onClick={() => cache?.invalidate()}>Invalidate read artifacts</button>;
};
const workspace = (credential = "owner-a", requester = owner) => (
  <TestSession credential={credential}>
    <RequesterContext value={requester}>
      <Refresh />
      <PeoplePanel onLockChange={vi.fn()} />
    </RequesterContext>
  </TestSession>
);
afterEach(() => vi.unstubAllGlobals());
it("uses a bound person task and renders the current Core people outcome after acceptance and cache invalidation", async () => {
  const previews: string[] = [];
  const executions: string[] = [];
  const lookups: string[] = [];
  let currentOwner = owner;
  let finishLookup: (value: Response) => void = () => {
    throw new Error("No lookup pending");
  };
  let acceptedId = "";
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url, options) =>
    Promise.try(() => {
      const endpoint = requestAddress(url);
      if (endpoint.endsWith("/context")) {
        return response({ kind: "SUCCEEDED", document: currentOwner });
      }
      if (endpoint.endsWith("people.list")) {
        return response({
          kind: "SUCCEEDED",
          document: {
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
                        key: actorId,
                        values: [{ kind: "TEXT", value: "Core directory caption Δ" }],
                      },
                    ],
                  },
                },
              ],
            },
            context: { kind: "PEOPLE", actorIds: [actorId], nextCursor: null },
            basis,
            disclosure: "CURRENT",
          },
        });
      }
      if (endpoint.endsWith("people.grants")) {
        return response({ kind: "SUCCEEDED", document: person });
      }
      if (endpoint.endsWith("operation.get")) {
        const { operationId } = JSON.parse(requestText(options?.body)) as { operationId: string };
        lookups.push(operationId);
        return lookups.length === 1
          ? response(
              {
                kind: "ACCEPTED",
                byRequester: true,
                document: receipt("Core accepted people outcome Ψ"),
              },
              operationId,
            )
          : new Promise<Response>((resolve) => {
              finishLookup = resolve;
            });
      }
      const bytes = requestText(options?.body);
      const input: unknown = JSON.parse(bytes);
      if (!isRequest("operation.execute", input)) {
        throw new Error(`Unexpected request ${endpoint}`);
      }
      if (endpoint.endsWith("operation.preview")) {
        previews.push(bytes);
        const review: HumanReview = {
          presentation: { changes: [], unchanged: [] },
          document: {
            screen: { regions: [] },
            context: {
              kind: "REVIEW",
              task: "people.enable",
              target: { kind: "PEOPLE", actorId, scope: { kind: "INSTALLATION" } },
              inputs: [
                "input:operationId",
                "input:principal.kind",
                "input:principal.issuer",
                "input:principal.subject",
              ],
              consequence: "ROUTINE",
              confirmation: { kind: "OPERATION", operationId: input.operationId },
            },
            basis,
            disclosure: "CURRENT",
          },
        };
        return response({ kind: "REVIEWED", review }, input.operationId);
      }
      executions.push(bytes);
      acceptedId = input.operationId;
      return response(
        { kind: "ACCEPTED", document: receipt("Original acceptance facts must be re-observed") },
        input.operationId,
      );
    }),
  );
  vi.stubGlobal("fetch", fetch);
  navigate(`/people/${actorId}`, true);
  const view = render(workspace());
  await screen.findByRole("dialog", { name: "Core selected person Ω" });
  await userEvent.click(await screen.findByRole("button", { name: /enable.*account/iu }));
  await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull();
  });
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await screen.findByText("Core accepted people outcome Ψ");
  expect(previews).toHaveLength(1);
  expect(executions).toEqual(previews);
  expect(JSON.parse(executions[0]!)).toMatchObject({
    kind: "PEOPLE",
    target: principal,
    act: { kind: "people.enable" },
  });
  expect(lookups).toEqual([acceptedId]);
  expect(
    screen.queryByText("Original acceptance facts must be re-observed"),
  ).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Invalidate read artifacts" }));
  expect(screen.queryByText("Core accepted people outcome Ψ")).not.toBeInTheDocument();
  await waitFor(() => {
    expect(lookups).toHaveLength(2);
  });
  finishLookup(
    response(
      {
        kind: "ACCEPTED",
        byRequester: true,
        document: receipt("Core current people outcome Χ"),
      },
      acceptedId,
    ),
  );
  await screen.findByText("Core current people outcome Χ");
  expect(executions).toHaveLength(1);
  currentOwner = {
    ...owner,
    context: { ...owner.context, actorId: "00000000-0000-4000-8000-000000000099" },
  };
  view.rerender(workspace("owner-b", currentOwner));
  expect(screen.queryByText("Core current people outcome Χ")).not.toBeInTheDocument();
  expect(executions).toHaveLength(1);
});

it("binds a selected revocation to the Core case identity without editable role/scope shadows", async () => {
  const caseId = "00000000-0000-4000-8000-000000000002";
  const grant = {
    key: "chosen-role",
    role: "CASE_READER",
    scope: { kind: "CASE", caseId },
  } as const;
  const document: HumanReadDocument_people_grants = {
    ...person,
    context: { ...person.context, tasks: ["people.revoke"], revocations: [grant] },
    screen: {
      regions: [
        ...person.screen.regions,
        {
          role: "ACCESS",
          content: {
            kind: "TABLE",
            columns: [
              { key: "role", labelId: "field.role.label", prominence: "PRIMARY", isMoney: false },
            ],
            rows: [{ key: grant.key, values: [{ kind: "TEXT", value: "Core selected grant Φ" }] }],
          },
        },
      ],
    },
  };
  const previews: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        if (requestAddress(url).endsWith("/context")) {
          return response({
            kind: "SUCCEEDED",
            document: {
              ...owner,
              context: {
                ...owner.context,
                tasks: ["people.revoke", "people.list", "people.grants"],
              },
            },
          });
        }
        if (requestAddress(url).endsWith("people.list")) {
          return response({
            kind: "SUCCEEDED",
            document: {
              screen: { regions: [] },
              context: { kind: "PEOPLE", actorIds: [], nextCursor: null },
              basis,
              disclosure: "CURRENT",
            },
          });
        }
        if (requestAddress(url).endsWith("people.grants")) {
          return response({ kind: "SUCCEEDED", document });
        }
        if (requestAddress(url).endsWith("operation.preview")) {
          const input: unknown = JSON.parse(requestText(options?.body));
          if (!isRequest("operation.execute", input)) {
            throw new Error("Invalid revocation command");
          }
          previews.push(input);
          return response(
            {
              kind: "REJECTED",
              rejection: {
                kind: "REFUSED",
                advice: {
                  message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                  action: "ASK_FOR_ACCESS",
                },
              },
            },
            input.operationId,
          );
        }
        throw new Error(`Unexpected endpoint ${requestAddress(url)}`);
      }),
    ),
  );
  navigate(`/people/${actorId}`, true);
  render(workspace());
  const { within } = await import("@testing-library/react");
  const row = await screen.findByRole("row", { name: /Core selected grant Φ/u });
  await userEvent.click(await within(row).findByRole("button", { name: /revoke/iu }));
  expect(screen.queryByRole("combobox", { name: /role|scope/iu })).not.toBeInTheDocument();
  expect(screen.queryByRole("textbox", { name: /case reference/iu })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(previews).toHaveLength(1);
  });
  expect(previews[0]).toMatchObject({
    kind: "PEOPLE",
    target: principal,
    act: {
      kind: "people.revoke",
      role: "CASE_READER",
      scope: { kind: "CASE", target: { kind: "ID", caseId } },
    },
  });
});

it("acquires admission inputs and ordered role rows through the actual semantic form", async () => {
  const admitting: ActiveRequester = {
    ...owner,
    context: {
      ...owner.context,
      tasks: [
        "people.admit",
        "people.list",
        "people.grants",
        "people.requests",
        "people.requests.dismiss",
      ],
      peopleIssuerSeed: "https://issuer.example/",
    },
  };
  const previews: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((url, options) =>
      Promise.try(() => {
        const endpoint = requestAddress(url);
        if (endpoint.endsWith("/context")) {
          return response({ kind: "SUCCEEDED", document: admitting });
        }
        if (endpoint.endsWith("people.list")) {
          return response({
            kind: "SUCCEEDED",
            document: {
              screen: { regions: [] },
              context: { kind: "PEOPLE", actorIds: [], nextCursor: null },
              basis,
              disclosure: "CURRENT",
            },
          });
        }
        if (endpoint.endsWith("people.requests")) {
          return response({
            kind: "SUCCEEDED",
            document: {
              screen: { regions: [] },
              context: { kind: "ACCESS_REQUESTS", requests: [], nextCursor: null },
              basis,
              disclosure: "CURRENT",
            },
          });
        }
        if (endpoint.endsWith("operation.preview")) {
          const input: unknown = JSON.parse(requestText(options?.body));
          if (!isRequest("operation.execute", input)) {
            throw new Error("Invalid admission command");
          }
          previews.push(input);
          return response(
            {
              kind: "REJECTED",
              rejection: {
                kind: "REFUSED",
                advice: {
                  message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                  action: "ASK_FOR_ACCESS",
                },
              },
            },
            input.operationId,
          );
        }
        throw new Error(`Unexpected endpoint ${endpoint}`);
      }),
    ),
  );
  navigate("/people", true);
  render(workspace("owner-a", admitting));
  await userEvent.click(await screen.findByRole("button", { name: "Add person" }));
  expect(screen.getByRole("textbox", { name: "Sign-in provider" })).toHaveValue(
    "https://issuer.example/",
  );
  await userEvent.type(screen.getByRole("textbox", { name: "Sign-in ID" }), "new-principal");
  await userEvent.type(screen.getByRole("textbox", { name: "Name" }), "Core-authored person");
  await userEvent.click(screen.getByRole("radio", { name: "Case reader" }));
  await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(previews).toHaveLength(1);
  });
  expect(previews[0]).toMatchObject({
    kind: "PEOPLE",
    target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "new-principal" },
    act: {
      kind: "people.admit",
      profile: { displayName: "Core-authored person" },
      grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
    },
  });
});
