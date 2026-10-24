import { useCallback, useRef, useState, useEffect } from "react";
import type { RecoveryItem } from "@contracts/client";
import {
  service,
  resultNotice,
  type ServiceResponse,
  type ProductMetadata,
} from "../../api/service";
import { useRetryablePage } from "../../hooks/useRead";
import { useOperation, type OperationModel } from "../../hooks/operation/useOperation";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../api/notices";
import { AccessibleModal } from "../../components/AccessibleModal";
import { ReceiptFields } from "../../components/ReceiptFields";
import { UnknownOperation, OperationDiscard } from "./OperationPanel";
const pageData = (response: ServiceResponse<"operation.pending">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;
type Props = {
  token: string;
  definition: ProductMetadata;
  onLockChange: (locked: boolean) => void;
};
const RecoveryResult = ({
  model,
  definition,
}: {
  model: OperationModel;
  definition: ProductMetadata;
}) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <section>
      <h3 ref={heading} tabIndex={-1}>
        {p.text("ui.acceptedOperation")}
      </h3>
      {model.receipt?.scope.kind === "CASE" ? (
        <ReceiptFields
          scope={model.receipt.scope}
          fields={definition.descriptors.fields}
          context={p.text("ui.acceptedContext", { operationId: model.receipt.operationId })}
        />
      ) : null}
      {model.receipt === null ? null : (
        <p>
          <bdi>{model.receipt.operationId}</bdi>
        </p>
      )}
      <button
        type="button"
        onClick={() => {
          model.discard();
        }}
      >
        {p.text("ui.recoveryAcknowledge")}
      </button>
    </section>
  );
};
const Forget = ({ item, token, done }: { item: RecoveryItem; token: string; done: () => void }) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const forget = async () => {
    setBusy(true);
    const result = await service.forget(item.recoveryId, token);
    setBusy(false);
    if (result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED") {
      setOpen(false);
      done();
    } else {
      setNotice(resultNotice(result));
    }
  };
  return (
    <>
      <button
        type="button"
        disabled={!item.offers.includes("FORGET")}
        onClick={() => {
          setOpen(true);
        }}
      >
        {p.text("ui.forgetSubmitted")}
      </button>
      <AccessibleModal
        title={p.text("ui.forgetSubmitted")}
        closeLabel={p.text("ui.keepEditing")}
        description={p.text("ui.forgetSubmittedDescription")}
        isOpen={open}
        isDismissable={!busy}
        onOpenChange={setOpen}
      >
        {notice === null ? null : (
          <p role="alert">
            <NoticeView value={notice} />
          </p>
        )}
        <button type="button" disabled={busy} onClick={() => void forget()}>
          {p.text("ui.forgetSubmitted")}
        </button>
      </AccessibleModal>
    </>
  );
};
const RecoveryItems = ({
  items,
  token,
  select,
  reload,
}: {
  items: ReadonlyArray<RecoveryItem>;
  token: string;
  select: (item: RecoveryItem) => void;
  reload: () => void;
}) => {
  const p = usePresentation();
  return (
    <ul>
      {items.map((item) => (
        <li key={item.recoveryId}>
          <p>
            <bdi>{item.operationId}</bdi> — <bdi>{item.act}</bdi> —{" "}
            <bdi>{item.caseReference ?? item.targetActorId}</bdi>
          </p>
          <p>{p.text("ui.recoveryExpires", { expiresAt: item.expiresAt })}</p>
          <button
            type="button"
            onClick={() => {
              select(item);
            }}
          >
            {p.text("ui.selectSubmitted")}
          </button>
          <Forget
            item={item}
            token={token}
            done={() => {
              reload();
            }}
          />
        </li>
      ))}
    </ul>
  );
};
const RecoveryList = ({
  token,
  select,
}: {
  token: string;
  select: (item: RecoveryItem) => void;
}) => {
  const p = usePresentation();
  const page = useRetryablePage<
    ServiceResponse<"operation.pending">,
    RecoveryItem,
    NonNullable<ReturnType<typeof pageData>>
  >(
    useCallback(
      (cursor: string | null, signal: AbortSignal) => service.pending(cursor, token, signal),
      [token],
    ),
    pageData,
  );
  return (
    <>
      <button type="button" disabled={page.loading} onClick={() => void page.load(null)}>
        {p.text("ui.tryAgain")}
      </button>
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      <RecoveryItems
        items={page.items}
        token={token}
        select={select}
        reload={() => {
          void page.load(null);
        }}
      />
      {page.cursor === null ? null : (
        <button type="button" disabled={page.loading} onClick={() => void page.load(page.cursor)}>
          {p.text("ui.moreSubmitted")}
        </button>
      )}
    </>
  );
};
const RecoverySelection = ({
  model,
  definition,
  token,
}: {
  model: OperationModel;
  definition: ProductMetadata;
  token: string;
}) => {
  if (model.recovery === null) {
    return <RecoveryList token={token} select={model.recover} />;
  }
  if (model.phase === "ACCEPTED") {
    return <RecoveryResult model={model} definition={definition} />;
  }
  return (
    <>
      <UnknownOperation model={model} />
      <OperationDiscard model={model} />
    </>
  );
};
export const RecoveryRequests = ({ token, definition, onLockChange }: Props) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  const model = useOperation({ scope: "CASE", kind: "OPEN", current: null, token, onLockChange });
  return (
    <section aria-labelledby="submitted-title">
      <h2 id="submitted-title">{p.text("ui.submittedRequests")}</h2>
      <p>{p.text("ui.submittedDescription")}</p>
      {open ? (
        <RecoverySelection model={model} token={token} definition={definition} />
      ) : (
        <button
          type="button"
          onClick={() => {
            setOpen(true);
          }}
        >
          {p.text("ui.listSubmitted")}
        </button>
      )}
    </section>
  );
};
