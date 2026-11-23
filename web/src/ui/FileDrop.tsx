import { useRef } from "react";
import { DropZone } from "react-aria-components/DropZone";
import { FileTrigger } from "react-aria-components/FileTrigger";
import { Button } from "./Button";
import "./file-drop.css";

export const FileDrop = ({
  enabled,
  choose,
  label,
  hint,
  busy,
}: {
  enabled: boolean;
  choose: (file: File) => void;
  label: string;
  hint: string;
  busy: boolean;
}) => {
  const held = useRef<number | null>(null);
  return (
    <DropZone
      className="file-drop"
      aria-label={hint}
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
        const [item] = event.items;
        if (enabled && !busy && event.items.length === 1 && item?.kind === "file") {
          void item.getFile().then((file) => {
            choose(file);
          });
        }
      }}
    >
      <p>{hint}</p>
      <FileTrigger
        aria-label={label}
        onSelect={(files) => {
          const file = files?.item(0);
          if (enabled && !busy && file !== undefined && file !== null) {
            choose(file);
          }
        }}
      >
        <Button variant="secondary" isDisabled={!enabled} isPending={busy}>
          <span>{label}</span>
        </Button>
      </FileTrigger>
    </DropZone>
  );
};
