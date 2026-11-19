import {
  attachmentTransport,
  clientCompositions,
  type AttachmentTransferControl,
  type CaseSelector,
  type HumanDocument,
  type RegionRow,
} from "@contracts/client";
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ChangeEvent,
} from "react";
import {
  attachmentNotice,
  captureAttachment,
  isAttachmentDisclosureRefused,
  requestAttachment,
} from "../../api/attachments";
import { isDisclosureRefused } from "../../api/outcomes";
import { apiEndpoints, type CurrentCase } from "../../api/service";
import { PresentedValue } from "../../components/PresentedValue";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { useEndpoint } from "../../hooks/useEndpoint";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { isOperationAffordance, type OperationAffordance } from "../../operation/affordances";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { attachmentFragment } from "../../routing/coreLinks";
import { localNotice, type Notice } from "../../protocol/notices";
import { ownerScope, type OwnerScope } from "../../protocol/ownerScope";
import { useSessionContext } from "../../session/SessionContext";
import { Actions } from "../../ui/regions/Actions";
import { Button } from "../../ui/Button";
import { DocumentPreview } from "./pdf/DocumentPreview";
import "./attachments.css";

type CapturedFile = Awaited<ReturnType<typeof captureAttachment>>;
type Preview = { id: string; generation: number; labelId: string } & (
  { kind: "image"; name: string; url: string; error: boolean } | { kind: "pdf"; bytes: ArrayBuffer }
);
type Props = {
  current: CurrentCase;
  editing: boolean;
  disclosureAvailable: boolean;
  authoringAvailable: boolean;
  onAdd: (action: OperationAffordance, upload: CapturedFile) => void;
  onWithdraw: (action: OperationAffordance) => void;
  onDisclosureChanged: () => void;
};
type Target = Extract<CaseSelector, { kind: "ID" }>;
const downloadFile = (bytes: Uint8Array<ArrayBuffer>, mediaType: string, name: string) => {
  const url = URL.createObjectURL(new Blob([bytes], { type: mediaType }));
  const link = document.createElement("a");
  try {
    link.href = url;
    link.download = name;
    link.hidden = true;
    document.body.append(link);
    link.click();
  } finally {
    link.remove();
    setTimeout(() => {
      URL.revokeObjectURL(url);
    }, 0);
  }
};

const useAttachmentDisclosure = (selectedCase: string, document: HumanDocument | null) => {
  const cache = useEndpointCache()!;
  useSyncExternalStore(cache.subscribeInvalidation, cache.invalidation, cache.invalidation);
  const candidate = document?.attachmentObservation;
  const sequence =
    candidate?.caseId === selectedCase && !cache.attachmentsErased(selectedCase)
      ? candidate.sequence
      : null;
  const known = cache.attachmentDisclosure(selectedCase);
  useLayoutEffect(() => {
    if (sequence !== null) {
      cache.observeAttachmentDisclosure(selectedCase, sequence);
    }
  }, [cache, selectedCase, sequence]);
  return sequence === null || (known !== undefined && sequence < known) ? null : sequence;
};

type AttachmentListQuery = ReturnType<typeof useEndpoint<typeof apiEndpoints.caseAttachments>>;

const useAttachmentListRefusal = (
  result: AttachmentListQuery["result"],
  onDisclosureChanged: () => void,
) => {
  const reportedRefusal = useRef<typeof result>(null);
  useLayoutEffect(() => {
    if (result !== null && result !== reportedRefusal.current && isDisclosureRefused(result)) {
      reportedRefusal.current = result;
      onDisclosureChanged();
    }
  }, [result, onDisclosureChanged]);
};

const attachmentListDocument = ({ value, previous }: AttachmentListQuery) => {
  const outcome = value?.outcome;
  const disclosed =
    outcome?.kind === "FAILED" && outcome.fault.code === "CORE_STORE_UNAVAILABLE"
      ? previous?.outcome
      : outcome;
  return disclosed?.kind === "SUCCEEDED" ? disclosed.document : null;
};

const useAttachmentList = (target: Target, available: boolean, onDisclosureChanged: () => void) => {
  const requester = useRequesterContext();
  const { state } = useSessionContext();
  const owner = ownerScope(
    requester === null || state.kind !== "authenticated"
      ? undefined
      : {
          serviceOrigin: location.origin,
          installationId: requester.subject.installationId,
          actorId: requester.subject.actorId,
        },
    state.epoch,
  );
  const credential = state.kind === "authenticated" ? state.token : "";
  const selection = useMemo(() => ({ target }), [target]);
  const query = useEndpoint(
    apiEndpoints.caseAttachments,
    selection,
    available && owner !== null,
    JSON.stringify(owner),
    owner?.installationId,
  );
  useAttachmentListRefusal(available ? query.result : null, onDisclosureChanged);
  const candidate = available ? attachmentListDocument(query) : null;
  const sequence = useAttachmentDisclosure(target.caseId, candidate);
  const document = sequence === null || candidate?.disclosure !== "CURRENT" ? null : candidate;
  return { owner, credential, document, sequence, query };
};

const useAttachmentInteraction = (
  owner: OwnerScope,
  target: Target,
  credential: string,
  sequence: number,
  onRefusal: (notice: Notice) => void,
  clearRefusal: () => void,
) => {
  const cache = useEndpointCache()!;
  const alive = useRef(true);
  const active = useRef<AbortController | null>(null);
  const url = useRef<string | null>(null);
  const generation = useRef(0);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const trigger = useRef<HTMLElement | null>(null);

  const releaseUrl = useCallback(() => {
    if (url.current !== null) {
      URL.revokeObjectURL(url.current);
      url.current = null;
    }
  }, []);
  useLayoutEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      active.current?.abort();
      active.current = null;
      releaseUrl();
    };
  }, [releaseUrl]);
  const close = (restoreFocus = false) => {
    releaseUrl();
    setPreview(null);
    if (restoreFocus) {
      trigger.current?.focus();
    }
  };
  const currentOwner = () =>
    alive.current &&
    !cache.attachmentsErased(target.caseId) &&
    (cache.attachmentDisclosure(target.caseId) ?? sequence) === sequence
      ? owner
      : null;
  const begin = () => {
    if (currentOwner() === null || active.current !== null) {
      return null;
    }
    const controller = new AbortController();
    active.current = controller;
    setBusy(true);
    setNotice(null);
    clearRefusal();
    close();
    return controller;
  };
  const finish = (request: AbortController): boolean => {
    if (currentOwner() === null || active.current !== request || request.signal.aborted) {
      return false;
    }
    active.current = null;
    setBusy(false);
    return true;
  };
  const deliver = (
    control: AttachmentTransferControl,
    file: { bytes: Uint8Array<ArrayBuffer>; mediaType: string },
  ) => {
    const { bytes, mediaType } = file;
    const {
      kind,
      name,
      labelId,
      request: { attachmentId: id },
    } = control;
    if (kind === "DOWNLOAD") {
      downloadFile(bytes, mediaType, name);
      return;
    }
    const presentation = { id, labelId, generation: ++generation.current };
    if (mediaType === attachmentTransport.pdfMediaType) {
      setPreview({ ...presentation, kind: "pdf", bytes: bytes.buffer });
      return;
    }
    const next = URL.createObjectURL(new Blob([bytes], { type: mediaType }));
    url.current = next;
    setPreview({ ...presentation, kind: "image", name, url: next, error: false });
  };
  const receive = async (control: AttachmentTransferControl, element: Element) => {
    const request = begin();
    if (request === null) {
      return;
    }
    trigger.current = element instanceof HTMLElement ? element : null;

    const result = await requestAttachment(
      control.kind,
      control.request,
      control.name,
      credential,
      owner,
      currentOwner,
      request.signal,
    );
    if (!finish(request)) {
      return;
    }
    const feedback = attachmentNotice(result);
    if (feedback !== null && isAttachmentDisclosureRefused(result)) {
      close();
      onRefusal(feedback);
      return;
    }
    setNotice(feedback);
    if (result.kind === "outcome" && result.value.kind === "RECEIVED") {
      try {
        deliver(control, result.value.file);
      } catch {
        const failed =
          control.kind === "DOWNLOAD"
            ? localNotice("downloadFailed")
            : {
                kind: "human" as const,
                advice: {
                  message: {
                    messageId: "diagnostic.ATTACHMENT_PREVIEW_UNAVAILABLE",
                    arguments: [],
                  },
                  action: "NONE_REQUIRED" as const,
                },
              };
        setNotice(failed);
      }
    }
  };
  const choose = async (source: File, selected: (value: CapturedFile) => void) => {
    const request = begin();
    if (request === null) {
      return;
    }
    try {
      const file = await captureAttachment(source);
      if (finish(request)) {
        selected(file);
      }
    } catch {
      if (finish(request)) {
        setNotice(localNotice("invalidInput"));
      }
    }
  };
  const decodeError = (value: Extract<Preview, { kind: "image" }>) => {
    if (url.current === value.url) {
      releaseUrl();
      setPreview({ ...value, error: true });
    }
  };
  return { preview, busy, notice, receive, choose, close, decodeError };
};
type Interaction = ReturnType<typeof useAttachmentInteraction>;

const AttachmentPreview = ({
  preview,
  interaction,
}: {
  preview: Preview;
  interaction: Interaction;
}) => {
  const p = usePresentation();
  const unavailable = renderCatalogKey(p, "diagnostic.ATTACHMENT_PREVIEW_UNAVAILABLE");
  const control = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    control.current?.focus();
  }, []);
  const body = () => {
    if (preview.kind === "pdf") {
      return (
        <DocumentPreview
          bytes={preview.bytes}
          title={renderCatalogKey(p, preview.labelId)}
          unavailable={unavailable}
          closeButton={control}
        />
      );
    }
    return preview.error ? (
      <p role="alert">{unavailable}</p>
    ) : (
      <img
        alt={preview.name}
        src={preview.url}
        onError={() => {
          interaction.decodeError(preview);
        }}
      />
    );
  };
  return (
    <div className="attachment-preview">
      <button
        type="button"
        ref={control}
        onClick={() => {
          interaction.close(true);
        }}
      >
        {p.text("ui.close")}
      </button>
      {body()}
    </div>
  );
};
const AttachmentControls = ({
  row,
  interaction,
  editing,
  onWithdraw,
}: {
  row: RegionRow;
  interaction: Interaction;
  editing: boolean;
  onWithdraw: Props["onWithdraw"];
}) => {
  const p = usePresentation();
  const { busy } = interaction;
  return (
    <div className="attachment-actions">
      {row.transfers?.map((transfer) => (
        <Button
          key={transfer.kind}
          variant="secondary"
          isDisabled={busy}
          onPress={({ target }) => {
            void interaction.receive(transfer, target);
          }}
        >
          {renderCatalogKey(p, transfer.labelId)}
        </Button>
      ))}
      {editing || busy ? null : (
        <Actions
          actions={row.actions ?? []}
          compact
          onAction={(action) => {
            if (
              isOperationAffordance(action) &&
              action.task === clientCompositions.attachmentWithdrawal
            ) {
              interaction.close();
              onWithdraw(action);
            }
          }}
        />
      )}
    </div>
  );
};
type AttachmentTable = Extract<
  HumanDocument["screen"]["regions"][number]["content"],
  { kind: "TABLE" }
>;
type AttachmentRowProps = {
  row: RegionRow;
  columns: AttachmentTable["columns"];
  zone: string;
  interaction: Interaction;
  editing: boolean;
  onWithdraw: Props["onWithdraw"];
};
const AttachmentRow = ({ row, columns, zone, interaction, ...controls }: AttachmentRowProps) => {
  const p = usePresentation();
  const { preview } = interaction;
  return (
    <li>
      <dl className="attachment-metadata">
        {row.values.map((value, index) => {
          const column = columns[index];
          return column === undefined ? null : (
            <div key={column.key}>
              <dt>{renderCatalogKey(p, column.labelId)}</dt>
              <dd>
                <PresentedValue value={value} timeZone={zone} />
              </dd>
            </div>
          );
        })}
      </dl>
      <AttachmentControls {...controls} row={row} interaction={interaction} />
      {preview === null || preview.id !== row.key ? null : (
        <AttachmentPreview key={preview.generation} preview={preview} interaction={interaction} />
      )}
    </li>
  );
};

const AttachmentPicker = ({
  enabled,
  choose,
}: {
  enabled: boolean;
  choose: (file: File) => void;
}) => {
  const p = usePresentation();
  const id = useId();
  const select = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.currentTarget.files?.item(0);
    event.currentTarget.value = "";
    if (file !== undefined && file !== null && enabled) {
      choose(file);
    }
  };
  return (
    <div>
      <label htmlFor={id}>{p.text("ui.attachments.file")}</label>
      <input id={id} type="file" disabled={!enabled} onChange={select} />
    </div>
  );
};
const addOffer = (document: HumanDocument) =>
  document.actions.find(
    (action): action is OperationAffordance =>
      isOperationAffordance(action) &&
      action.task === clientCompositions.attachmentEntry &&
      action.blocked === null,
  );
const attachmentRegion = (document: HumanDocument | null) =>
  document?.screen.regions.find(({ role }) => role === "ATTACHMENTS");

const AttachmentRows = ({
  table,
  zone,
  ...controls
}: Omit<AttachmentRowProps, "row" | "columns"> & { table: AttachmentTable | null }) => {
  const p = usePresentation();
  if (table === null) {
    return null;
  }
  if (table.rows.length === 0) {
    return <p>{p.text("ui.attachments.empty")}</p>;
  }
  return (
    <ul className="attachment-list">
      {table.rows.map((row) => (
        <AttachmentRow key={row.key} row={row} columns={table.columns} zone={zone} {...controls} />
      ))}
    </ul>
  );
};

const useCurrentUpload = (
  enabled: boolean,
  offer: OperationAffordance | undefined,
  onAdd: Props["onAdd"],
) => {
  const current = useRef({ enabled, offer, onAdd });
  useLayoutEffect(() => {
    current.current = { enabled, offer, onAdd };
  }, [enabled, offer, onAdd]);
  return (file: CapturedFile) => {
    const active = current.current;
    if (active.enabled && active.offer !== undefined) {
      active.onAdd(active.offer, file);
    }
  };
};

const AttachmentWorkspace = ({
  owner,
  target,
  credential,
  document,
  sequence,
  onRefusal,
  clearRefusal,
  props,
}: {
  owner: OwnerScope;
  target: Target;
  credential: string;
  document: HumanDocument;
  sequence: number;
  onRefusal: (notice: Notice) => void;
  clearRefusal: () => void;
  props: Props;
}) => {
  const p = usePresentation();
  const interaction = useAttachmentInteraction(
    owner,
    target,
    credential,
    sequence,
    onRefusal,
    clearRefusal,
  );
  const content = attachmentRegion(document)?.content;
  const table = content?.kind === "TABLE" ? content : null;
  const add = addOffer(document);
  const editing = props.editing || !props.authoringAvailable;
  const addSelected = useCurrentUpload(!editing, add, props.onAdd);
  return (
    <div aria-busy={interaction.busy}>
      {add === undefined || !props.authoringAvailable ? null : (
        <AttachmentPicker
          enabled={!interaction.busy && !editing}
          choose={(file) => {
            void interaction.choose(file, addSelected);
          }}
        />
      )}
      {interaction.busy ? <p role="status">{p.text("ui.loading")}</p> : null}
      {interaction.notice === null ? null : (
        <p role="alert">
          <NoticeView value={interaction.notice} />
        </p>
      )}
      <AttachmentRows
        table={table}
        zone={props.current.basis.observation.timeZone}
        editing={editing}
        interaction={interaction}
        onWithdraw={props.onWithdraw}
      />
    </div>
  );
};

const ApplicableAttachments = (props: Props) => {
  const p = usePresentation();
  const id = useId();
  const { caseId } = props.current.subject;
  const target = useMemo(() => ({ kind: "ID" as const, caseId }), [caseId]);
  const { owner, credential, document, sequence, query } = useAttachmentList(
    target,
    props.disclosureAvailable,
    props.onDisclosureChanged,
  );
  const scope = JSON.stringify([owner, target, credential]);
  const [refusal, setRefusal] = useState<{ scope: string; notice: Notice } | null>(null);
  const notice = refusal?.scope === scope ? refusal.notice : query.message;
  const region = attachmentRegion(document);
  if (owner === null || (!props.disclosureAvailable && notice === null)) {
    return null;
  }
  return (
    <section
      className="screen-region case-attachments"
      id={attachmentFragment}
      tabIndex={-1}
      aria-labelledby={id}
      aria-busy={query.loading}
    >
      <h2 id={id}>{renderCatalogKey(p, region?.titleId ?? "section.attachments.label")}</h2>
      {query.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {notice === null ? null : (
        <p role="alert">
          <NoticeView value={notice} />
        </p>
      )}
      {document === null || sequence === null ? null : (
        <AttachmentWorkspace
          key={JSON.stringify([owner, target, credential, sequence])}
          owner={owner}
          target={target}
          credential={credential}
          document={document}
          sequence={sequence}
          clearRefusal={() => {
            setRefusal(null);
          }}
          onRefusal={(feedback) => {
            setRefusal({ scope, notice: feedback });
            query.reload();
            props.onDisclosureChanged();
          }}
          props={props}
        />
      )}
    </section>
  );
};
export const CaseAttachments = (props: Props) =>
  props.current.links.some((named) => named.link.endpoint === apiEndpoints.caseAttachments) ? (
    <ApplicableAttachments {...props} />
  ) : null;
