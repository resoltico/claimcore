import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { renderIsolatedValue } from "../presentation/messages";
import type { MessageArgs } from "../presentation/types";

type ReferenceMessage = {
  [K in keyof MessageArgs]: MessageArgs[K] extends { readonly reference: string } ? K : never;
}[keyof MessageArgs];

export const ReferenceSummary = <K extends ReferenceMessage>({
  id,
  values,
}: {
  id: K;
  values: MessageArgs[K];
}) => {
  const p = usePresentation();
  const reference = <bdi key="reference">{values.reference}</bdi>;
  return (
    <span>
      {renderIsolatedValue(p, id, values, reference)}
      <CharacterWarning value={values.reference} />
    </span>
  );
};
