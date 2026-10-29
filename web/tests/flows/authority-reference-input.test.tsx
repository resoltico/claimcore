import { chooseOption } from "../support/choice-entry";
import { catalogs as coreCatalogs } from "@contracts/messages";
import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import enChrome from "@locales/en.json";
import lvChrome from "@locales/lv.json";
import arChrome from "@locales/ar.json";
import { service } from "../../src/api/service";
import { useOperation } from "../../src/hooks/operation/useOperation";
import {
  initialOperationValues,
  refusalPath,
  operationEntry,
} from "../../src/operation/operationForm";
import { preferenceKey } from "../../src/presentation/preferences";
import { OperationForm } from "../../src/views/operation/OperationForm";
import { render, screen, waitFor } from "../support/presentation-test-support";
const enField = { ...coreCatalogs.en, ...enChrome };
const lvField = { ...coreCatalogs.lv, ...lvChrome };
const arField = { ...coreCatalogs.ar, ...arChrome };
const locales = [
  {
    language: "en",
    label: "Handler's case reference",
    field: enField,
    ui: enField,
    authority: enField,
    diagnostic: enField,
  },
  {
    language: "lv",
    label: "Administratora lietas numurs",
    field: lvField,
    ui: lvField,
    authority: lvField,
    diagnostic: lvField,
  },
  {
    language: "ar",
    label: "مرجع الملف لدى المعالج",
    field: arField,
    ui: arField,
    authority: arField,
    diagnostic: arField,
  },
] as const;
type Locale = (typeof locales)[number];
const noLockChange = () => undefined;
const AuthorityForm = ({ kind }: { kind: "GRANT" | "REVOKE" }) => {
  const model = useOperation({
    subject: "AUTHORITY",
    kind,
    current: null,
    credential: "synthetic-token",
    onLockChange: noLockChange,
  });
  return <OperationForm model={model} current={null} offered={[kind]} onAct={() => undefined} />;
};
const rejectedPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REJECTED",
          data: {
            code: "INVALID_INPUT",
            action: "CORRECT_INPUT",
            issues: [{ field: "caseReference", code: "REFERENCE_FORMAT" }],
          },
        },
      },
    }),
  );
const authorCaseGrant = async (user: ReturnType<typeof userEvent.setup>, locale: Locale) => {
  await user.type(
    screen.getByRole("textbox", {
      name: locale.authority["field.principal.issuer.label"],
    }),
    "https://synthetic.example/",
  );
  await user.type(
    screen.getByRole("textbox", {
      name: locale.authority["field.principal.subject.label"],
    }),
    "synthetic-target",
  );
  await chooseOption(
    user,
    within(
      screen.getByRole("group", {
        name: locale.authority["input.scope.label"],
      }),
    ).getByRole("radiogroup"),
    "CASE",
  );
  const reference = screen.getByRole("textbox", {
    name: locale.label,
  });
  await user.type(reference, "bad reference");
  return reference;
};
const expectReferenceDescription = (reference: HTMLElement, locale: Locale, invalid: boolean) => {
  const meaning = locale.field["field.caseReference.meaning"];
  const hint = locale.ui["scalar.reference.guidance"].replace(
    "{maximumCharacters}",
    locale.language === "ar" ? "\u206880\u2069" : "80",
  );
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
    ? `${meaning}${hint} ${locale.diagnostic["diagnostic.REFERENCE_FORMAT"]}`
    : `${meaning}${hint}`;
  expect(reference).toHaveAccessibleDescription(expected);
  expect(reference).toHaveAccessibleName(locale.label);
};
afterEach(() => vi.restoreAllMocks());
const expectGrantReferencePath = (active: boolean) => {
  const values = {
    ...initialOperationValues("AUTHORITY", active ? "GRANT" : "REVOKE", null),
  };
  expect(
    refusalPath(
      "caseReference",
      operationEntry({ subject: "AUTHORITY", kind: active ? "GRANT" : "REVOKE" }),
      {
        ...values,
        "act.scope.kind": "INSTALLATION",
      },
    ),
  ).toBeNull();
  expect(
    refusalPath(
      "caseReference",
      operationEntry({ subject: "AUTHORITY", kind: active ? "GRANT" : "REVOKE" }),
      {
        ...values,
        "act.scope.kind": "CASE",
      },
    ),
  ).toBe("act.scope.caseReference");
};
it.each(
  locales.flatMap((locale) =>
    [true, false].map((active) => ({
      language: locale.language,
      locale,
      active,
    })),
  ),
)(
  "binds and focuses a case-scoped grant reference in $language with active=$active",
  async ({ locale, active }) => {
    const user = userEvent.setup();
    const preview = rejectedPreview();
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 2,
        language: locale.language,
        displayLocale: "en-GB",
        theme: "SYSTEM" as const,
      }),
    );
    expectGrantReferencePath(active);
    render(<AuthorityForm kind={active ? "GRANT" : "REVOKE"} />);
    expect(
      screen.queryByRole("textbox", {
        name: locale.label,
      }),
    ).toBeNull();
    const reference = await authorCaseGrant(user, locale);
    expect(reference).toHaveAttribute("name", "act.scope.caseReference");
    expectReferenceDescription(reference, locale, false);
    await user.click(
      screen.getByRole("button", {
        name: locale.ui["label.reviewTitle"],
      }),
    );
    await waitFor(() => {
      expect(screen.getByRole("alert")).toHaveFocus();
    });
    expect(preview).toHaveBeenCalledOnce();
    expect(preview.mock.calls[0]![0].input).toMatchObject({
      kind: "AUTHORITY",
      target: {
        issuer: "https://synthetic.example/",
        subject: "synthetic-target",
      },
      act: {
        kind: active ? "GRANT" : "REVOKE",
        scope: {
          kind: "CASE",
          caseReference: "bad reference",
        },
      },
    });
    expect(reference).toHaveAttribute("aria-invalid", "true");
    expectReferenceDescription(reference, locale, true);
    await user.click(within(screen.getByRole("alert")).getByRole("link"));
    expect(reference).toHaveFocus();
  },
);
