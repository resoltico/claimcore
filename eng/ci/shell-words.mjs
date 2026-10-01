// Literal shell words for source-reference inspection, not a shell evaluator or syntax checker.
class Words {
  /** @type {Array<string | null>} */
  tokens = [];
  word = "";
  quote = "";
  escaped = false;
  comment = false;

  flush() {
    if (this.word !== "") {
      this.tokens.push(this.word);
      this.word = "";
    }
  }

  /** @param {string} character */
  ordinary(character) {
    if (character === '"' || character === "'") {
      this.quote = character;
    } else if (character === "#" && this.word === "") {
      this.comment = true;
    } else if (/[;|&(){}\n]/u.test(character)) {
      this.flush();
      this.tokens.push(null);
    } else if (/\s/u.test(character)) {
      this.flush();
    } else {
      this.word += character;
    }
  }

  /** @param {string} character */
  read(character) {
    if (this.comment) {
      if (character === "\n") {
        this.comment = false;
        this.tokens.push(null);
      }
    } else if (this.escaped) {
      this.word += character === "\n" ? "" : character;
      this.escaped = false;
    } else if (character === "\\" && this.quote !== "'") {
      this.escaped = true;
    } else if (this.quote === "") {
      this.ordinary(character);
    } else if (character === this.quote) {
      this.quote = "";
    } else {
      this.word += character;
    }
  }
}

/** @param {string} script @returns {string[][]} */
export function shellCommands(script) {
  const words = new Words();
  for (const character of script) {
    words.read(character);
  }
  words.flush();
  /** @type {string[][]} */
  const commands = [[]];
  for (const token of words.tokens) {
    if (token === null) {
      commands.push([]);
    } else {
      commands.at(-1)?.push(token);
    }
  }
  return commands.filter((command) => command.length > 0);
}
