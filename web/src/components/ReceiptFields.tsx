import type { CaseReceipt, CatalogDocument } from "../api/service";
import { usePresentation } from "../presentation/context";
import { CaseFieldsView } from "./CaseFieldsView";

export const ReceiptFields = ({
  scope,
  fields,
  context,
}: {
  scope: CaseReceipt["scope"];
  fields: CatalogDocument["fields"];
  context: string;
}) => {
  const p = usePresentation();
  switch (scope.snapshot.disclosure) {
    case "WITHHELD":
      return <p>{p.text("ui.snapshotWithheld")}</p>;
    case "ERASED":
      return <p>{p.text("ui.lifecycleErased")}</p>;
    case "DISCLOSED":
      return (
        <CaseFieldsView
          values={scope.snapshot.view.fields}
          revision={scope.revision}
          fields={fields}
          context={context}
        />
      );
  }
};
