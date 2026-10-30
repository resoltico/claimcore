import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { ActorIdentity, RecordedTime } from "../../../src/components/ActorIdentity";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { context } from "../../support/service-ui.fixtures";
import { preferenceKey } from "../../../src/presentation/preferences";
it.each([
  ["en", "You"],
  ["lv", "Jūs"],
  ["ar", "أنت"],
])(
  "localizes requester attribution in %s without an authority link for readers",
  (language, label) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 2, language, displayLocale: "en-GB", theme: "SYSTEM" }),
    );
    render(<ActorIdentity actorId={context.actorId} />);
    expect(screen.getByText(label)).toBeVisible();
    expect(screen.queryByRole("link")).toBeNull();
  },
);
it("links short IDs only for authority readers and formats time in the installation zone", () => {
  const view = render(
    <RequesterContext
      value={{ ...context, capabilities: ["READ_AUTHORITY"], timeZone: "Europe/Riga" }}
    >
      <ActorIdentity actorId="20000000-0000-4000-8000-000000000002" />
      <RecordedTime value="2026-01-01T22:30:00Z" />
    </RequesterContext>,
  );
  expect(screen.getByRole("link", { name: "20000000" })).toHaveAttribute(
    "href",
    "/authority/20000000-0000-4000-8000-000000000002",
  );
  expect(view.container.querySelector("time")).toHaveTextContent("2 Jan 2026, 00:30");
  expect(view.container.querySelector("time")).toHaveAttribute("title", "2026-01-01T22:30:00Z");
});
