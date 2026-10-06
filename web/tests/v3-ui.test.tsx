import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "../src/App";
import type { CurrentCase } from "../src/api/v3";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { DescriptorField } from "../src/components/DescriptorField";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { preparedForRequest } from "./prepared-request.fixtures";
import { definition, fields, operationId, preparation } from "./v3-ui.fixtures";

const current: CurrentCase = { case: { fields, revision: "1" }, availableCommands: ["CLOSE"] };
const response = (endpoint: string, tag: string, data: unknown, status = 200) =>
  new Response(JSON.stringify({ endpoint, outcome: { tag, data } }), {
    status,
    headers: { "content-type": "application/json" },
  });

describe("metadata-driven descriptor rendering", () => {
  it("renders descriptor labels and all supplied field values without client-side normalization", () => {
    const changed = vi.fn();
    const incident = definition.definition.fields.find((field) => field.name === "incidentDate");
    if (incident === undefined) {
      throw new Error("Incident descriptor is required.");
    }
    render(
      <>
        <DescriptorField field={incident} value="2026-09-01" onChange={changed} />
        <CaseFieldsView
          caseView={{ fields, revision: "1" }}
          fields={definition.definition.fields}
          context="synthetic current case"
        />
      </>,
    );
    const input = screen.getByLabelText("Incident date", { exact: true });
    expect(input).toHaveValue("2026-09-01");
    const descriptionId = input.getAttribute("aria-describedby");
    expect(descriptionId).not.toBeNull();
    expect(document.getElementById(descriptionId ?? "")).toHaveTextContent(incident.meaning);
    expect(screen.getAllByText(incident.meaning)).toHaveLength(2);
    expect(screen.getByText("CASE-1")).toBeVisible();
  });
});

const acceptsOperation = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockImplementationOnce(
    preparedForRequest(
      response("command.prepare", "PREPARED", {
        details: preparation,
        review: {
          before: { fields, revision: "1" },
          proposed: { fields, revision: "2" },
          changes: [],
          context: definition.runtime,
          advisory: true,
        },
      }),
    ),
  );
  fetch.mockResolvedValueOnce(generatedResponse("command.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialCommand="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onRecovery={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(await screen.findByRole("dialog", { name: "Review changes" })).toBeVisible();
  await user.click(screen.getByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  expect(await screen.findByRole("heading", { name: "Accepted operation" })).toBeVisible();
  expect(screen.getByText(`Operation ${operationId} is accepted.`)).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
};

describe("metadata-driven operation acceptance", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it(
    "prepares, reviews, and accepts an operation with React Aria modal semantics",
    acceptsOperation,
  );
});

describe("metadata-driven dirty command behavior", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("keeps a prepared operation in recovery and asks before discarding a dirty command switch", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(
      response("command.prepare", "PREPARED", {
        details: preparation,
        review: {
          before: null,
          proposed: { fields, revision: "2" },
          changes: [],
          context: definition.runtime,
          advisory: true,
        },
      }),
    );
    render(
      <OperationEditor
        token="token"
        definition={definition}
        current={{ ...current, availableCommands: ["CLOSE", "OPEN"] }}
        initialCommand="CLOSE"
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onRecovery={vi.fn()}
        onMutationLockChange={vi.fn()}
      />,
    );
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await user.click(
      await screen.findByRole("button", { name: "Back to editing; keep for Recovery" }),
    );
    await user.selectOptions(screen.getByLabelText("Case action"), "OPEN");
    await user.type(screen.getByLabelText(/Incident date/u), "2026-09-09");
    await user.selectOptions(screen.getByLabelText("Case action"), "CLOSE");
    expect(
      await screen.findByRole("dialog", { name: "Discard these draft changes?" }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Discard and change action" }));
    expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
  });
});

describe("v3 session lifecycle", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("offers OIDC sign-in, then hydrates an authenticated return and signs out", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(
      response("session", "SNAPSHOT", { authenticated: false, antiforgeryToken: "anonymous" }),
    );
    const anonymous = render(<App />);
    const signIn = await screen.findByRole("link", { name: "Sign in" });
    expect(signIn).toHaveAttribute("href", "/auth/login");
    expect(fetch).toHaveBeenCalledTimes(1);
    anonymous.unmount();
    fetch.mockResolvedValueOnce(
      response("session", "SNAPSHOT", {
        authenticated: true,
        antiforgeryToken: "authenticated",
      }),
    );
    fetch.mockResolvedValueOnce(response("definition", "DESCRIBED", definition));
    fetch.mockResolvedValueOnce(
      response("case.list", "SUCCEEDED", { items: [], nextCursor: null }),
    );
    fetch.mockResolvedValueOnce(
      response("session.logout", "SNAPSHOT", {
        authenticated: false,
        antiforgeryToken: "replacement",
      }),
    );
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByRole("heading", { name: "ClaimCore" })).toBeVisible();
  });
});
