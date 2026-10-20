import { usePresentation } from "../presentation/context";
import type { MessageKey } from "../presentation/types";
export const PageStatus = ({
  empty,
  cursor,
  emptyMessage,
}: {
  empty: boolean;
  cursor: string | null;
  emptyMessage: Extract<
    MessageKey,
    "ui.noAccessibleCases" | "ui.noAccessibleActors" | "ui.noAccessibleHistory"
  >;
}) => {
  const p = usePresentation();
  return (
    <>
      {empty ? <p>{p.text(emptyMessage)}</p> : null}
      <p>{p.text(cursor === null ? "ui.pageEnd" : "ui.pagePartial")}</p>
    </>
  );
};
