import type { FormValues } from "../protocol/forms";

type ConstrainedInput = {
  readonly choicesWhen: ReadonlyArray<{
    readonly messageId?: string | null;
    readonly input: string;
    readonly value: string;
    readonly choices: ReadonlyArray<string>;
  }>;
};
/** Every matching Core constraint applies; this renderer supplies no additional admission policy. */
export const inputChoiceRestriction = (
  input: ConstrainedInput,
  choice: string,
  values: FormValues,
) =>
  input.choicesWhen.find(
    (rule) => values[rule.input] === rule.value && !rule.choices.includes(choice),
  );
export const inputChoiceAllowed = (
  input: ConstrainedInput,
  choice: string,
  values: FormValues,
): boolean => inputChoiceRestriction(input, choice, values) === undefined;
