import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import { OperationLookup } from "./OperationLookup";
import { UnresolvedNotice } from "./operation/UnresolvedNotice";

export const RecoveryPanel = ({ initialOperationId }: { initialOperationId?: string }) => {
  const p = usePresentation();
  const operation = initialOperationId === "" ? undefined : initialOperationId;
  return (
    <section className="recovery-panel" aria-labelledby="recovery-title">
      <h1 id="recovery-title">{p.text("ui.recovery")}</h1>
      <p className="page-description">{p.text("ui.recoveryDescription")}</p>
      <UnresolvedNotice showEmpty />
      <details className="recovery-lookup" open={operation !== undefined}>
        <summary>{p.text("ui.generalOperationLookup")}</summary>
        <OperationLookup
          definition={productMetadata}
          {...(operation === undefined ? {} : { initialOperationId: operation })}
        />
      </details>
    </section>
  );
};
