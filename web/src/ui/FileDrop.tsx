import { useState, type ReactNode } from "react";
import "./file-drop.css";

export const FileDrop = ({
  enabled,
  choose,
  children,
}: {
  enabled: boolean;
  choose: (file: File) => void;
  children: ReactNode;
}) => {
  const [dragOver, setDragOver] = useState(false);
  return (
    <div
      className="file-drop"
      data-drag-over={dragOver || undefined}
      onDragOver={(event) => {
        if (!enabled || !event.dataTransfer.types.includes("Files")) {
          return;
        }
        event.preventDefault();
        event.dataTransfer.dropEffect = "copy";
        setDragOver(true);
      }}
      onDragLeave={(event) => {
        if (
          !(event.relatedTarget instanceof Node) ||
          !event.currentTarget.contains(event.relatedTarget)
        ) {
          setDragOver(false);
        }
      }}
      onDrop={(event) => {
        if (!enabled || !event.dataTransfer.types.includes("Files")) {
          return;
        }
        event.preventDefault();
        setDragOver(false);
        const file =
          event.dataTransfer.files.length === 1 ? event.dataTransfer.files.item(0) : null;
        if (file !== null) {
          choose(file);
        }
      }}
    >
      {children}
    </div>
  );
};
