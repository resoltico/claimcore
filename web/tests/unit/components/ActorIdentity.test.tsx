import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { ActorIdentity, RecordedTime } from "../../../src/components/ActorIdentity";
import { actorRefInput } from "../../support/inputs";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { context } from "../../support/service-ui.fixtures";
import { preferenceKey } from "../../../src/presentation/preferences";
it.each([
  ["en", "Former account"],
  ["lv", "Bijušais konts"],
  ["ar", "حساب سابق"],
] as const)(
  "uses the core erased-name label in %s without a principal lookup",
  (language, label) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language,
        displayLocale: "en-GB",
        mode: "system",
        accent: "green",
      }),
    );
    const actor = actorRefInput({ displayName: { kind: "ERASED" } });
    render(<ActorIdentity actor={actor} />);
    const name = screen.getByText((text) => text.includes(label));
    expect(name).toHaveTextContent(actor.actorId.slice(0, 8));
    expect(name.tagName).toBe("BDI");
    expect(screen.queryByRole("link")).toBeNull();
  },
);
it.each(["AVAILABLE", "DISABLED", "NOT_ELIGIBLE"] as const)(
  "preserves an authored name unchanged when availability is %s",
  (availability) => {
    const name = "A\u0308 العربية\u200D <tag>";
    const view = render(
      <ActorIdentity
        actor={actorRefInput({
          actorId: context.actorId,
          displayName: { kind: "NAMED", value: name },
          availability,
        })}
      />,
    );
    expect(view.container.querySelector(".actor-identity > bdi")?.textContent).toBe(name);
    expect(screen.queryByText("You")).toBeNull();
  },
);
it("formats attribution time in the requester’s time zone and preserves its exact instant", () => {
  const view = render(
    <RequesterContext
      value={{ ...context, capabilities: ["READ_AUTHORITY"], timeZone: "Europe/Riga" }}
    >
      <RecordedTime value="2026-01-01T22:30:00Z" />
    </RequesterContext>,
  );
  expect(view.container.querySelector("time")).toHaveTextContent("2 Jan 2026, 00:30");
  expect(view.container.querySelector("time")).toHaveAttribute("title", "2026-01-01T22:30:00Z");
});
