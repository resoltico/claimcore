import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import enField from "@locales/en.field.json";
import lvField from "@locales/lv.field.json";
import arField from "@locales/ar.field.json";
import enUi from "@locales/en.ui.json";
import lvUi from "@locales/lv.ui.json";
import arUi from "@locales/ar.ui.json";
import enAuthority from "@locales/en.authority.json";
import lvAuthority from "@locales/lv.authority.json";
import arAuthority from "@locales/ar.authority.json";
import enDiagnostic from "@locales/en.diagnostic.json";
import lvDiagnostic from "@locales/lv.diagnostic.json";
import arDiagnostic from "@locales/ar.diagnostic.json";
import { service } from "../src/api/service";
import { useOperation } from "../src/hooks/operation/useOperation";
import { initialOperationValues, refusalPath } from "../src/interaction/operationForm";
import { preferenceKey } from "../src/presentation/preferences";
import { OperationForm } from "../src/views/operation/OperationForm";
import { render, screen, waitFor } from "./presentation-test-support";

const locales = [
  {
    language: "en",
    label: "Handler's case reference",
    field: enField,
    ui: enUi,
    authority: enAuthority,
    diagnostic: enDiagnostic,
  },
  {
    language: "lv",
    label: "Administratora lietas numurs",
    field: lvField,
    ui: lvUi,
    authority: lvAuthority,
    diagnostic: lvDiagnostic,
  },
  {
    language: "ar",
    label: "مرجع الملف لدى المعالج",
    field: arField,
    ui: arUi,
    authority: arAuthority,
    diagnostic: arDiagnostic,
  },
] as const;
type Locale = (typeof locales)[number];
const noLockChange = () => undefined;
const AuthorityForm = () => {
  const model = useOperation({
    scope: "AUTHORITY",
    kind: "SET_GRANT",
    current: null,
    token: "synthetic-token",
    onLockChange: noLockChange,
  });
  return (
    <OperationForm
      model={model}
      current={null}
      offered={["SET_GRANT"]}
      onCommand={() => undefined}
    />
  );
};
const rejectedPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: {
          tag: "REJECTED",
          data: {
            code: "INPUT_CASE_REFERENCE_FORMAT",
            action: "CORRECT_INPUT",
            target: "caseReference",
            actualRevision: null,
            limits: {},
          },
        },
      },
    }),
  );
const authorCaseGrant = async (
  user: ReturnType<typeof userEvent.setup>,
  locale: Locale,
  active: boolean,
) => {
  await user.type(
    screen.getByRole("textbox", { name: locale.authority["authority.field.issuer.label"] }),
    "https://synthetic.example/",
  );
  await user.type(
    screen.getByRole("textbox", { name: locale.authority["authority.field.subject.label"] }),
    "synthetic-target",
  );
  await user.selectOptions(
    within(
      screen.getByRole("group", { name: locale.authority["authority.field.scope.label"] }),
    ).getByRole("combobox"),
    "CASE",
  );
  const activeControl = screen.getByRole("checkbox", {
    name: locale.authority["authority.field.active.label"],
  });
  if (active) {
    await user.click(activeControl);
  }
  expect(activeControl).toHaveProperty("checked", active);
  const reference = screen.getByRole("textbox", { name: locale.label });
  await user.type(reference, "bad reference");
  return reference;
};
const expectReferenceDescription = (reference: HTMLElement, locale: Locale, invalid: boolean) => {
  const meaning = locale.field["field.caseReference.meaning"];
  const hint = locale.ui["ui.referenceHint"];
  const description = reference
    .getAttribute("aria-describedby")!
    .split(" ")
    .map((id) => {
      const node = document.getElementById(id);
      expect(node).not.toBeNull();
      return node!.textContent;
    });
  expect(description.join(" ")).toContain(meaning);
  expect(description.join(" ")).toContain(hint);
  const expected = invalid
    ? `${meaning}${hint} ${locale.diagnostic["diagnostic.INPUT_CASE_REFERENCE_FORMAT"]}`
    : `${meaning}${hint}`;
  expect(reference).toHaveAccessibleDescription(expected);
  expect(reference).toHaveAccessibleName(locale.label);
};

afterEach(() => vi.restoreAllMocks());

it.each(
  locales.flatMap((locale) =>
    [true, false].map((active) => ({ language: locale.language, locale, active })),
  ),
)(
  "binds and focuses a case-scoped grant reference in $language with active=$active [CC-WEB-001]",
  async ({ locale, active }) => {
    const user = userEvent.setup();
    const preview = rejectedPreview();
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language: locale.language, displayLocale: "en-GB" }),
    );
    const values = {
      ...initialOperationValues("AUTHORITY", "SET_GRANT", null),
      "act.active": active,
    };
    expect(
      refusalPath("caseReference", { ...values, "act.scope.kind": "INSTALLATION" }),
    ).toBeNull();
    expect(refusalPath("caseReference", { ...values, "act.scope.kind": "CASE" })).toBe(
      "act.scope.caseReference",
    );
    render(<AuthorityForm />);
    expect(screen.queryByRole("textbox", { name: locale.label })).toBeNull();
    const reference = await authorCaseGrant(user, locale, active);
    expect(reference).toHaveAttribute("name", "act.scope.caseReference");
    expectReferenceDescription(reference, locale, false);
    await user.click(screen.getByRole("button", { name: locale.ui["ui.previewOperation"] }));
    await waitFor(() => {
      expect(reference).toHaveFocus();
    });
    expect(preview).toHaveBeenCalledOnce();
    expect(preview.mock.calls[0]![0].input).toMatchObject({
      scope: "AUTHORITY",
      target: { issuer: "https://synthetic.example/", subject: "synthetic-target" },
      act: {
        kind: "SET_GRANT",
        active,
        scope: { kind: "CASE", caseReference: "bad reference" },
      },
    });
    expect(reference).toHaveAttribute("aria-invalid", "true");
    expectReferenceDescription(reference, locale, true);
  },
);
