import { localNotice } from "../src/api/notices";
import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { isWebV2EndpointId } from "../src/generated/convergence/web-v2.endpoint-catalog";
import { CopyValue } from "../src/components/CopyValue";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { DescriptorField } from "../src/components/DescriptorField";
import { TextInput } from "../src/components/TextInput";
import { hasSuspiciousCharacters, suspiciousCodePoints } from "../src/utils/text";
import { definition, fields } from "./v2-ui.fixtures";

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
      <TextInput
        id="credential"
        label="Credential"
        value="secret"
        onChange={onChange}
        type="password"
        error="Required"
      />
      <DescriptorField
        field={definition.definition.fields[0]!}
        value="CASE-1"
        error={localNotice("unreachable")}
        onChange={onChange}
      />
    </>,
  );
  expect(screen.getByLabelText("Credential")).toHaveAttribute("type", "password");
  expect(screen.getAllByText(/Required|could not be reached/u)).toHaveLength(2);
});

it("keeps generated endpoint inventory callable and presents suspicious Unicode defensively", () => {
  expect(isWebV2EndpointId("command.prepare")).toBe(true);
  expect(isWebV2EndpointId("nope")).toBe(false);
  expect(hasSuspiciousCharacters("plain")).toBe(false);
  expect(hasSuspiciousCharacters("a\u202Eb")).toBe(true);
  expect(suspiciousCodePoints("a\u202Eb")).toEqual(["U+202E"]);
});

it("renders absent and suspicious accepted values without changing the recorded text", () => {
  render(
    <CaseFieldsView
      caseView={{
        fields: { ...fields, caseReference: "A\u202E", paymentDate: null },
        revision: "1",
      }}
      fields={definition.definition.fields}
      context="synthetic current case"
    />,
  );
  expect(screen.getAllByText("Not recorded")).toHaveLength(4);
  expect(screen.getByText("Contains U+202E")).toBeVisible();
});

it("gives simultaneous current and historical case fields distinct heading IDs", () => {
  const snapshot = { fields, revision: "1" };
  render(
    <>
      <CaseFieldsView
        caseView={snapshot}
        fields={definition.definition.fields}
        context="first snapshot"
      />
      <CaseFieldsView
        caseView={snapshot}
        fields={definition.definition.fields}
        context="second snapshot"
      />
    </>,
  );
  const sections = screen.getAllByRole("region", { name: /Case fields for/u });
  expect(sections).toHaveLength(2);
  const headings = sections.map((section) => section.getAttribute("aria-labelledby"));
  expect(headings[0]).not.toBe(headings[1]);
  for (const id of headings)
    expect(document.getElementById(id ?? "")?.textContent).toContain("Case fields");
});
