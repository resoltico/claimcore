import { type RequestByEndpoint, type HumanReview } from "@contracts/client";
import { operationRequest, requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../../support/presentation-test-support";
import { useOperation, type OperationModel } from "../../../../src/hooks/operation/useOperation";
const first = {
  kind: "SERVICE",
  issuer: "https://issuer.example/",
  subject: "service-a",
} as const;
const second = {
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "person-b",
} as const;
const fixture = () => {
  const bodies: string[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) =>
    Promise.try(() => {
      const bytes = requestText(options?.body);
      const input: unknown = JSON.parse(bytes);
      bodies.push(bytes);
      const operationExecuteRequest = input as RequestByEndpoint["operation.execute"];
      const review: HumanReview = {
        presentation: {
          changes: [],
          unchanged: [],
        },
        document: {
          screen: {
            regions: [],
          },
          context: {
            kind: "REVIEW",
            task: "people.enable",
            target: {
              kind: "PEOPLE",
              actorId: null,
              scope: {
                kind: "INSTALLATION",
              },
            },
            inputs: [
              "input:operationId",
              "input:principal.kind",
              "input:principal.issuer",
              "input:principal.subject",
            ],
            consequence: "ROUTINE",
            confirmation: {
              kind: "OPERATION",
              operationId: operationExecuteRequest.operationId,
            },
          },
          basis: {
            kind: "SNAPSHOT",
            observation: {
              observedAt: "2026-11-09T12:00:00.000000Z",
              businessDate: "2026-11-09",
              timeZone: "Etc/UTC",
            },
          },
          disclosure: "CURRENT",
        },
      };
      return new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: operationExecuteRequest.operationId,
          outcome: {
            kind: "REVIEWED",
            review,
          },
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    }),
  );
  vi.stubGlobal("fetch", fetch);
  let current: OperationModel | undefined;
  const Harness = () => {
    current = useOperation({
      subject: "PEOPLE",
      kind: "people.enable",
      current: null,
      credential: "token",
      onLockChange: vi.fn(),
    });
    return null;
  };
  render(<Harness />);
  const model = () => {
    if (current === undefined) {
      throw new Error("No operation model");
    }
    return current;
  };
  return {
    model,
    bodies,
    fetch,
  };
};
afterEach(() => vi.unstubAllGlobals());
it("binds a Core principal atomically outside editable inputs and retains it across action resets", async () => {
  const f = fixture();
  await act(() =>
    Promise.try(() => {
      f.model().discard(
        false,
        "people.enable",
        {
          "input:principal.kind": "HUMAN",
          "input:principal.issuer": "https://bait.example/",
          "input:principal.subject": "bait",
        },
        {
          kind: "PEOPLE",
          principal: first,
        },
      );
    }),
  );
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    principal: first,
  });
  expect(f.model().values).toEqual({});
  expect(f.model().contextChanged).toBe(false);
  await act(() =>
    Promise.try(() => {
      f.model().discard(false, "people.disable");
    }),
  );
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    principal: first,
  });
  await act(() =>
    Promise.try(() => {
      f.model().discard(false, "people.enable");
    }),
  );
  await act(() => f.model().preview());
  await waitFor(() => {
    expect(f.model().phase).toBe("REVIEWING");
  });
  expect(JSON.parse(f.bodies[0]!)).toMatchObject({
    kind: "PEOPLE",
    target: first,
    act: {
      kind: "people.enable",
    },
  });
});
it("an explicit target reset clears consent and allocates the next identity only at its new preview", async () => {
  const f = fixture();
  await act(() =>
    Promise.try(() => {
      f.model().discard(
        false,
        "people.enable",
        {},
        {
          kind: "PEOPLE",
          principal: first,
        },
      );
    }),
  );
  await act(() => f.model().preview());
  await waitFor(() => {
    expect(f.model().phase).toBe("REVIEWING");
  });
  await act(() =>
    Promise.try(() => {
      f.model().confirm(true);
    }),
  );
  const old = f.model();
  expect(old.confirmed).toBe(true);
  await act(() =>
    Promise.try(() => {
      f.model().discard(
        false,
        "people.enable",
        {},
        {
          kind: "PEOPLE",
          principal: second,
        },
      );
    }),
  );
  expect(f.model().phase).toBe("EDITING");
  expect(f.model().confirmed).toBe(false);
  expect(f.model().prepared).toBeNull();
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    principal: second,
  });
  await act(() => old.execute());
  expect(f.fetch).toHaveBeenCalledTimes(1);
  await act(() => f.model().preview());
  await waitFor(() => {
    expect(f.model().phase).toBe("REVIEWING");
  });
  expect(JSON.parse(f.bodies[1]!)).toMatchObject({
    target: second,
  });
  expect(operationRequest(f.bodies[1]).operationId).not.toBe(
    operationRequest(f.bodies[0]).operationId,
  );
  expect(f.model().confirmed).toBe(false);
});
