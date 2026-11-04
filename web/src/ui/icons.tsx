import "./icons.css";

export type IconName = "calendar" | "chevron-start" | "chevron-end" | "chevron-down" | "close";

/** A single document sprite supplies decorative icons; controls own their accessible names. */
export const IconSprite = () => (
  <svg className="ui-icon-sprite" aria-hidden="true" focusable="false">
    <defs>
      <symbol id="claimcore-icon-calendar" viewBox="0 0 24 24">
        <rect x="3" y="5" width="18" height="16" rx="2" />
        <path d="M8 3v4m8-4v4M3 11h18M8 15h.01M12 15h.01M16 15h.01" />
      </symbol>
      <symbol id="claimcore-icon-chevron-start" viewBox="0 0 24 24">
        <path d="m15 18-6-6 6-6" />
      </symbol>
      <symbol id="claimcore-icon-chevron-end" viewBox="0 0 24 24">
        <path d="m9 6 6 6-6 6" />
      </symbol>
      <symbol id="claimcore-icon-chevron-down" viewBox="0 0 24 24">
        <path d="m6 9 6 6 6-6" />
      </symbol>
      <symbol id="claimcore-icon-close" viewBox="0 0 24 24">
        <path d="m6 6 12 12M6 18 18 6" />
      </symbol>
    </defs>
  </svg>
);

export const Icon = ({ name }: { name: IconName }) => (
  <svg
    className="ui-icon"
    viewBox="0 0 24 24"
    aria-hidden="true"
    focusable="false"
    data-directional={name.startsWith("chevron-") || undefined}
  >
    <use href={`#claimcore-icon-${name}`} />
  </svg>
);
