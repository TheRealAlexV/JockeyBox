/**
 * Logging straight to stderr.
 *
 * Stream Deck captures a plugin's stderr in its own log files, and running
 * `node bin/plugin.js` by hand shows it in the terminal. Nothing here is allowed
 * to throw: logging must never be the reason a key stops working.
 */

const PREFIX = "[jockeybox-soundboard]";

function format(parts: unknown[]): string {
  return parts
    .map((part) => {
      if (typeof part === "string") return part;
      if (part instanceof Error) return `${part.name}: ${part.message}`;
      try {
        return JSON.stringify(part);
      } catch {
        return String(part);
      }
    })
    .join(" ");
}

function write(level: string, parts: unknown[]): void {
  try {
    process.stderr.write(`${PREFIX} ${level} ${format(parts)}\n`);
  } catch {
    /* stderr closed; nothing useful to do */
  }
}

export function log(...parts: unknown[]): void {
  write("info ", parts);
}

export function warn(...parts: unknown[]): void {
  write("warn ", parts);
}

export function logError(...parts: unknown[]): void {
  write("error", parts);
}
