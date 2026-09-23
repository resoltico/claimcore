import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "../src/App";
import type { CurrentCase } from "../src/api/v2";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { DescriptorField } from "../src/components/DescriptorField";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { definition, fields, operationId, preparation } from "./v2-ui.fixtures";

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
    if (incident === undefined) throw new Error("Incident descriptor is required.");
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
  fetch.mockResolvedValueOnce(
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
      onMutationLockChange={vi.fn()}
    />,
  );
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(await screen.findByRole("dialog", { name: "Review prepared operation" })).toBeVisible();
  await user.click(
    screen.getByRole("checkbox", { name: "I will submit this exact prepared request." }),
  );
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
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
        onMutationLockChange={vi.fn()}
      />,
    );
    await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
    await user.click(await screen.findByRole("button", { name: "Keep for Recovery" }));
    await user.selectOptions(screen.getByLabelText("Command"), "OPEN");
    await user.type(screen.getByLabelText(/Incident date/u), "2026-09-09");
    await user.selectOptions(screen.getByLabelText("Command"), "CLOSE");
    expect(
      await screen.findByRole("dialog", { name: "Discard this command draft?" }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Discard and change command" }));
    expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
  });
});

describe("v2 session lifecycle", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("hydrates, signs in, renders v2 definition, and signs out through the anonymous snapshot", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(
      response("session", "SNAPSHOT", { authenticated: false, antiforgeryToken: "anonymous" }),
    );
    fetch.mockResolvedValueOnce(
      response("session.login", "SNAPSHOT", {
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
    await user.type(await screen.findByLabelText("Bootstrap credential"), "synthetic credential");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("heading", { name: "Cases" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByRole("heading", { name: "ClaimCore" })).toBeVisible();
  });
});
