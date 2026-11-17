import { allActions } from "../../../../src/operation/affordances";
import {
  withRecommendation,
  operationAffordance,
  requireOperationAffordance,
} from "../../../support/affordances.fixtures";

import type { RequestByEndpoint, HumanReview } from "@contracts/client";
import { requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../../support/presentation-test-support";
import {
  usePeopleTarget,
  type PeopleTarget,
} from "../../../../src/hooks/operation/usePeopleTarget";
import { useOperation } from "../../../../src/hooks/operation/useOperation";
import { navigate } from "../../../../src/routing/navigation";
import type { PersonDocument } from "../../../../src/protocol/types";
const firstId = "00000000-0000-4000-8000-000000000001";
const secondId = "00000000-0000-4000-8000-000000000002";
const person = (actorId: string, _subject: string): PersonDocument =>
  withRecommendation(
    {
      screen: {
        regions: [],
      },
      subject: { kind: "PERSON", actorId },
      actions: [
        operationAffordance(
          "people.enable",
          { kind: "PEOPLE", actorId, expectedRevision: 1 },
          {},
          undefined,
          {},
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
    "people.enable",
  );
const first = person(firstId, "person-a");
const second = person(secondId, "person-b");
const fixture = () => {
  const bodies: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((_url, options) =>
      Promise.try(() => {
        const input: unknown = JSON.parse(requestText(options?.body));
        bodies.push(requestText(options?.body));
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
                "people.enable",
                { kind: "PEOPLE", expectedRevision: 1, actorId: firstId },
                {},
                [],
              ),
            ],
            links: [],
            history: null,
            nextCursor: null,
            basis:
              first.basis.kind === "SNAPSHOT"
                ? first.basis
                : (() => {
                    throw new Error("Snapshot required");
                  })(),
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
    ),
  );
  let current: PeopleTarget | undefined;
  const Harness = ({ available = true }: { available?: boolean }) => {
    const model = useOperation({
      subject: "PEOPLE",
      kind: "people.admit",
      current: null,
      credential: "token",
      onLockChange: vi.fn(),
    });
    current = usePeopleTarget(model, 0, available);
    return null;
  };
  navigate(`/people/${firstId}`, true);
  const view = render(<Harness />);
  const target = () => {
    if (current === undefined) {
      throw new Error("No target");
    }
    return current;
  };
  return {
    target,
    availability: (available: boolean) => {
      view.rerender(<Harness available={available} />);
    },
    bodies,
  };
};
afterEach(() => vi.unstubAllGlobals());
it("keeps a retained request on person A until explicit discard consent binds the selected person B", async () => {
  const f = fixture();
  await act(() =>
    Promise.try(() => {
      f.target().observe(first);
    }),
  );
  await waitFor(() => {
    expect(f.target().mode).toBe("bound");
  });
  await act(() => f.target().model.preview());
  await waitFor(() => {
    expect(f.target().model.phase).toBe("REVIEWING");
  });
  const original = f.target().model.prepared;
  await act(() => navigate(`/people/${secondId}`, true));
  await act(() =>
    Promise.try(() => {
      f.target().observe(second);
    }),
  );
  expect(f.target().mode).toBe("retained");
  expect(f.target().person).toBeNull();
  expect(f.target().model.bindingSource).toEqual({
    kind: "PEOPLE",
    expectedRevision: 1,
    actorId: firstId,
  });
  await act(() =>
    Promise.try(() => {
      f.target().begin(
        requireOperationAffordance(
          allActions(second).find((action) => action.task === "people.enable"),
        ),
      );
    }),
  );
  expect(f.target().model.pendingDiscard).toBe("people.enable");
  expect(f.target().model.prepared).toBe(original);
  expect(f.bodies).toHaveLength(1);
  await act(() =>
    Promise.try(() => {
      f.target().model.discard(false, "people.enable");
    }),
  );
  expect(f.target().model.bindingSource).toEqual({
    kind: "PEOPLE",
    expectedRevision: 1,
    actorId: secondId,
  });
  expect(f.target().model.prepared).toBeNull();
  expect(f.target().model.confirmed).toBe(false);
  expect(f.bodies).toHaveLength(1);
});
it("uses the permitted profile seed rather than saved account presentation and ignores a wrong-person observation", async () => {
  const f = fixture();
  await act(() =>
    Promise.try(() => {
      f.target().observe(second);
    }),
  );
  expect(f.target().offered).toEqual([]);
  const profile: PersonDocument = withRecommendation(
    {
      ...first,
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
                  value: {
                    kind: "TEXT",
                    value: "Saved read-only caption",
                  },
                },
              ],
            },
          },
        ],
      },
      actions: [
        operationAffordance(
          "people.setProfile",
          { kind: "PEOPLE", expectedRevision: 1, actorId: firstId },
          {
            "input:profile.displayName": "Core editable profile seed",
            "input:profile.contact": "contact@example.test",
          },
          undefined,
          {},
        ),
      ],
    },
    "people.setProfile",
  );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile);
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.target().begin(
        requireOperationAffordance(
          allActions(profile).find((action) => action.task === "people.setProfile"),
        ),
      );
    }),
  );
  expect(f.target().model.values).toEqual({
    "input:profile.displayName": "Core editable profile seed",
    "input:profile.contact": "contact@example.test",
  });
  expect(f.target().model.bindingSource).toEqual({
    kind: "PEOPLE",
    expectedRevision: 1,
    actorId: firstId,
  });
  expect(f.bodies).toEqual([]);
});

it("requires explicit latest review before replacing a People revision and keeps authored values without dispatch", async () => {
  const f = fixture();
  const profile = (revision: number): PersonDocument =>
    withRecommendation(
      {
        ...first,
        actions: [
          operationAffordance(
            "people.setProfile",
            { kind: "PEOPLE", actorId: firstId, expectedRevision: revision },
            { "input:profile.displayName": "Current saved name" },
            undefined,
            {},
          ),
        ],
      },
      "people.setProfile",
    );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile(1));
    }),
  );
  await waitFor(() => {
    expect(f.target().model.entry.kind).toBe("people.setProfile");
  });
  await act(() =>
    Promise.try(() => {
      f.target().model.edit("input:profile.displayName", "Authored name");
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile(2));
    }),
  );
  expect(f.target().model.bindingSource).toMatchObject({ expectedRevision: 1 });
  await act(() =>
    Promise.try(() => {
      f.target().model.refreshPrecondition();
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile(2));
    }),
  );
  await waitFor(() => {
    expect(f.target().model.bindingSource).toMatchObject({ expectedRevision: 2 });
  });
  expect(f.target().model.values["input:profile.displayName"]).toBe("Authored name");
  expect(f.bodies).toEqual([]);
});

it("does not let a late fresh-review response retarget authored work to another selected actor", async () => {
  const f = fixture();
  const profile = (actorId: string, revision: number): PersonDocument =>
    withRecommendation(
      {
        ...person(actorId, "person"),
        actions: [
          operationAffordance(
            "people.setProfile",
            { kind: "PEOPLE", actorId, expectedRevision: revision },
            { "input:profile.displayName": "Saved name" },
            undefined,
            {},
          ),
        ],
      },
      "people.setProfile",
    );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile(firstId, 1));
    }),
  );
  await waitFor(() => {
    expect(f.target().model.entry.kind).toBe("people.setProfile");
  });
  await act(() =>
    Promise.try(() => {
      f.target().model.edit("input:profile.displayName", "Only for person A");
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.target().model.refreshPrecondition();
    }),
  );
  await act(() =>
    Promise.try(() => {
      navigate(`/people/${secondId}`, true);
    }),
  );
  await act(() =>
    Promise.try(() => {
      f.target().observe(profile(secondId, 2));
    }),
  );
  expect(f.target().model.bindingSource).toMatchObject({ actorId: firstId, expectedRevision: 1 });
  expect(f.target().model.values["input:profile.displayName"]).toBe("Only for person A");
  expect(f.bodies).toEqual([]);
});

it.each([
  { "input:scope": "INSTALLATION" },
  { "input:scope": "CASE", "field:caseReference": "FIXED-CASE" },
  { "input:scope": "CASE", "input:caseId": secondId },
])(
  "fresh explicit review keeps a noneditable grant target and advances only its reviewed binding: %j",
  async (selection) => {
    const f = fixture();
    const profile = (revision: number): PersonDocument =>
      withRecommendation(
        {
          ...first,
          actions: [
            operationAffordance(
              "people.revoke",
              { kind: "PEOPLE", actorId: firstId, expectedRevision: revision },
              { "input:role": "CASE_READER", ...selection },
              [],
              {},
            ),
            operationAffordance("people.revoke", {
              kind: "PEOPLE",
              actorId: firstId,
              expectedRevision: revision,
            }),
          ],
        },
        "people.revoke",
      );
    await act(() =>
      Promise.try(() => {
        f.target().observe(profile(1));
      }),
    );
    await waitFor(() => {
      expect(f.target().model.entry.kind).toBe("people.revoke");
    });
    await act(() => f.target().model.preview());
    await waitFor(() => {
      expect(f.bodies).toHaveLength(1);
    });
    const target = f.target().model.values["input:scope"];
    await act(() =>
      Promise.try(() => {
        f.target().observe(profile(2));
      }),
    );
    expect(f.target().model.bindingSource).toMatchObject({ expectedRevision: 1 });
    await act(() =>
      Promise.try(() => {
        f.target().model.refreshPrecondition();
      }),
    );
    f.availability(false);
    f.availability(true);
    await act(() =>
      Promise.try(() => {
        f.target().observe(profile(2));
      }),
    );
    await waitFor(() => {
      expect(f.target().model.bindingSource).toMatchObject({ expectedRevision: 2 });
    });
    expect(f.target().model.values["input:scope"]).toEqual(target);
    expect(f.target().model.affordance?.editable).toEqual([]);
    expect(f.target().model.values["input:role"]).toBe("CASE_READER");
    expect(f.bodies).toHaveLength(1);
  },
);
