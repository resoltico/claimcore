import { localNotice } from "../../src/protocol/notices";
import { render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { isEndpointId } from "../../src/protocol/client";
import { CopyValue } from "../../src/components/CopyValue";
import { CaseFieldsView } from "../../src/components/CaseFieldsView";
import { CatalogField } from "../../src/components/CatalogField";
import { LabeledTextInput } from "../../src/components/LabeledTextInput";
import { hasSuspiciousCharacters, suspiciousCodePoints } from "../../src/utils/text";
import { definition, fields } from "../support/service-ui.fixtures";

it("copies safe values and provides an accessible fallback when clipboard access is unavailable", async () => {
  const user = userEvent.setup();
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
  const view = render(<CopyValue label="reference" value="CASE-1" />);
  await user.click(screen.getByRole("button", { name: "Copy reference" }));
  expect(writeText).toHaveBeenCalledWith("CASE-1");
  expect(screen.getByText("reference copied.")).toBeVisible();
  writeText.mockRejectedValueOnce(new Error("denied"));
  await user.click(screen.getByRole("button", { name: "Copy reference" }));
  expect(screen.getByLabelText("reference copy fallback")).toHaveValue("CASE-1");
  view.unmount();
});

it("associates optional field errors and supports password text input", () => {
  const onChange = vi.fn();
  render(
    <>
      <LabeledTextInput
        id="credential"
        label="Credential"
        value="secret"
        onChange={onChange}
        type="password"
        error="Required"
      />
      <CatalogField
        field={definition.catalog.fields[0]!}
        value="CASE-1"
        error={localNotice("unreachable")}
        onChange={onChange}
      />
    </>,
  );
  expect(screen.getByLabelText("Credential")).toHaveAttribute("type", "password");
  expect(screen.getAllByText(/Required|could not be reached/u)).toHaveLength(2);
});

it("renders ordinary text input without an error and forwards edits", async () => {
  const user = userEvent.setup();
  const onChange = vi.fn();
  render(<LabeledTextInput id="reference" label="Reference" value="" onChange={onChange} />);
  const input = screen.getByRole("textbox", { name: "Reference" });
  expect(input).toHaveAttribute("type", "text");
  expect(screen.queryByRole("alert")).toBeNull();
  await user.type(input, "A");
  expect(onChange).toHaveBeenCalledWith("A");
});

it("keeps generated endpoint inventory callable and presents suspicious Unicode defensively", () => {
  expect(isEndpointId("operation.preview")).toBe(true);
  expect(isEndpointId("nope")).toBe(false);
  expect(hasSuspiciousCharacters("plain")).toBe(false);
  expect(hasSuspiciousCharacters("a\u202Eb")).toBe(true);
  expect(suspiciousCodePoints("a\u202Eb")).toEqual(["U+202E"]);
  expect(suspiciousCodePoints("\u0085\u00AD\u2028\u2029")).toEqual([
    "U+85",
    "U+AD",
    "U+2028",
    "U+2029",
  ]);
  expect(suspiciousCodePoints("العربية\u200D")).toEqual(["U+200D"]);
});

it("renders absent and suspicious accepted values without changing the recorded text", () => {
  render(
    <CaseFieldsView
      values={{ ...fields, caseReference: "A\u202E", payment: null }}
      revision={1}
      fields={definition.catalog.fields}
      context="synthetic current case"
    />,
  );
  expect(screen.getAllByText("Not recorded")).toHaveLength(4);
  expect(screen.getByText("Contains U+202E")).toBeVisible();
});

it("gives simultaneous current and historical case fields distinct heading IDs", () => {
  const snapshot = { fields, revision: 1 };
  render(
    <>
      <CaseFieldsView
        values={snapshot.fields}
        revision={snapshot.revision}
        fields={definition.catalog.fields}
        context="first snapshot"
      />
      <CaseFieldsView
        values={snapshot.fields}
        revision={snapshot.revision}
        fields={definition.catalog.fields}
        context="second snapshot"
      />
    </>,
  );
  const sections = screen.getAllByRole("region", { name: /snapshot/u });
  expect(sections).toHaveLength(2);
  const headings = sections.map((section) => section.getAttribute("aria-labelledby"));
  expect(headings[0]).not.toBe(headings[1]);
  for (const id of headings) {
    expect(document.getElementById(id ?? "")?.textContent).toContain("snapshot");
  }
});
