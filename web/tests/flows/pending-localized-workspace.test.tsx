import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import {
  render,
  screen,
  testWorkspaceIdentity,
  waitFor,
} from "../support/presentation-test-support";
import { defaults, savePreferences } from "../../src/presentation/preferences";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { service } from "../../src/api/service";
import { pendingLookup } from "../support/operation-lookup.fixtures";
import { recoveryRecord, testJournal } from "../support/recovery.fixtures";

it.each(["ar", "en-XA"] as const)(
  "renders the actual pending recovery screen in %s with a semantic deadline and accessible controls",
  async (language) => {
    expect(savePreferences({ ...defaults, language, displayLocale: "ar-EG" })).toBe(true);
    const record = recoveryRecord();
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
    await waitFor(() => {
      expect(lookup).toHaveBeenCalledTimes(2);
    });
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
