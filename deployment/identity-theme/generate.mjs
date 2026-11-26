import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const target = process.argv[2];
const tokens = JSON.parse(
  readFileSync(new URL("../../web/src/theme/tokens.json", import.meta.url), "utf8"),
);
const accent = tokens.$extensions.claimcore.defaultAccent;
const names = ["bg-canvas", "bg-surface", "border-control", "border-subtle", "text-primary", "accent-solid", "on-accent-solid", "accent-text", "focus-ring"];
const color = (name, mode) => {
  const path = tokens.semantic[name].$value[mode].slice(1, -1).split(".");
  return (path[0] === "accent" ? [accent, path[1]] : path.slice(1)).reduce(
    (node, key) => node[key],
    tokens.color,
  ).$value;
};
const variables = (mode) => names.map((name) => `  --cc-${name}: ${color(name, mode)};`).join("\n");
const space = tokens.foundation["space-4"].$value;
const font = tokens.foundation["font-body"].$value;
const radius = tokens.foundation["radius-sm"].$value;
const cardRadius = tokens.foundation["radius-md"].$value;
const control = tokens.foundation["size-control-touch"].$value;
const markSize = tokens.foundation["size-product-mark"].$value;

mkdirSync(join(target, "resources/css"), { recursive: true });
mkdirSync(join(target, "resources/img"), { recursive: true });
copyFileSync(new URL("../../web/public/brand.svg", import.meta.url), join(target, "resources/img/brand.svg"));
mkdirSync(join(target, "messages"), { recursive: true });
copyFileSync(new URL("./messages/messages_en.properties", import.meta.url), join(target, "messages/messages_en.properties"));
copyFileSync(new URL("./theme.properties", import.meta.url), join(target, "theme.properties"));
writeFileSync(
  join(target, "resources/css/claimcore.css"),
  `:root {
${variables("light")}
  color-scheme: light dark;
  --keycloak-card-top-color: transparent;
  --pf-v5-global--FontFamily--text: ${font};
  --pf-v5-global--FontFamily--heading: ${font};
}
@media (prefers-color-scheme: dark) {
  :root {
${variables("dark")}
  }
}
.login-pf body { background: var(--cc-bg-canvas); }
#kc-header-wrapper {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: .5rem;
  color: var(--cc-text-primary) !important;
  font: 600 1.5rem ${font};
  letter-spacing: normal;
  text-transform: none;
}
#kc-header-wrapper::before {
  content: "";
  flex: none;
  inline-size: ${markSize};
  block-size: ${markSize};
  background: url("../img/brand.svg") center / contain no-repeat;
}
.pf-v5-c-login__main, #kc-content {
  color: var(--cc-text-primary);
  background: var(--cc-bg-surface);
}
.pf-v5-c-login__main {
  border: 1px solid var(--cc-border-subtle);
  border-block-start-width: 0;
  border-radius: ${cardRadius};
}
.pf-v5-c-login__main::before { display: none; }
.pf-v5-c-login__main :is(input, button) {
  min-block-size: ${control};
  border-radius: ${radius};
}
.pf-v5-c-login__main input:not([type="submit"]) {
  background: var(--cc-bg-surface);
  color: var(--cc-text-primary);
  border: 1px solid var(--cc-border-control);
}
.pf-v5-c-login__container {
  grid-template-columns: minmax(0, 34rem);
  padding-inline: ${space};
}
.pf-v5-c-login .pf-v5-c-button.pf-m-primary {
  --pf-v5-c-button--m-primary--BackgroundColor: var(--cc-accent-solid);
  --pf-v5-c-button--m-primary--hover--BackgroundColor: var(--cc-accent-solid);
  --pf-v5-c-button--m-primary--focus--BackgroundColor: var(--cc-accent-solid);
  --pf-v5-c-button--m-primary--active--BackgroundColor: var(--cc-accent-solid);
  --pf-v5-c-button--m-primary--Color: var(--cc-on-accent-solid);
  --pf-v5-c-button--m-primary--hover--Color: var(--cc-on-accent-solid);
  --pf-v5-c-button--m-primary--focus--Color: var(--cc-on-accent-solid);
  --pf-v5-c-button--m-primary--active--Color: var(--cc-on-accent-solid);
}
.pf-v5-c-login a { color: var(--cc-accent-text); }
.pf-v5-c-login :focus-visible { outline: 2px solid var(--cc-focus-ring); outline-offset: 2px; }
@media (forced-colors: active) {
  .pf-v5-c-login :focus-visible { outline-color: Highlight; }
}
`,
);
