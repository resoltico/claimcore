import { recoveryOptions } from "../support/recovery.fixtures";
import { chooseRoleForReview } from "../support/people-input.fixtures";
import { chooseOption } from "../support/choice-entry";
import { coreCatalogs, chromeCatalogs } from "../support/authored-catalogs";
import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { service } from "../../src/api/service";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { preferenceKey } from "../../src/presentation/preferences";
import { useRef } from "react";
import { OperationFormActions, OperationForm } from "../../src/views/operation/OperationForm";
import { render, screen, waitFor } from "../support/presentation-test-support";
const enField = { ...coreCatalogs.en, ...chromeCatalogs.en };
const lvField = { ...coreCatalogs.lv, ...chromeCatalogs.lv };
const arField = { ...coreCatalogs.ar, ...chromeCatalogs.ar };
const locales = [
  {
    language: "en",
    label: "Case reference",
    field: enField,
    ui: enField,
    people: enField,
    diagnostic: enField,
  },
  {
    language: "lv",
    label: "Lietas atsauce",
    field: lvField,
    ui: lvField,
    people: lvField,
    diagnostic: lvField,
  },
  {
    language: "ar",
    label: "مرجع الملف",
    field: arField,
    ui: arField,
    people: arField,
    diagnostic: arField,
  },
] as const;
type Locale = (typeof locales)[number];
const noLockChange = () => undefined;
const PeopleForm = ({ kind }: { kind: "people.grant" | "people.revoke" }) => {
  const model = useOperation({
    ...recoveryOptions,
    subject: "PEOPLE",
    kind,
    current: null,
    credential: "synthetic-token",
    onLockChange: noLockChange,
  });
  const preview = useRef<HTMLButtonElement>(null);
  return (
    <>
      <OperationForm
        model={model}
        current={null}
        offered={[kind]}
        onAct={() => undefined}
        formId="grant-form"
      />
      <OperationFormActions
        model={model}
        current={null}
        offered={[kind]}
        formId="grant-form"
        preview={preview}
      />
    </>
  );
};
const rejectedPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "INPUT",
            advice: {
              message: {
                messageId: "diagnostic.INVALID_INPUT",
                arguments: [{ key: "issueCount", value: { kind: "COUNT", value: 1 } }],
              },
              action: "CORRECT_INPUT",
            },
            issues: [
              {
                input: "field:caseReference",
                row: null,
                related: null,
                message: { messageId: "diagnostic.REFERENCE_FORMAT.gui", arguments: [] },
              },
            ],
          },
        },
      },
    }),
  );
const authorCaseGrant = async (user: ReturnType<typeof userEvent.setup>, locale: Locale) => {
  await user.type(
    screen.getByRole("textbox", {
      name: locale.people["field.principal.issuer.label"],
    }),
    "https://synthetic.example/",
  );
  await user.type(
    screen.getByRole("textbox", {
      name: locale.people["field.principal.subject.label"],
    }),
    "synthetic-target",
  );
  await chooseOption(
    user,
    screen.getByRole("group", { name: locale.people["input.scope.label"] }),
    "CASE",
  );
  const reference = screen.getByRole("textbox", {
    name: locale.label,
  });
  await user.type(reference, "bad reference");
  return reference;
};
const expectReferenceDescription = (reference: HTMLElement, locale: Locale, invalid: boolean) => {
  const hint = locale.field["field.caseReference.hint"];
  const description = reference
    .getAttribute("aria-describedby")!
    .split(" ")
    .map((id) => {
      const node = document.getElementById(id);
      expect(node).not.toBeNull();
      return node!.textContent;
    });
  expect(description.join(" ")).toContain(hint);
  const expected = invalid
    ? `${hint} ${locale.diagnostic["diagnostic.REFERENCE_FORMAT.gui"]}`
    : hint;
  expect(reference).toHaveAccessibleDescription(expected);
  expect(reference).toHaveAccessibleName(locale.label);
};
afterEach(() => vi.restoreAllMocks());
const referencePreferences = (language: string) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language,
      displayLocale: "en-GB",
      mode: "system",
      accent: "green" as const,
    }),
  );
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
    referencePreferences(locale.language);
    render(<PeopleForm kind={active ? "people.grant" : "people.revoke"} />);
    expect(
      screen.queryByRole("textbox", {
        name: locale.label,
      }),
    ).toBeNull();
    const reference = await authorCaseGrant(user, locale);
    expect(reference).toHaveAttribute("name", "field:caseReference");
    expectReferenceDescription(reference, locale, false);
    chooseRoleForReview();
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
      kind: "PEOPLE",
      target: {
        issuer: "https://synthetic.example/",
        subject: "synthetic-target",
      },
      act: {
        kind: active ? "people.grant" : "people.revoke",
        scope: {
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: "bad reference" },
        },
      },
    });
    expect(reference).toHaveAttribute("aria-invalid", "true");
    expectReferenceDescription(reference, locale, true);
    await user.click(within(screen.getByRole("alert")).getByRole("link"));
    expect(reference).toHaveFocus();
  },
);
