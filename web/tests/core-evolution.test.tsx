import { expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import fixture from "@contracts/evolution-fixture.json" with { type: "json" };
import { ProtocolFields } from "../src/components/ProtocolFields";
import { PresentationContext } from "../src/presentation/context";
import { createPresenter } from "../src/presentation/presenter";
import { renderKey } from "../src/presentation/messages";
import { assembleBoundAct } from "../src/interaction/drafts";
import { formValue } from "../src/protocol/forms";
import { validateSchema } from "../src/protocol/schema";

const flatten = (value: unknown, path = ""): Record<string, string | boolean> => {
  if (typeof value === "object" && value !== null) {
    return Object.fromEntries(
      Object.entries(value).flatMap(([key, child]) =>
        Object.entries(flatten(child, path ? `${path}.${key}` : key)),
      ),
    );
  }
  return { [path]: typeof value === "boolean" ? value : String(value) };
};
const preferences = { language: "en", displayLocale: "en-GB" } as const;
const binding = fixture.bindings[0]!;
const presenter = createPresenter(preferences, fixture.descriptors);
const context = {
  ...presenter,
  changed: false,
  persistenceFailed: false,
  setPreferences: () => undefined,
};
const EvolvedForm = () => {
  const initial = flatten(fixture.request);
  for (const field of binding.fields) {
    initial[`act.${field.path.join(".")}`] = "";
  }
  const [values, setValues] = useState(initial);
  return (
    <PresentationContext.Provider value={context}>
      <form
        onSubmit={(event) => {
          event.preventDefault();
        }}
      >
        <ProtocolFields
          document={fixture.schema}
          node={fixture.schema}
          path=""
          values={values}
          disabled={false}
          onChange={(path, value) => {
            setValues((prior) => ({ ...prior, [path]: value }));
          }}
        />
      </form>
    </PresentationContext.Provider>
  );
};

it("renders emitted evolved inputs and assembles exact bytes admitted by the fixture codec", () => {
  const view = render(<EvolvedForm />);
  const authored: Readonly<Record<string, string>> = fixture.request.act.values;
  const values: Record<string, string> = {};
  for (const field of binding.fields) {
    const name = `act.${field.path.join(".")}`;
    const input = view.container.querySelector<HTMLInputElement>(`input[name="${name}"]`);
    expect(input, field.fieldName).not.toBeNull();
    fireEvent.change(input!, { target: { value: authored[field.fieldName] } });
    values[field.fieldName] = input!.value;
  }
  expect(screen.getByRole("textbox", { name: presenter.fieldLabel("reason") })).toHaveValue(
    authored["reason"],
  );
  const request = {
    operationId: fixture.request.operationId,
    caseReference: fixture.request.caseReference,
    expectedRevision: fixture.request.expectedRevision,
    act: assembleBoundAct(binding, values),
  };
  const collected = Object.fromEntries(
    Array.from(view.container.querySelectorAll<HTMLInputElement>("input")).map((input) => [
      input.name,
      input.value,
    ]),
  );
  expect(formValue(fixture.schema, fixture.schema, "", collected)).toEqual(request);
  expect(validateSchema(fixture.schema, request)).toBe(true);
  expect(JSON.stringify(request)).toBe(fixture.requestBytes);
});

it("English discovery and localized presentation follow declared alternate identities", () => {
  const command = fixture.descriptors.commands.find((candidate) => candidate.kind === "OPEN")!;
  expect(command.labelId).toBe("command.CLOSE.label");
  expect(command.label).toBe(createPresenter(preferences).commandLabel("CLOSE"));
  expect(fixture.descriptors.authorityTarget.labelId).toBe("authority.field.subject.label");
  for (const language of ["en", "lv", "ar"] as const) {
    const localizedPreferences = { language, displayLocale: "en-GB" } as const;
    const evolved = createPresenter(localizedPreferences, fixture.descriptors);
    const normal = createPresenter(localizedPreferences);
    expect(evolved.commandLabel("OPEN")).toBe(normal.commandLabel("CLOSE"));
    expect(evolved.commandLabel("OPEN")).not.toBe(normal.commandLabel("OPEN"));
    expect(evolved.commandMeaning("OPEN")).toBe(normal.commandMeaning("CLOSE"));
    expect(evolved.fieldLabel("incidentDate")).toBe(normal.fieldLabel("paymentDate"));
    expect(evolved.groupLabel("registration")).toBe(normal.groupLabel("payment"));
    expect(evolved.authorityLabel("SET_GRANT")).toBe(normal.authorityLabel("REGISTER_ACTOR"));
    expect(evolved.fieldLabel("role")).toBe(normal.fieldLabel("scope"));
    expect(evolved.fieldLabel("target")).toBe(normal.fieldLabel("subject"));
    expect(renderKey(localizedPreferences, command.confirmationId)).toBe(
      normal.text("ui.confirmExactRequest"),
    );
  }
});
