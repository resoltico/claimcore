import { afterEach, expect, it } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { actorRefInput } from "../../../support/inputs";
import { TimelineEventView } from "../../../../src/views/case/TimelineEventView";
import { preferenceKey } from "../../../../src/presentation/preferences";

afterEach(() => {
  localStorage.removeItem(preferenceKey);
});
it("renders the Core's recorded event rather than a command label", () => {
  const view = render(
    <TimelineEventView event={{ kind: "CASE_OPENED", messageId: "timeline.event.caseOpened" }} />,
  );
  expect(view.container).toHaveTextContent("Case opened");
  expect(view.container).not.toHaveTextContent("Open a case");
});
for (const [event, meaning] of [
  [
    {
      kind: "RESPONSIBILITY_ASSIGNED",
      messageId: "timeline.event.responsibilityAssigned",
      person: actorRefInput({ displayName: { kind: "NAMED", value: "عميل العربية" } }),
    },
    "was made responsible",
  ],
  [
    {
      kind: "RESPONSIBILITY_CLEARED",
      messageId: "timeline.event.responsibilityCleared",
      person: actorRefInput({ displayName: { kind: "NAMED", value: "عميل العربية" } }),
    },
    "is no longer responsible",
  ],
] as const) {
  it(`isolates the supplied current actor name for ${event.kind}`, () => {
    const view = render(<TimelineEventView event={event} />);
    expect(view.container).toHaveTextContent(meaning);
    expect(view.container.querySelector("bdi")).toHaveTextContent("عميل العربية");
  });
}
it("renders an erased actor marker without reconstructing an old name", () => {
  const view = render(
    <TimelineEventView
      event={{
        kind: "RESPONSIBILITY_ASSIGNED",
        messageId: "timeline.event.responsibilityAssigned",
        person: actorRefInput({
          actorId: "11111111-1111-4111-8111-111111111111",
          displayName: { kind: "ERASED" },
        }),
      }}
    />,
  );
  expect(view.container).toHaveTextContent("Former account");
  expect(view.container).toHaveTextContent("11111111");
  expect(view.container).not.toHaveTextContent("Named account");
});
for (const event of [
  {
    kind: "REMINDER_PLANNED",
    messageId: "timeline.event.reminderPlanned",
    action: "Call العربية",
    due: "2026-11-04",
  },
  {
    kind: "REMINDER_REOPENED",
    messageId: "timeline.event.reminderReopened",
    action: "Call العربية",
    due: "2026-11-04",
  },
] as const) {
  it(`formats the supplied historical action and canonical date for ${event.kind}`, () => {
    const view = render(<TimelineEventView event={event} />);
    expect(screen.getByText("Call العربية").tagName).toBe("BDI");
    expect(view.container.querySelector("time")).toHaveAttribute("datetime", "2026-11-04");
    expect(view.container.querySelector("time")).toHaveTextContent("04/11/2026");
  });
}
for (const [event, meaning] of [
  [
    {
      kind: "REMINDER_CHANGED",
      messageId: "timeline.event.reminderChanged",
      action: "Historical action",
    },
    "Reminder updated",
  ],
  [
    {
      kind: "REMINDER_COMPLETED",
      messageId: "timeline.event.reminderCompleted",
      action: "Historical action",
    },
    "Reminder completed",
  ],
  [
    {
      kind: "REMINDER_CANCELLED",
      messageId: "timeline.event.reminderCancelled",
      action: "Historical action",
    },
    "Reminder cancelled",
  ],
] as const) {
  it(`renders the Core-supplied historical action for ${event.kind}`, () => {
    const view = render(<TimelineEventView event={event} />);
    expect(view.container).toHaveTextContent(meaning);
    expect(view.container.querySelector("bdi")).toHaveTextContent("Historical action");
  });
}
it("isolates both authored actions in Arabic and keeps warning text and dates inert", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "ar",
      displayLocale: "ar-EG",
      mode: "dark",
      accent: "green",
    }),
  );
  const completedAction = "Call العربية\u202e";
  const nextAction = '<img src=x onerror="alert(1)">';
  const view = render(
    <TimelineEventView
      event={{
        kind: "REMINDER_COMPLETED_AND_PLANNED",
        messageId: "timeline.event.reminderCompletedAndPlanned",
        completedAction,
        nextAction,
        nextDue: "2026-11-05",
      }}
    />,
  );
  expect(screen.getByText(completedAction).tagName).toBe("BDI");
  expect(screen.getByText(nextAction).tagName).toBe("BDI");
  expect(view.container.querySelector("time")).toHaveAttribute("datetime", "2026-11-05");
  expect(view.container).toHaveTextContent("U+202E");
  expect(view.container.querySelector("img,script")).toBeNull();
  expect(view.container).not.toHaveTextContent("timeline.event.");
});
