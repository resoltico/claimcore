import "./icons.css";

export type IconName =
  | "check"
  | "calendar"
  | "chevron-start"
  | "chevron-end"
  | "chevron-down"
  | "close"
  | "cases"
  | "reminders"
  | "people"
  | "person"
  | "history"
  | "settings"
  | "search"
  | "info";

/** A single document sprite supplies decorative icons; controls own their accessible names. */
export const IconSprite = () => (
  <svg className="ui-icon-sprite" aria-hidden="true" focusable="false">
    <defs>
      <symbol id="claimcore-icon-info" viewBox="0 0 24 24">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 11v6m0-10v1" />
      </symbol>
      <symbol id="claimcore-icon-check" viewBox="0 0 24 24">
        <path d="m5 12 4 4 10-10" />
      </symbol>
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
      <symbol id="claimcore-icon-cases" viewBox="0 0 24 24">
        <path d="M3 6h7l2 3h9v12H3Z" />
      </symbol>
      <symbol id="claimcore-icon-reminders" viewBox="0 0 24 24">
        <path d="M5 17h14l-2-3V9a5 5 0 0 0-10 0v5ZM10 21h4" />
      </symbol>
      <symbol id="claimcore-icon-person" viewBox="0 0 24 24">
        <circle cx="12" cy="7" r="4" />
        <path d="M4 21v-3a8 8 0 0 1 16 0v3" />
      </symbol>
      <symbol id="claimcore-icon-people" viewBox="0 0 24 24">
        <circle cx="9" cy="7" r="3" />
        <path d="M2 21v-3a7 7 0 0 1 14 0v3M16 4a3 3 0 0 1 0 6m3 4a7 7 0 0 1 3 7" />
      </symbol>
      <symbol id="claimcore-icon-history" viewBox="0 0 24 24">
        <path d="M3 11a9 9 0 1 1 2 7M3 4v7h7m2-5v7l4 2" />
      </symbol>
      <symbol id="claimcore-icon-settings" viewBox="0 0 24 24">
        <circle cx="12" cy="12" r="3" />
        <path d="m9 3-1 3-3 1-2 3 2 2-1 4 3 2 3-1 2 4 3-2 1-3 4-1v-4l-3-2-1-4-4-1Z" />
      </symbol>
      <symbol id="claimcore-icon-search" viewBox="0 0 24 24">
        <circle cx="10" cy="10" r="7" />
        <path d="m15 15 7 7" />
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
