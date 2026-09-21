import type { RecoveryRejection } from "../src/generated/convergence/web-v2.types";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CurrentCase, PreparationDetails, Rejection } from "../src/api/v2";
import { OperationEditor } from "../src/views/OperationEditor";
import { generatedResponse } from "./contract-corpus.fixtures";
import { definition, fields, operationId, preparation, response } from "./v2-ui.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE", "OPEN"],
};

const review = {
  before: { fields, revision: "1" },
  proposed: { fields, revision: "2" },
  changes: [{ fieldName: "status", before: "OPENED", after: "CLOSED" }],
  context: definition.runtime,
  advisory: true,
};

const renderEditor = (overrides: Partial<React.ComponentProps<typeof OperationEditor>> = {}) => {
  const defaults: React.ComponentProps<typeof OperationEditor> = {
    token: "token",
    definition,
    current,
    initialCommand: "CLOSE",
    onClose: vi.fn(),
    onCommitted: vi.fn(),
    onMutationLockChange: vi.fn(),
  };
  return render(<OperationEditor {...defaults} {...overrides} />);
};

const preparedResponse = (details: PreparationDetails = preparation) =>
  response("command.prepare", "PREPARED", { details, review });

const acceptedResponse = () => generatedResponse("command.execute");

const openReview = async (user: ReturnType<typeof userEvent.setup>): Promise<void> => {
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  await screen.findByRole("dialog", { name: "Review prepared operation" });
};

const selectConfirmedSubmit = async (user: ReturnType<typeof userEvent.setup>): Promise<void> => {
  await user.click(
    screen.getByRole("checkbox", { name: "I will submit this exact prepared request." }),
  );
  await user.click(screen.getByRole("button", { name: "Submit exact request" }));
};

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

const definitePrepareRejection = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("command.prepare", "REJECTED", {
      operationId,
      rejection: {
        code: "INVALID_INPUT",
        message: "Correct the case reference.",
        diagnostic: { id: "INPUT_TEXT_REQUIRED", parameters: {} },
        field: "caseReference",
        actualRevision: null,
        recommendedAction: "CORRECT_INPUT",
      } satisfies Rejection,
    }),
  );
  renderEditor({ current: null, initialCommand: "OPEN" });
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  const input = screen.getByLabelText("Handler's case reference", { exact: true });
  await waitFor(() => expect(document.activeElement).toBe(input));
  expect(input).toHaveAttribute("aria-invalid", "true");
  expect(screen.getAllByText("Correct the case reference.").length).toBeGreaterThan(0);
  expect(screen.queryByRole("dialog", { name: "Review prepared operation" })).toBeNull();
};

const namedAuthoringRejection = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("command.prepare", "REJECTED", {
      operationId,
      rejection: {
        code: "INVALID_INPUT",
        message: "Correct the incident date.",
        diagnostic: { id: "INPUT_CALENDAR_DATE_REQUIRED", parameters: {} },
        field: "incidentDate",
        actualRevision: null,
        recommendedAction: "CORRECT_INPUT",
      } satisfies Rejection,
    }),
  );
  renderEditor({ current: null, initialCommand: "OPEN" });
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  const input = screen.getByLabelText("Incident date", { exact: true });
  await waitFor(() => expect(document.activeElement).toBe(input));
  expect(input).toHaveAttribute("aria-invalid", "true");
  const descriptions = (input.getAttribute("aria-describedby") ?? "")
    .split(" ")
    .map((id) => document.getElementById(id)?.textContent ?? "")
    .join(" ");
  expect(descriptions).toContain("Correct the incident date.");
};

const uncertainPrepareDelivery = async (): Promise<void> => {
  const user = userEvent.setup();
  const lock = vi.fn();
  vi.mocked(globalThis.fetch).mockRejectedValueOnce(new Error("Synthetic delivery loss"));
  renderEditor({ onMutationLockChange: lock });
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Inspect Recovery");
  expect(screen.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
  expect(lock).toHaveBeenCalledWith(true);
};

const missingDigest = async (): Promise<void> => {
  const user = userEvent.setup();
  const digestless = { ...preparation, summary: { ...preparation.summary, requestSha256: null } };
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(preparedResponse(digestless));
  renderEditor();
  await openReview(user);
  await selectConfirmedSubmit(user);
  expect(screen.getByRole("dialog", { name: "Review prepared operation" })).toBeVisible();
  expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(1);
};

const definiteSubmitRejection = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(preparedResponse())
    .mockResolvedValueOnce(
      response("command.execute", "REFUSED_BEFORE_ATTEMPT", {
        preparation: null,
        rejection: {
          code: "PREPARATION_DISMISSED",
          diagnostic: { id: "RECOVERY_PREPARATION_DISMISSED", parameters: {} },
          message: "The exact preparation was refused.",
          recommendedAction: "READ_CURRENT",
        } satisfies RecoveryRejection,
      }),
    );
  renderEditor();
  await openReview(user);
  await selectConfirmedSubmit(user);
  expect(await screen.findByRole("alert")).toHaveTextContent("The exact preparation was refused.");
};

const uncertainSubmitDelivery = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(preparedResponse())
    .mockRejectedValueOnce(new Error("Lost"));
  renderEditor();
  await openReview(user);
  await selectConfirmedSubmit(user);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Inspect Recovery before taking another action.",
  );
  expect(screen.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
};

const acceptedSubmission = async (): Promise<void> => {
  const user = userEvent.setup();
  const committed = vi.fn();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(preparedResponse())
    .mockResolvedValueOnce(acceptedResponse());
  renderEditor({ onCommitted: committed });
  await openReview(user);
  await selectConfirmedSubmit(user);
  await user.click(await screen.findByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
};

const submittingState = async (): Promise<void> => {
  const user = userEvent.setup();
  let resolveResponse: (value: Response) => void = () => undefined;
  const pending = new Promise<Response>((resolve) => {
    resolveResponse = resolve;
  });
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(preparedResponse())
    .mockReturnValueOnce(pending);
  renderEditor();
  await openReview(user);
  await selectConfirmedSubmit(user);
  expect(screen.getByRole("button", { name: "Submitting…" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Keep for Recovery" })).toBeDisabled();
  resolveResponse(acceptedResponse());
  await screen.findByRole("heading", { name: "Accepted operation" });
};

describe("operation editor transport outcomes", () => {
  it(
    "shows a definite prepare rejection without creating recovery uncertainty",
    definitePrepareRejection,
  );
  it("focuses and describes a named authored field after core rejection", namedAuthoringRejection);
  it(
    "locks the editor and directs recovery after an uncertain prepare delivery",
    uncertainPrepareDelivery,
  );
  it("keeps review open when a malformed prepared result omits its exact digest", missingDigest);
  it(
    "shows a definite submit rejection after preserving the prepared identity",
    definiteSubmitRejection,
  );
  it(
    "preserves only recovery direction after an uncertain submit delivery",
    uncertainSubmitDelivery,
  );
  it("renders an accepted receipt and delegates its return action", acceptedSubmission);
  it("makes the review modal non-dismissable while submission is dispatched", submittingState);
});

const openDiscardDialog = async (user: ReturnType<typeof userEvent.setup>): Promise<void> => {
  renderEditor();
  await user.selectOptions(screen.getByLabelText("Command"), "OPEN");
  await user.type(screen.getByLabelText(/Incident date/u), "2026-09-09");
  await user.selectOptions(screen.getByLabelText("Command"), "CLOSE");
  await screen.findByRole("dialog", { name: "Discard this command draft?" });
};

const cancelReview = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(preparedResponse());
  renderEditor();
  await openReview(user);
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("dialog", { name: "Review prepared operation" })).toBeNull();
  const prepareButton = screen.getByRole("button", { name: "Prepare exact request" });
  expect(prepareButton).toBeVisible();
  await waitFor(() => expect(document.activeElement).toBe(prepareButton));
};

const keepDirtyCommand = async (): Promise<void> => {
  const user = userEvent.setup();
  await openDiscardDialog(user);
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  expect(screen.getByRole("heading", { name: "Open a case" })).toBeVisible();
};

const confirmCommandDiscard = async (): Promise<void> => {
  const user = userEvent.setup();
  await openDiscardDialog(user);
  await user.click(await screen.findByRole("button", { name: "Discard and change command" }));
  expect(screen.getByRole("heading", { name: "Close the case" })).toBeVisible();
};

const cancelCommandDiscard = async (): Promise<void> => {
  const user = userEvent.setup();
  await openDiscardDialog(user);
  await user.click(await screen.findByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("dialog", { name: "Discard this command draft?" })).toBeNull();
};

const retainPreparedCommand = async (): Promise<void> => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(preparedResponse());
  renderEditor();
  await openReview(user);
  await user.click(screen.getByRole("button", { name: "Keep for Recovery" }));
  expect(screen.queryByRole("dialog", { name: "Review prepared operation" })).toBeNull();
};

const newCaseReference = async (): Promise<void> => {
  const user = userEvent.setup();
  const closed = vi.fn();
  renderEditor({ current: null, initialCommand: "OPEN", onClose: closed });
  await user.type(screen.getByLabelText(/Handler's case reference/u), "NEW-1");
  await user.click(screen.getByRole("button", { name: "Back without preparing" }));
  expect(closed).toHaveBeenCalledOnce();
};

describe("operation editor dialogs and render branches", () => {
  it("keeps the editor when the review dialog is cancelled", cancelReview);
  it("keeps a dirty command when the discard dialog is cancelled", keepDirtyCommand);
  it("changes a dirty command only after the discard confirmation", confirmCommandDiscard);
  it("closes the discard dialog through its accessible cancel action", cancelCommandDiscard);
  it("keeps a prepared command for recovery through the review action", retainPreparedCommand);
  it("renders the OPEN reference input and delegates an unprepared close", newCaseReference);
});
