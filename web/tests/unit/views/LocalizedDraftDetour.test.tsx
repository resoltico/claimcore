import { requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, fireEvent } from "../../support/presentation-test-support";
import { OperationEditor } from "../../../src/views/OperationEditor";
import { SettingsWorkspace } from "../../../src/views/SettingsWorkspace";
import { useRoute } from "../../../src/routing/useRoute";
import { navigate } from "../../../src/routing/navigation";
import { productMetadata } from "../../../src/protocol/productMetadata";
import { changeField } from "../../support/field-entry";
import { isRequest } from "@contracts/client";
const Workspace = () => {
  const route = useRoute();
  return (
    <SettingsWorkspace route={route}>
      {() => (
        <OperationEditor
          definition={productMetadata}
          current={null}
          initialAct="case.open"
          prefill={{
            "field:caseReference": "DETOUR-1",
            "field:incidentNotificationDate": "2026-11-09",
            "field:incidentCountry": "LV",
            "field:claimantName": "Synthetic claimant",
            "field:insurerName": "Synthetic insurer",
            "field:claimedCurrency": "EUR",
          }}
          onClose={vi.fn()}
          onCommitted={vi.fn()}
          onMutationLockChange={vi.fn()}
        />
      )}
    </SettingsWorkspace>
  );
};
afterEach(() => vi.unstubAllGlobals());
it.each([
  { raw: "1,234.50", edited: null, normalized: "1234.50" },
  { raw: "1,", edited: "1,25", normalized: "1.25" },
])(
  "preserves partial dates and authored amount $raw across settings, using the new locale only for subsequent edits",
  async ({ raw, edited, normalized }) => {
    const bodies: unknown[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) =>
        Promise.try(() => {
          bodies.push(JSON.parse(requestText(options?.body)));
          return new Response("", { status: 503 });
        }),
      ),
    );
    navigate("/cases/new", true);
    render(<Workspace />);
    const date = () => document.querySelector<HTMLElement>('[data-field-name="incidentDate"]')!;
    const month = date().querySelector<HTMLElement>('[data-type="month"]')!;
    month.focus();
    fireEvent(
      month,
      new InputEvent("beforeinput", {
        data: "8",
        inputType: "insertText",
        bubbles: true,
        cancelable: true,
      }),
    );
    await waitFor(() => {
      expect(date().querySelector('[data-type="month"]')).toHaveAttribute("aria-valuenow", "8");
    });
    fireEvent.change(screen.getByRole("textbox", { name: "Claimed amount" }), {
      target: { value: raw },
    });
    await userEvent.click(screen.getByRole("button", { name: "Settings" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    await userEvent.click(screen.getByRole("combobox", { name: "Date and number format" }));
    await userEvent.click(screen.getByRole("option", { name: "Latviešu — Latvija" }));
    await userEvent.click(screen.getByRole("button", { name: "Return to workspace" }));
    expect(screen.getByRole("textbox", { name: "Claimed amount" })).toHaveValue(raw);
    expect(date().querySelector('[data-type="month"]')).toHaveAttribute("aria-valuenow", "8");
    expect(date().querySelector('[data-type="year"]')).toHaveAttribute("data-placeholder", "true");
    expect(date().querySelector('[data-type="day"]')).toHaveAttribute("data-placeholder", "true");
    expect(bodies).toEqual([]);
    if (edited !== null) {
      fireEvent.change(screen.getByRole("textbox", { name: "Claimed amount" }), {
        target: { value: edited },
      });
    }
    changeField("Incident date", "2026-08-17");
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(isRequest("operation.execute", bodies[0])).toBe(true);
    expect(bodies[0]).toMatchObject({
      kind: "CASE",
      act: {
        kind: "case.open",
        values: {
          incidentDate: "2026-08-17",
          claimedAmount: normalized,
          claimedCurrency: "EUR",
        },
      },
    });
  },
);
