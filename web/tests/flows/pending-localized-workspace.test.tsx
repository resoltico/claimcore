import { cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import {
  render,
  screen,
  testWorkspaceIdentity,
  waitFor,
} from "../support/presentation-test-support";
import { defaults, savePreferences } from "../../src/presentation/preferences";
import { UnresolvedNotice } from "../../src/features/operation/UnresolvedNotice";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { service } from "../../src/api/service";
import { operationId } from "../../src/operation/operationId";
import { pendingLookup } from "../support/operation-lookup.fixtures";
import { recoveryRecord, testJournal } from "../support/recovery.fixtures";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

it.each(["ar", "en-XA"] as const)(
  "renders the actual pending recovery screen in %s with a semantic deadline and accessible controls",
  async (language) => {
    // Suspend the periodic poll so the second lookup can only come from the keyboard action.
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    expect(savePreferences({ ...defaults, language, displayLocale: "ar-EG" }).kind).toBe(
      "available",
    );
    const record = { ...recoveryRecord(), operationId: operationId() };
    await testJournal.put(record);
    const lookup = vi
      .spyOn(service, "operationGet")
      .mockResolvedValue(pendingLookup(record.operationId));
    render(
      <RecoveryProvider identity={testWorkspaceIdentity}>
        <UnresolvedNotice showEmpty />
      </RecoveryProvider>,
    );
    const deadline = await waitFor(() => {
      const time = document.querySelector('time[datetime="2026-11-07T10:10:00.000000Z"]');
      expect(time).not.toBeNull();
      return time!;
    });
    expect(deadline).toBeVisible();
    expect(deadline.textContent).toContain("٢٠٢٦");
    expect(deadline.closest('[role="alert"]')).toBeNull();
    expect(deadline.closest('[role="status"]')).not.toBeNull();
    expect(document.documentElement.dir).toBe(language === "ar" ? "rtl" : "ltr");
    expect(lookup).toHaveBeenCalledOnce();
    const controls = screen.getAllByRole("button");
    expect(controls.length).toBeGreaterThan(0);
    for (const button of controls) {
      expect(button).toHaveAccessibleName();
    }
    const user = userEvent.setup();
    await user.tab();
    expect(controls[0]).toHaveFocus();
    await waitFor(() => {
      expect(controls[0]).not.toBeDisabled();
    });
    await user.keyboard("{Enter}");
    expect(controls[0]).toBeDisabled();
    // An explicit check waits for the pending response's retry delay before sending.
    await waitFor(
      () => {
        expect(lookup).toHaveBeenCalledTimes(2);
      },
      { timeout: 3000 },
    );
    expect(lookup.mock.calls[1]![1]).toEqual(lookup.mock.calls[0]![1]);
    expect(document.body).not.toHaveTextContent("label.operationPending");
    expect(document.body).not.toHaveTextContent("diagnostic.");
    if (language === "ar") {
      expect(document.body).toHaveTextContent("التحقق");
    } else {
      expect(document.body.textContent).toContain("Chëëckïïng");
    }
  },
);
