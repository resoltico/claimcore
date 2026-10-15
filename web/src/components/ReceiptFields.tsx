import type { CaseReceipt, FieldDescriptor } from "../api/service";
import { usePresentation } from "../presentation/context";
import { CaseFieldsView } from "./CaseFieldsView";

export const ReceiptFields = ({
  receipt,
  fields,
  context,
}: {
  receipt: CaseReceipt;
  fields: ReadonlyArray<FieldDescriptor>;
  context: string;
}) => {
  const p = usePresentation();
  switch (receipt.snapshot.tag) {
    case "WITHHELD":
      return <p>{p.text("ui.snapshotWithheld")}</p>;
    case "ERASED":
      return <p>{p.text("ui.lifecycleErased")}</p>;
    case "DISCLOSED":
      return (
        <CaseFieldsView
          values={receipt.snapshot.state.fields}
          revision={receipt.revision}
          fields={fields}
          context={context}
        />
      );
  }
};
