import { afterEach, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { render } from "../../../support/presentation-test-support";
import {
  useAuthorityTarget,
  type AuthorityTarget,
} from "../../../../src/hooks/operation/useAuthorityTarget";
import { useOperation } from "../../../../src/hooks/operation/useOperation";
import { navigate } from "../../../../src/routing/navigation";
import type { PersonDocument } from "../../../../src/views/AuthorityGrants";
import { isRequest, type HumanReview } from "@contracts/client";

const firstId = "00000000-0000-4000-8000-000000000001";
const secondId = "00000000-0000-4000-8000-000000000002";
const person = (actorId: string, subject: string): PersonDocument => ({
  screen: { regions: [] },
  context: {
    kind: "PERSON",
    actorId,
    principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject },
    nextCursor: null,
    tasks: ["AUTHORITY:ENABLE"],
    profileSeed: null,
    revocations: [],
    responsibleCases: null,
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
});
const first = person(firstId, "person-a");
const second = person(secondId, "person-b");
const fixture = () => {
  const bodies: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation(async (_url, options) => {
      const input: unknown = JSON.parse(String(options?.body));
      if (!isRequest("operation.execute", input)) {
        throw new Error("Expected typed authority command");
      }
      bodies.push(String(options?.body));
      const review: HumanReview = {
        presentation: { changes: [], unchanged: [] },
        document: {
          screen: { regions: [] },
          context: {
            kind: "REVIEW",
            task: "AUTHORITY:ENABLE",
            target: { kind: "AUTHORITY", actorId: firstId, scope: { kind: "INSTALLATION" } },
            inputs: [
              "input:operationId",
              "input:principal.kind",
              "input:principal.issuer",
              "input:principal.subject",
            ],
            consequence: "ROUTINE",
            confirmation: { kind: "OPERATION", operationId: input.operationId },
          },
          basis: first.basis,
          disclosure: "CURRENT",
        },
      };
      return new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: input.operationId,
          outcome: { kind: "REVIEWED", review },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      );
    }),
  );
  let current: AuthorityTarget | undefined;
  const Harness = () => {
    const model = useOperation({
      subject: "AUTHORITY",
      kind: "ADMIT",
      current: null,
      credential: "token",
      onLockChange: vi.fn(),
    });
    current = useAuthorityTarget(model, 0, true);
    return null;
  };
  navigate(`/people/${firstId}`, true);
  render(<Harness />);
  const target = () => {
    if (current === undefined) {
      throw new Error("No target");
    }
    return current;
  };
  return { target, bodies };
};
afterEach(() => vi.unstubAllGlobals());
it("keeps a retained request on person A until explicit discard consent binds the selected person B", async () => {
  const f = fixture();
  await act(async () => f.target().observe(first));
  await waitFor(() => expect(f.target().mode).toBe("bound"));
  await act(async () => f.target().model.preview());
  await waitFor(() => expect(f.target().model.phase).toBe("REVIEWING"));
  const original = f.target().model.prepared;
  await act(async () => navigate(`/people/${secondId}`, true));
  await act(async () => f.target().observe(second));
  expect(f.target().mode).toBe("retained");
  expect(f.target().person).toBeNull();
  expect(f.target().model.bindingSource).toEqual({
    kind: "AUTHORITY",
    principal: first.context.principal,
  });
  await act(async () => f.target().begin("ENABLE"));
  expect(f.target().model.pendingDiscard).toBe("ENABLE");
  expect(f.target().model.prepared).toBe(original);
  expect(f.bodies).toHaveLength(1);
  await act(async () => f.target().model.discard(false, "ENABLE"));
  expect(f.target().model.bindingSource).toEqual({
    kind: "AUTHORITY",
    principal: second.context.principal,
  });
  expect(f.target().model.prepared).toBeNull();
  expect(f.target().model.confirmed).toBe(false);
  expect(f.bodies).toHaveLength(1);
});
it("uses the permitted profile seed rather than saved account presentation and ignores a wrong-person observation", async () => {
  const f = fixture();
  await act(async () => f.target().observe(second));
  expect(f.target().offered).toEqual([]);
  const profile: PersonDocument = {
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
                value: { kind: "TEXT", value: "Saved read-only caption" },
              },
            ],
          },
        },
      ],
    },
    context: {
      ...first.context,
      tasks: ["AUTHORITY:SET_PROFILE"],
      profileSeed: { displayName: "Core editable profile seed", contact: "contact@example.test" },
    },
  };
  await act(async () => f.target().observe(profile));
  await act(async () => f.target().begin("SET_PROFILE"));
  expect(f.target().model.values).toEqual({
    "input:profile.displayName": "Core editable profile seed",
    "input:profile.contact": "contact@example.test",
  });
  expect(f.target().model.bindingSource).toEqual({
    kind: "AUTHORITY",
    principal: first.context.principal,
  });
  expect(f.bodies).toEqual([]);
});
