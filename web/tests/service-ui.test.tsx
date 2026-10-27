import { stubDashboardContext } from "./dashboard-context.fixtures";
import { caseAdvice } from "./case-advice.fixtures";
import { getButton, render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPresenter } from "../src/presentation/presenter";
import { defaults } from "../src/presentation/preferences";
import { App } from "../src/App";
import type { CurrentCase } from "../src/api/service";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { CatalogField } from "../src/components/CatalogField";
import { OperationEditor } from "../src/views/OperationEditor";
import { wireResponse } from "./contract-corpus.fixtures";
import { previewForRequest } from "./preview-request.fixtures";
import { context, definition, fields, runtime, response } from "./service-ui.fixtures";
const current: CurrentCase = {
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
};
describe("catalog rendering", () => {
  it("renders catalog labels and all supplied field values without client-side normalization", () => {
    const changed = vi.fn();
    const incident = definition.catalog.fields.find((field) => field.name === "incidentDate");
    if (incident === undefined) {
      throw new Error("Incident catalog entry is required.");
    }
    render(
      <>
        <CatalogField field={incident} value="2026-09-01" onChange={changed} />
        <CaseFieldsView
          values={fields}
          revision={1}
          fields={definition.catalog.fields}
          context="synthetic current case"
        />
      </>,
    );
    const input = screen.getByLabelText("Incident date", {
      exact: true,
    });
    expect(input).toHaveValue("2026-09-01");
    const descriptionId = input.getAttribute("aria-describedby");
    expect(descriptionId).not.toBeNull();
    expect(document.getElementById(descriptionId ?? "")).toHaveTextContent(
      createPresenter(defaults).fieldMeaning(incident.name),
    );
    expect(screen.getAllByText(createPresenter(defaults).fieldMeaning(incident.name))).toHaveLength(
      2,
    );
    expect(screen.getByText("CASE-1")).toBeVisible();
  });
});
const queueClosePreview = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockImplementationOnce(
    previewForRequest(
      response("operation.preview", "REVIEWED", {
        review: {
          subject: {
            kind: "CASE",
            before: {
              caseReference: fields.caseReference,
              revision: 1,
              state: {
                kind: "ACTIVE",
                fields,
              },
            },
            after: {
              caseReference: fields.caseReference,
              revision: 2,
              state: {
                kind: "ACTIVE",
                fields,
              },
            },
            changes: [],
            context: runtime,
          },
        },
      }),
    ),
  );
};
const acceptsOperation = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  queueClosePreview();
  fetch.mockResolvedValueOnce(wireResponse("operation.execute"));
  const committed = vi.fn();
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialAct="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={vi.fn()}
    />,
  );
  await user.click(getButton("Review changes"));
  expect(
    await screen.findByRole("dialog", {
      name: "Review changes",
    }),
  ).toBeVisible();
  await user.click(
    screen.getByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(getButton("Record changes"));
  expect(
    await screen.findByRole("heading", {
      name: "Recorded change",
    }),
  ).toBeVisible();
  expect(screen.getByText(/Operation .* is accepted\./u)).toBeVisible();
  await user.click(getButton("Return to case"));
  expect(committed).toHaveBeenCalledOnce();
};
describe("metadata-driven operation acceptance", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it(
    "prepares, reviews, and accepts an operation with React Aria modal semantics",
    acceptsOperation,
  );
});
const sessionLifecycle = async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("session", "SUCCEEDED", {
      authenticated: false,
      antiforgeryToken: "anonymous",
    }),
  );
  const anonymous = render(<App />);
  const signIn = await screen.findByRole("link", {
    name: "Sign in",
  });
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
    response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [],
      nextCursor: null,
    }),
  );
  fetch.mockResolvedValueOnce(
    response("session.logout", "SUCCEEDED", {
      authenticated: false,
      antiforgeryToken: "replacement",
    }),
  );
  render(<App />);
  expect(
    await screen.findByRole("heading", {
      name: "Cases",
    }),
  ).toBeVisible();
  await user.click(getButton("Sign out"));
  expect(
    await screen.findByRole("heading", {
      name: "ClaimCore",
    }),
  ).toBeVisible();
};
describe("service session lifecycle", () => {
  beforeEach(() => {
    stubDashboardContext();
  });
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it("offers OIDC sign-in, then hydrates an authenticated return and signs out", sessionLifecycle);
});
