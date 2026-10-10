// JSON.parse owns grammar; this pass retains duplicate-member information it would discard.
const stringEnd = (text: string, start: number): number => {
  for (let at = start + 1; at < text.length; at += 1) {
    if (text[at] === "\\") {
      at += 1;
    } else if (text[at] === '"') {
      return at + 1;
    }
  }
  return text.length;
};

export const requireUniqueJsonMembers = (text: string): void => {
  const scopes: (Set<string> | null)[] = [];
  for (let at = 0; at < text.length; at += 1) {
    const character = text[at];
    if (character === "{") {
      scopes.push(new Set());
    } else if (character === "[") {
      scopes.push(null);
    } else if (character === "}" || character === "]") {
      scopes.pop();
    } else if (character === '"') {
      const end = stringEnd(text, at);
      let next = end;
      while (/\s/u.test(text[next] ?? "")) {
        next += 1;
      }
      const members = scopes.at(-1);
      if (text[next] === ":" && members) {
        const name = String(JSON.parse(text.slice(at, end)));
        if (members.has(name)) {
          throw new Error("Duplicate JSON member.");
        }
        members.add(name);
      }
      at = end - 1;
    }
  }
};
