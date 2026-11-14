import { operationAffordance } from "../../../support/affordances.fixtures";
import type { RequestByEndpoint, HumanReview } from "@contracts/client";
import { operationRequest, requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../../support/presentation-test-support";
import { useOperation, type OperationModel } from "../../../../src/hooks/operation/useOperation";
const first = "10000000-0000-7000-8000-000000000001";
const second = "10000000-0000-7000-8000-000000000002";
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
          subject: {
            kind: "REVIEW",
            confirmation: {
              kind: "OPERATION",
              operationId: operationExecuteRequest.operationId,
            },
            consequence: "ROUTINE",
          },
          actions: [
            operationAffordance(
              operationExecuteRequest.task,
              operationExecuteRequest.binding,
              {},
              [],
            ),
          ],
          links: [],
          history: null,
          nextCursor: null,
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
      affordances: [
        operationAffordance("people.enable", { kind: "PEOPLE", actorId: first }),
        operationAffordance("people.disable", { kind: "PEOPLE", actorId: first }),
      ],
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
it("binds a Core actor atomically outside editable inputs and retains it across action resets", async () => {
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
          actorId: first,
        },
      );
    }),
  );
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    actorId: first,
  });
  expect(f.model().values["input:principal.subject"]).toBe("bait");
  expect(f.model().contextChanged).toBe(false);
  await act(() =>
    Promise.try(() => {
      f.model().discard(false, "people.disable");
    }),
  );
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    actorId: first,
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
    task: "people.enable",
    binding: { kind: "PEOPLE", actorId: first },
    inputs: {},
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
          actorId: first,
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
          actorId: second,
        },
      );
    }),
  );
  expect(f.model().phase).toBe("EDITING");
  expect(f.model().confirmed).toBe(false);
  expect(f.model().prepared).toBeNull();
  expect(f.model().bindingSource).toEqual({
    kind: "PEOPLE",
    actorId: second,
  });
  await act(() => old.execute());
  expect(f.fetch).toHaveBeenCalledTimes(1);
  await act(() => f.model().preview());
  await waitFor(() => {
    expect(f.model().phase).toBe("REVIEWING");
  });
  expect(JSON.parse(f.bodies[1]!)).toMatchObject({
    binding: { actorId: second },
  });
  expect(operationRequest(f.bodies[1]).operationId).not.toBe(
    operationRequest(f.bodies[0]).operationId,
  );
  expect(f.model().confirmed).toBe(false);
});
