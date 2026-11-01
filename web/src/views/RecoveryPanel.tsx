import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import { OperationLookup } from "./OperationLookup";
import { UnresolvedNotice } from "./operation/UnresolvedNotice";

export const RecoveryPanel = ({ initialOperationId }: { initialOperationId?: string }) => {
  const p = usePresentation();
  const operation = initialOperationId === "" ? undefined : initialOperationId;
  return (
    <section aria-labelledby="recovery-title">
      <h1 id="recovery-title">{p.text("ui.recovery")}</h1>
      <p>{p.text("ui.recoveryDescription")}</p>
      <UnresolvedNotice showEmpty />
      <details open={operation !== undefined}>
        <summary>{p.text("ui.generalOperationLookup")}</summary>
        <OperationLookup
          definition={productMetadata}
          {...(operation === undefined ? {} : { initialOperationId: operation })}
        />
      </details>
    </section>
  );
};
