export default {
  extends: ["stylelint-config-standard"],
  reportNeedlessDisables: true,
  reportInvalidScopeDisables: true,
  reportDescriptionlessDisables: true,
  rules: {
    "max-nesting-depth": 2,
    "selector-max-specificity": "0,3,0",
    "declaration-block-no-duplicate-properties": true,
    "selector-max-compound-selectors": 3,
  },
};
