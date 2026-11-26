import type { Region } from "@contracts/client";
import { Section } from "../../ui/Section";
import { FileDrop, type FileSelection } from "../../ui/FileDrop";
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
  type KeyboardEvent,
} from "react";
import {
  attachmentNotice,
  captureAttachment,
  type CapturedAttachment,
  isAttachmentDisclosureRefused,
  requestAttachment,
} from "../../api/attachments";
import { isDisclosureRefused } from "../../api/outcomes";
import { apiEndpoints, type CurrentCase } from "../../api/service";
import { DataTable } from "../../ui/DataTable";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { useEndpoint } from "../../hooks/useEndpoint";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { isOperationAffordance, type OperationAffordance } from "../../operation/affordances";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { localNotice, type Notice } from "../../protocol/notices";
import { ownerScope, type OwnerScope } from "../../protocol/ownerScope";
import { useSessionContext } from "../../session/SessionContext";
import { TaskControls } from "../../ui/TaskControls";
import { Button } from "../../ui/Button";
import { ButtonControl } from "../../ui/ButtonControl";
import { CopyValue } from "../../components/CopyValue";
import { DocumentPreview } from "./pdf/DocumentPreview";
import "./attachments.css";

const suppressActivation = (event: KeyboardEvent, busy: boolean) => {
  if ((event.key === "Enter" || event.key === " ") && (busy || event.repeat)) {
    event.preventDefault();
  }
};

// Capture at a native ancestor because pending buttons filter their own press handlers.
const suppressTransferActivation = (event: KeyboardEvent<HTMLDivElement>) => {
  const target =
    event.target instanceof Element
      ? event.target.closest("button[data-attachment-control]")
      : null;
  if (target !== null) {
    suppressActivation(event, target.hasAttribute("data-pending"));
  }
};

type Preview = { id: string; generation: number; labelId: string; focusOrigin: Element | null } & (
  { kind: "image"; name: string; url: string; error: boolean } | { kind: "pdf"; bytes: ArrayBuffer }
);
type Props = {
  current: CurrentCase;
  editing: boolean;
  disclosureAvailable: boolean;
  authoringAvailable: boolean;
  onAdd: (action: OperationAffordance, upload: CapturedAttachment) => void;
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

const intakeNotice = (messageId: string, counts: Record<string, number> = {}): Notice => ({
  kind: "human",
  advice: {
    action: "NONE_REQUIRED",
    message: {
      messageId,
      arguments: Object.entries(counts).map(([key, value]) => ({
        key,
        value: { kind: "COUNT", value },
      })),
    },
  },
});

const captureSelection = async (file: File | null) => {
  if (file === null) {
    return { kind: "unreadable" } as const;
  }
  try {
    return await captureAttachment(file);
  } catch {
    return { kind: "failed" } as const;
  }
};

const captureNotice = (
  failure: Exclude<Awaited<ReturnType<typeof captureSelection>>, { kind: "captured" }>,
): Notice => {
  switch (failure.kind) {
    case "tooLarge":
      return intakeNotice("label.attachmentFileTooLarge", {
        maximumBytes: attachmentTransport.maximumFileBytes,
        actualBytes: failure.actualBytes,
      });
    case "changed":
      return intakeNotice("label.attachmentFileChanged");
    case "unreadable":
      return intakeNotice("label.attachmentFileUnreadable");
    case "failed":
      return intakeNotice("fault.CORE_INTERNAL_ERROR");
  }
};

const materialize = async (selection: NonNullable<FileSelection>): Promise<File | null> => {
  try {
    return await selection();
  } catch {
    return null;
  }
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
  const picking = useRef<AbortController | null>(null);
  const url = useRef<string | null>(null);
  const generation = useRef(0);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [busy, setBusy] = useState(false);
  const [pickerBusy, setPickerBusy] = useState(false);
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
  const begin = (intake = false) => {
    if (
      currentOwner() === null ||
      (active.current !== null && (!intake || active.current !== picking.current))
    ) {
      return null;
    }
    active.current?.abort();
    const controller = new AbortController();
    active.current = controller;
    picking.current = intake ? controller : null;
    setPickerBusy(!intake);
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
    picking.current = null;
    setPickerBusy(false);
    setBusy(false);
    return true;
  };
  const deliver = (
    control: AttachmentTransferControl,
    file: { bytes: Uint8Array<ArrayBuffer>; mediaType: string },
    focusOrigin: Element | null,
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
    const presentation = { id, labelId, generation: ++generation.current, focusOrigin };
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

    const initialFocus = document.activeElement;
    let focusMoved = false;
    const observeFocus = (event: FocusEvent) => {
      if (event.target !== initialFocus && event.target !== document.body) {
        focusMoved = true;
      }
    };
    if (control.kind === "PREVIEW") {
      document.addEventListener("focusin", observeFocus, { signal: request.signal });
    }
    const result = await requestAttachment(
      control.kind,
      control.request,
      control.name,
      credential,
      owner,
      currentOwner,
      request.signal,
    ).finally(() => {
      document.removeEventListener("focusin", observeFocus);
    });
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
        deliver(control, result.value.file, focusMoved ? null : document.activeElement);
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
  const choose = async (
    selection: FileSelection,
    available: () => boolean,
    selected: (value: CapturedAttachment) => void,
  ) => {
    const request = available() ? begin(true) : null;
    if (request === null) {
      return;
    }
    if (selection === null) {
      if (finish(request)) {
        setNotice(intakeNotice("label.attachmentSingleFile"));
      }
      return;
    }
    const file = await materialize(selection);
    if (request.signal.aborted || currentOwner() === null || !available()) {
      finish(request);
      return;
    }
    picking.current = null;
    setPickerBusy(true);
    const result = await captureSelection(file);
    if (finish(request) && available()) {
      if (result.kind === "captured") {
        selected(result.value);
      } else {
        setNotice(captureNotice(result));
      }
    }
  };
  const decodeError = (value: Extract<Preview, { kind: "image" }>) => {
    if (url.current === value.url) {
      releaseUrl();
      setPreview({ ...value, error: true });
    }
  };
  return { preview, busy, pickerBusy, notice, receive, choose, close, decodeError };
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
  const { focusOrigin } = preview;
  useEffect(() => {
    if (focusOrigin !== null && document.activeElement === focusOrigin) {
      control.current?.focus();
    }
  }, [focusOrigin]);
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
    <div className="attachment-preview" onKeyDownCapture={suppressTransferActivation}>
      <ButtonControl
        data-attachment-control
        type="button"
        ref={control}
        onPress={() => {
          interaction.close(true);
        }}
      >
        {p.text("ui.close")}
      </ButtonControl>
      {body()}
    </div>
  );
};
const AttachmentControls = ({
  row,
  rowLabel,
  showDetails,
  interaction,
  editing,
  onWithdraw,
}: {
  row: RegionRow;
  rowLabel: string | undefined;
  showDetails: () => void;
  interaction: Interaction;
  editing: boolean;
  onWithdraw: Props["onWithdraw"];
}) => {
  const p = usePresentation();
  const { busy } = interaction;
  const actionLabel = (action: string) =>
    rowLabel === undefined ? action : p.text("ui.rowActionLabel", { action, row: rowLabel });
  return (
    <div className="attachment-actions" onKeyDownCapture={suppressTransferActivation}>
      {row.transfers?.map((transfer) => (
        <Button
          key={transfer.kind}
          variant="secondary"
          data-attachment-control
          isPending={busy}
          aria-label={actionLabel(renderCatalogKey(p, transfer.labelId))}
          onPress={({ target }) => {
            void interaction.receive(transfer, target);
          }}
        >
          {renderCatalogKey(p, transfer.labelId)}
        </Button>
      ))}
      {editing || busy ? null : (
        <TaskControls
          actions={row.actions ?? []}
          compact
          {...(rowLabel === undefined ? {} : { rowLabel })}
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
      {row.detail === undefined ? null : (
        <Button
          variant="secondary"
          aria-label={actionLabel(p.text("ui.viewDetails"))}
          onPress={showDetails}
        >
          {p.text("ui.viewDetails")}
        </Button>
      )}
    </div>
  );
};
type AttachmentTable = Extract<
  HumanDocument["screen"]["regions"][number]["content"],
  { kind: "TABLE" }
>;
const AttachmentPicker = ({
  enabled,
  busy,
  choose,
}: {
  enabled: boolean;
  busy: boolean;
  choose: (selection: FileSelection) => void;
}) => {
  const p = usePresentation();
  return (
    <FileDrop
      enabled={enabled}
      busy={busy}
      choose={choose}
      label={p.text("ui.attachments.file")}
      dropLabel={p.text("ui.attachments.drop")}
      hint={p.fieldHint("attachmentName")}
    />
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
  emptyId,
  zone,
  editing,
  interaction,
  onWithdraw,
}: {
  table: AttachmentTable | null;
  emptyId: string | undefined;
  zone: string;
  interaction: Interaction;
  editing: boolean;
  onWithdraw: Props["onWithdraw"];
}) => {
  const p = usePresentation();
  if (table === null) {
    return null;
  }
  const { preview } = interaction;
  const identityColumn = table.columns.findIndex(({ key }) => key === "attachmentId");
  const rowLabel = (row: RegionRow) => {
    const name = row.transfers?.[0]?.name ?? row.key;
    return row.values[identityColumn]?.kind === "IDENTIFIER"
      ? p.text("ui.pairedFacts", { first: name, second: row.key })
      : undefined;
  };
  return (
    <DataTable
      view={table}
      timeZone={zone}
      emptyId={emptyId ?? "ui.attachments.empty"}
      renderControls={(row, showDetails) => (
        <AttachmentControls
          row={row}
          rowLabel={rowLabel(row)}
          showDetails={showDetails}
          interaction={interaction}
          editing={editing}
          onWithdraw={onWithdraw}
        />
      )}
      renderDetailActions={(row) => (
        <CopyValue label={p.fieldLabel("attachmentId")} value={row.key} />
      )}
      renderSupplement={(row) =>
        preview === null || preview.id !== row.key ? null : (
          <AttachmentPreview key={preview.generation} preview={preview} interaction={interaction} />
        )
      }
    />
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
  const select = (file: CapturedAttachment) => {
    const active = current.current;
    if (active.enabled && active.offer !== undefined) {
      active.onAdd(active.offer, file);
    }
  };
  return { available: () => current.current.enabled, select };
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
  const region = attachmentRegion(document);
  const content = region?.content;
  const table = content?.kind === "TABLE" ? content : null;
  const add = addOffer(document);
  const editing = props.editing || !props.authoringAvailable;
  const upload = useCurrentUpload(!editing, add, props.onAdd);
  return (
    <div aria-busy={interaction.busy}>
      {add === undefined || !props.authoringAvailable ? null : (
        <AttachmentPicker
          enabled={!editing}
          busy={interaction.pickerBusy}
          choose={(selection) => {
            void interaction.choose(selection, upload.available, upload.select);
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
        emptyId={region?.emptyId}
        zone={props.current.basis.observation.timeZone}
        editing={editing}
        interaction={interaction}
        onWithdraw={props.onWithdraw}
      />
    </div>
  );
};

const AttachmentsSection = ({
  region,
  ...props
}: {
  region: Region | undefined;
} & Omit<React.ComponentProps<typeof Section>, "title" | "count">) => {
  const p = usePresentation();
  return (
    <Section
      {...props}
      title={renderCatalogKey(p, region?.titleId ?? "section.attachments.label")}
      count={region?.count}
    />
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
    <AttachmentsSection
      className="case-attachments"
      id={id}
      region={region}
      aria-busy={query.loading}
    >
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
    </AttachmentsSection>
  );
};
export const CaseAttachments = (props: Props) =>
  props.current.links.some((named) => named.link.endpoint === apiEndpoints.caseAttachments) ? (
    <ApplicableAttachments {...props} />
  ) : null;
