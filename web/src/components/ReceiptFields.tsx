import type { CaseReceipt, CatalogDocument } from "../api/service";
import { usePresentation } from "../presentation/context";
import { CaseFieldsView } from "./CaseFieldsView";

export const ReceiptFields = ({
  subject,
  fields,
  context,
}: {
  subject: CaseReceipt["subject"];
  fields: CatalogDocument["fields"];
  context: string;
}) => {
  const p = usePresentation();
  switch (subject.snapshot.kind) {
    case "WITHHELD":
      return <p>{p.text("ui.snapshotWithheld")}</p>;
    case "ERASED":
      return <p>{p.text("lifecycle.ERASED.meaning")}</p>;
    case "DISCLOSED":
      return (
        <CaseFieldsView
          values={subject.snapshot.view.fields}
          revision={subject.revision}
          fields={fields}
          context={context}
        />
      );
  }
};
