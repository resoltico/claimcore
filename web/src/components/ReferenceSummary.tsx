import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { renderReference } from "../presentation/messages";
import type { MessageArgs } from "../presentation/types";

type ReferenceMessage =
  | "ui.reference"
  | "ui.reviewTarget"
  | "ui.recoverySummary"
  | "ui.retainedCommand"
  | "ui.importTarget";

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
      {renderReference(p, id, values, reference)}
      <CharacterWarning value={values.reference} />
    </span>
  );
};
