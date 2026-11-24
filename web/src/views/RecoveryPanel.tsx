import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { Page } from "../ui/Page";
import { Section } from "../ui/Section";
import { usePresentation } from "../presentation/context";
import { productMetadata } from "../protocol/productMetadata";
import { OperationLookup } from "./OperationLookup";
import { UnresolvedNotice } from "./operation/UnresolvedNotice";

export const RecoveryPanel = ({ initialOperationId }: { initialOperationId?: string }) => {
  const p = usePresentation();
  useDocumentTitle(p.text("ui.recovery"));
  const operation = initialOperationId === "" ? undefined : initialOperationId;
  return (
    <div className="recovery-panel">
      <Page id="recovery-title" title={p.text("ui.recovery")}>
        <p className="page-description">
          {p.text("ui.recoveryDescription", {
            window: p.text("label.operationWindow", {
              minutes: p.integer(
                productMetadata.catalog.limits.operationWindowMilliseconds / 60000,
              ),
            }),
          })}
        </p>
        <UnresolvedNotice showEmpty />
        <Section id="recovery-lookup" title={p.text("ui.generalOperationLookup")}>
          <OperationLookup
            definition={productMetadata}
            {...(operation === undefined ? {} : { initialOperationId: operation })}
          />
        </Section>
      </Page>
    </div>
  );
};
