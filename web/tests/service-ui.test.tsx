import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "../src/App";
import type { CurrentCase } from "../src/api/service";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { DescriptorField } from "../src/components/DescriptorField";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { definition, fields, operationId, digest, runtime, response } from "./service-ui.fixtures";

const current: CurrentCase = {
  state: "LIVE",
  disposition: "ACTIVE",
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

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
    previewForRequest(
      response("command.preview", "REVIEWED", {
        requestSha256: digest,
        review: {
          before: { fields, revision: "1" },
          proposed: { fields, revision: "2" },
          changes: [],
          context: runtime,
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
      onMutationLockChange={vi.fn()}
    />,
  );
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(await screen.findByRole("dialog", { name: "Review changes" })).toBeVisible();
  await user.click(screen.getByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  expect(await screen.findByRole("heading", { name: "Recorded change" })).toBeVisible();
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

describe("service session lifecycle", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

  it("offers OIDC sign-in, then hydrates an authenticated return and signs out", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(
      response("session", "SUCCEEDED", { authenticated: false, antiforgeryToken: "anonymous" }),
    );
    const anonymous = render(<App />);
    const signIn = await screen.findByRole("link", { name: "Sign in" });
    expect(signIn).toHaveAttribute("href", "/auth/login");
    expect(fetch).toHaveBeenCalledTimes(1);
    anonymous.unmount();
    fetch.mockResolvedValueOnce(
      response("session", "SUCCEEDED", {
        authenticated: true,
        antiforgeryToken: "authenticated",
      }),
    );
    fetch.mockResolvedValueOnce(
      response("case.list", "SUCCEEDED", { items: [], nextCursor: null }),
    );
    fetch.mockResolvedValueOnce(
      response("session.logout", "SUCCEEDED", {
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
