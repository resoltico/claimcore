import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { PresentedValue } from "../../components/PresentedValue";
import { Icon } from "../icons";
import "./notice.css";
export const Notice = ({
  content,
  timeZone,
}: {
  content: Extract<RegionContent, { kind: "NOTICE" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  const occurrences = new Map<string, number>();
  return content.notices.map((notice) => {
    const occurrence = occurrences.get(notice.messageId) ?? 0;
    occurrences.set(notice.messageId, occurrence + 1);
    return (
      <div
        key={`${notice.messageId}:${occurrence}`}
        className="region-notice"
        data-tone={notice.tone}
        role="status"
      >
        <Icon name="check" />
        <div>
          <p>{renderCatalogKey(p, notice.messageId)}</p>
          {notice.reason === undefined ? null : (
            <PresentedValue value={notice.reason} timeZone={timeZone} />
          )}
        </div>
      </div>
    );
  });
};
