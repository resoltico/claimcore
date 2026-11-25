import { useRef, type ClipboardEvent } from "react";
import { DropZone } from "react-aria-components/DropZone";
import { FileTrigger } from "react-aria-components/FileTrigger";
import { Button } from "./Button";
import "./file-drop.css";

export type FileSelection = (() => File | Promise<File>) | null;

export const FileDrop = ({
  enabled,
  choose,
  label,
  dropLabel,
  hint,
  busy,
}: {
  enabled: boolean;
  choose: (selection: FileSelection) => void;
  label: string;
  dropLabel: string;
  hint: string;
  busy: boolean;
}) => {
  const held = useRef<number | null>(null);
  const chooser = useRef<HTMLButtonElement>(null);
  const selectFiles = (files: FileList) => {
    const file = files.item(0);
    choose(files.length === 1 && file !== null ? () => file : null);
  };
  const pasteFiles = (event: ClipboardEvent<HTMLDivElement>) => {
    const { files, items } = event.clipboardData;
    if (!enabled || busy || event.target === chooser.current || files.length === 0) {
      return;
    }

    // Clipboard files can lack filesystem entries. Handle FileList before the drag adapter.
    event.preventDefault();
    event.stopPropagation();
    if (Array.from(items).some((item) => item.webkitGetAsEntry?.()?.isDirectory === true)) {
      choose(null);
    } else {
      selectFiles(files);
    }
  };
  return (
    <div onPasteCapture={pasteFiles}>
      <DropZone
        className="file-drop"
        aria-label={dropLabel}
        isDisabled={!enabled}
        data-pending={busy || undefined}
        onPointerDownCapture={(event) => {
          held.current = busy ? event.pointerId : null;
          if (busy) {
            event.preventDefault();
            event.stopPropagation();
          }
        }}
        onPointerCancel={(event) => {
          if (event.pointerId === held.current) {
            held.current = null;
          }
        }}
        onClickCapture={(event) => {
          if (
            event.nativeEvent instanceof PointerEvent &&
            event.nativeEvent.pointerId === held.current
          ) {
            held.current = null;
            event.preventDefault();
            event.stopPropagation();
          }
        }}
        onDrop={(event) => {
          if (enabled && !busy) {
            const filesAndDirectories = event.items.filter((item) => item.kind !== "text");
            const [item] = filesAndDirectories;
            choose(
              filesAndDirectories.length === 1 && item?.kind === "file"
                ? () => item.getFile()
                : null,
            );
          }
        }}
      >
        <p>{dropLabel}</p>
        <FileTrigger
          onSelect={(files) => {
            if (enabled && !busy && files !== null && files.length > 0) {
              selectFiles(files);
            }
          }}
        >
          <Button ref={chooser} variant="secondary" isDisabled={!enabled} isPending={busy}>
            <span>{label}</span>
          </Button>
        </FileTrigger>
        <p>{hint}</p>
      </DropZone>
    </div>
  );
};
