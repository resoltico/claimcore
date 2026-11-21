import {
  announce,
  clearAnnouncer,
  destroyAnnouncer,
} from "react-aria/private/live-announcer/LiveAnnouncer";
import { expect, it, vi } from "vitest";

it("clears both React Aria announcement logs without assigning innerHTML", () => {
  vi.useFakeTimers();
  const html = vi.spyOn(Element.prototype, "innerHTML", "set");

  try {
    announce("Important update", "assertive");
    announce("Routine update", "polite");
    vi.advanceTimersByTime(100);

    const assertive = document.querySelector('[data-live-announcer] [aria-live="assertive"]');
    const polite = document.querySelector('[data-live-announcer] [aria-live="polite"]');
    expect(assertive).toHaveTextContent("Important update");
    expect(polite).toHaveTextContent("Routine update");

    clearAnnouncer("assertive");
    expect(assertive?.childNodes.length).toBe(0);
    expect(polite).toHaveTextContent("Routine update");

    clearAnnouncer("polite");
    expect(polite?.childNodes.length).toBe(0);
    expect(html).not.toHaveBeenCalled();
  } finally {
    destroyAnnouncer();
    html.mockRestore();
    vi.useRealTimers();
  }
});
