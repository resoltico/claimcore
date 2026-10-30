import { actorContextInput } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import {
  getButton,
  fireEvent,
  render,
  screen,
  waitFor,
} from "../support/presentation-test-support";
import { AuthorityPanel } from "../../src/views/AuthorityPanel";
import { productMetadata } from "../../src/protocol/productMetadata";
import { isRequest } from "../../src/protocol/client";
import type { RequestByEndpoint } from "@contracts/client";
import { installationOffers } from "../support/installation-advice.fixtures";
import { wireReply } from "../support/contract-corpus.fixtures";
import { runtime, input, response, type Dispatch } from "../support/catalog-transport.fixtures";
afterEach(() => vi.unstubAllGlobals());
type AuthorityInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "AUTHORITY";
  }
>;
const authorityActs = productMetadata.catalog.entries
  .filter((entry) => entry.endpoint === "operation.execute" && entry.subject === "AUTHORITY")
  .map((entry) => entry as typeof entry & { kind: AuthorityInput["act"]["kind"] });
const authorityReceipt = (request: AuthorityInput) => {
  const source = wireReply("operation.execute", "AUTHORITY");
  if (source.outcome.kind !== "ACCEPTED" || source.outcome.data.subject.kind !== "AUTHORITY") {
    throw new Error("Missing frozen authority alternative.");
  }
  return {
    ...source.outcome.data,
    operationId: request.operationId,
    subject: {
      ...source.outcome.data.subject,
      act: request.act.kind,
    },
  };
};
const contextReply = (): Response =>
  response("context", {
    outcome: {
      kind: "SUCCEEDED",
      data: actorContextInput({
        ...runtime,
        capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
        views: ["AUTHORITY", "OPERATIONS"],
        offers: installationOffers(["REGISTER", "GRANT", "REVOKE", "ENABLE", "DISABLE"]),
      }),
    },
  });
const authorityPreview = (parsed: AuthorityInput) =>
  response("operation.preview", {
    operationId: parsed.operationId,
    outcome: {
      kind: "REVIEWED",
      data: {
        review: {
          subject: {
            kind: "AUTHORITY",
            target: null,
            before: {
              enabled: false,
              grants: [],
            },
            after: {
              enabled: true,
              grants: [],
            },
          },
        },
      },
    },
  });
const authorityTransport = (calls: Dispatch<AuthorityInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (path === "/api/context") {
      return Promise.resolve(contextReply());
    }
    if (path === "/api/authority.list") {
      return Promise.resolve(
        response("authority.list", {
          outcome: {
            kind: "SUCCEEDED",
            data: {
              items: [],
              nextCursor: null,
            },
          },
        }),
      );
    }
    if (
      typeof path !== "string" ||
      !["/api/operation.execute", "/api/operation.preview"].includes(path) ||
      typeof init?.body !== "string"
    ) {
      throw new Error("Unexpected authority endpoint.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("operation.execute", parsed) || parsed.kind !== "AUTHORITY") {
      throw new Error("Production authority form did not produce a complete request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: parsed,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(authorityPreview(parsed));
    }
    return Promise.resolve(
      response("operation.execute", {
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: authorityReceipt(parsed),
        },
      }),
    );
  });
const authorAuthority = async (kind: AuthorityInput["act"]["kind"]) => {
  await waitFor(() => {
    expect(
      screen
        .getByLabelText("Authority action", {
          exact: true,
        })
        .querySelectorAll("option"),
    ).toHaveLength(5);
  });
  fireEvent.change(
    screen.getByLabelText("Authority action", {
      exact: true,
    }),
    {
      target: {
        value: kind,
      },
    },
  );
  input("target.issuer", "https://issuer.example/");
  input("target.subject", "synthetic-catalogue-human");
  input("target.kind", "HUMAN");
  if (kind === "GRANT" || kind === "REVOKE") {
    input("act.role", "CASE_READER");
  }
};
it.each(authorityActs)(
  "renders, reviews and submits real authority act $kind through production transport",
  async (act) => {
    expect(authorityActs).toHaveLength(5);
    const calls: Dispatch<AuthorityInput>[] = [];
    vi.stubGlobal("fetch", authorityTransport(calls));
    render(<AuthorityPanel onLockChange={vi.fn()} />);
    await authorAuthority(act.kind);
    const user = userEvent.setup();
    await user.click(getButton("Review changes"));
    await screen.findByRole("heading", {
      name: "Review changes",
    });
    const before = screen.getByRole("dialog").textContent;
    await user.click(
      screen.getByRole("checkbox", {
        name: "I confirm this exact request.",
      }),
    );
    await user.click(getButton("Record changes"));
    await waitFor(() => {
      expect(calls).toHaveLength(2);
      expect(calls[1]!.bytes).toBe(calls[0]!.bytes);
    });
    await screen.findByText(calls[0]!.input.operationId, {
      exact: true,
    });
    expect(before).toContain(calls[0]!.input.operationId);
    expect(calls[0]!.input.act.kind).toBe(act.kind);
    expect(calls[0]!.input.target).toEqual({
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "synthetic-catalogue-human",
    });
    if (calls[0]!.input.act.kind === "GRANT" || calls[0]!.input.act.kind === "REVOKE") {
      expect(calls[0]!.input.act).toMatchObject({
        role: "CASE_READER",
        scope: {
          kind: "INSTALLATION",
        },
      });
    }
    if (calls[0]!.input.act.kind === "ENABLE") {
      expect(calls[0]!.input.act).toEqual({
        kind: "ENABLE",
      });
    }
  },
);
