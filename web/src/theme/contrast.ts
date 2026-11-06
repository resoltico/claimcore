import tokens from "./tokens.json" with { type: "json" };
import pairs from "./contrast-pairs.json" with { type: "json" };

type Mode = "light" | "dark";
const primitiveColors = (): Readonly<Record<string, string>> => {
  const colors: Record<string, string> = {};
  for (const [name, group] of Object.entries(tokens.color)) {
    if ("$value" in group) {
      colors[`color.${name}`] = group.$value;
    } else {
      for (const [step, item] of Object.entries(group)) {
        colors[`color.${name}.${step}`] = item.$value;
      }
    }
  }
  return colors;
};
const colors = primitiveColors();
const themeColors = (accent: string, mode: Mode): Readonly<Record<string, string>> => {
  if (!tokens.$extensions.claimcore.accents.includes(accent)) {
    throw new Error(`Unknown accent: ${accent}`);
  }
  return Object.fromEntries(
    Object.entries(tokens.semantic).map(([name, token]) => {
      const reference = token.$value[mode].slice(1, -1).replace(/^accent\./u, `color.${accent}.`);
      const color = colors[reference];
      if (color === undefined) {
        throw new Error(`Unknown color reference: ${reference}`);
      }
      return [`--${name}`, color];
    }),
  );
};
const luminance = (hex: string): number => {
  if (!/^#[\da-f]{6}$/iu.test(hex)) {
    throw new Error(`Invalid sRGB color: ${hex}`);
  }
  const linear = [1, 3, 5].map((offset) => {
    const channel = Number.parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * linear[0]! + 0.7152 * linear[1]! + 0.0722 * linear[2]!;
};
export const contrastRatio = (foreground: string, background: string): number => {
  const first = luminance(foreground);
  const second = luminance(background);
  return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
};
export const themeContrast = (accent: string, mode: Mode) => {
  const palette = themeColors(accent, mode);
  return pairs.map((pair) => {
    const [foreground, background, minimum, meaning] = pair;
    if (
      typeof foreground !== "string" ||
      typeof background !== "string" ||
      typeof minimum !== "number" ||
      typeof meaning !== "string"
    ) {
      throw new Error("Invalid contrast pair.");
    }
    const front = palette[foreground];
    const back = palette[background];
    if (front === undefined || back === undefined) {
      throw new Error(`Missing contrast token: ${foreground}, ${background}`);
    }
    return { foreground, background, minimum, meaning, ratio: contrastRatio(front, back) };
  });
};
