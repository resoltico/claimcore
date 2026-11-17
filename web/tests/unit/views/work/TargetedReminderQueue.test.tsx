import { humanReadReply } from "../../../support/human-read-reply.fixtures";
import { caseDocumentInput } from "../../../support/case-document.fixtures";
import { timelineDocument } from "../../../support/timeline-document.fixtures";
import { productMetadata } from "../../../../src/protocol/productMetadata";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { navigate } from "../../../../src/routing/navigation";
import { useRoute } from "../../../../src/routing/useRoute";
import { CaseDetail } from "../../../../src/views/CaseDetail";
import { ReminderQueue } from "../../../../src/views/work/ReminderQueue";
import { workBasis } from "../../../support/inputs";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { CaseSelector } from "@contracts/client";

const reference = "VISIBLE-CASE-2026";
const id = "10000000-0000-4000-8000-000000000001";
const emptyPage = (nextCursor: string | null = null) =>
  humanReadReply("reminder.list", {
    screen: { regions: [] },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor,
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  });
const Workspace = () => {
  const route = useRoute();
  return route.screen === "reminders" ? (
    <ReminderQueue />
  ) : (
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: reference }}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  );
};
it("navigates from the supplied case workspace link into a visible targeted queue without inventing a standard view", async () => {
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply(
      "case.get",
      caseDocumentInput({
        links: [
          {
            rel: "VIEW",
            labelId: "label.caseReminders",
            link: {
              endpoint: "reminder.list",
              input: {
                target: { kind: "REFERENCE", caseReference: reference },
                includeInactive: true,
              },
            },
          },
        ],
      }),
    ),
  });
  mockEndpoint("case.timeline").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.timeline", timelineDocument()),
  });
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: emptyPage(),
  });
  navigate(`/cases/${reference}`, true);
  render(<Workspace />);
  const link = await screen.findByRole("link", { name: "Reminders for this case" });
  expect(link).toHaveAttribute(
    "href",
    `/reminders?includeInactive=true&caseReference=${reference}&filters=manual`,
  );
  await userEvent.click(link);
  expect(await screen.findByRole("heading", { name: "Reminders" })).toBeVisible();
  expect(screen.getByText(reference)).toBeVisible();
  expect(screen.getByRole("checkbox", { name: "Include inactive case work" })).toBeChecked();
  await waitFor(() => {
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({
      target: { kind: "REFERENCE", caseReference: reference },
      includeInactive: true,
      limit: 50,
    });
  });
});
it.each<CaseSelector>([
  { kind: "REFERENCE", caseReference: reference },
  { kind: "ID", caseId: id },
])(
  "shows and clears target $kind while retaining composed filters and resetting actual paging",
  async (target) => {
    const text = target.kind === "REFERENCE" ? target.caseReference : target.caseId;
    const key = target.kind === "REFERENCE" ? "caseReference" : "caseId";
    const reads = mockEndpoint("reminder.list").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: emptyPage("opaque-next"),
    });
    navigate(
      `/reminders?${key}=${text}&due=TODAY&assignment=MINE&q=synthetic+action&state=PENDING&includeInactive=true&filters=manual`,
      true,
    );
    render(<ReminderQueue />);
    await waitFor(() => {
      expect(reads).toHaveBeenCalledOnce();
    });
    expect(screen.getByText(text)).toBeVisible();
    expect(screen.getByText(text).closest("bdi")).not.toBeNull();
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "More reminders" }));
    await waitFor(() => {
      expect(reads.mock.calls.at(-1)?.[1]).toMatchObject({ cursor: "opaque-next", target });
    });
    reads.mockResolvedValue({ kind: "outcome", status: 200, value: emptyPage() });
    await user.click(screen.getByRole("button", { name: "Clear case restriction" }));
    await waitFor(() => {
      expect(reads.mock.calls.at(-1)?.[1]).toEqual({
        due: "TODAY",
        assignment: "MINE",
        text: "synthetic action",
        state: "PENDING",
        includeInactive: true,
        limit: 50,
      });
    });
    expect(screen.queryByText(text)).toBeNull();
    expect(location.search).not.toMatch(/caseReference|caseId|cursor/u);
    expect(screen.getByRole("heading", { name: "Reminders" })).toHaveFocus();
  },
);
it("keeps the active case visible through real filter composition and reload", async () => {
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: emptyPage(),
  });
  navigate(`/reminders?caseReference=${reference}&filters=manual`, true);
  const view = render(<ReminderQueue />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Reminder due" }));
  await user.click(screen.getByRole("option", { name: "Today" }));
  await user.click(screen.getByRole("combobox", { name: "Responsibility" }));
  await user.click(screen.getByRole("option", { name: "Mine" }));
  await user.type(screen.getByRole("searchbox", { name: "Search" }), "synthetic action");
  await user.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => {
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({
      target: { kind: "REFERENCE", caseReference: reference },
      due: "TODAY",
      assignment: "MINE",
      text: "synthetic action",
      limit: 50,
    });
  });
  expect(screen.getByText(reference)).toBeVisible();
  view.unmount();
  render(<ReminderQueue />);
  expect(screen.getByText(reference)).toBeVisible();
  await waitFor(() => {
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({
      target: { kind: "REFERENCE", caseReference: reference },
      due: "TODAY",
      assignment: "MINE",
      text: "synthetic action",
      limit: 50,
    });
  });
});
