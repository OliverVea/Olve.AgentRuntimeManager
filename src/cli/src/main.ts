#!/usr/bin/env bun
import { spawn } from "node:child_process";
import { run } from "./cli";

/** Best effort: the login URL is always printed too. */
function openBrowser(url: string): void {
  const [command, ...args] =
    process.platform === "darwin" ? ["open", url] : process.platform === "win32" ? ["cmd", "/c", "start", "", url] : ["xdg-open", url];
  try {
    spawn(command!, args, { stdio: "ignore", detached: true }).on("error", () => {}).unref();
  } catch {
    // No opener: the user follows the printed URL.
  }
}

// Ctrl+C ends the process (130), except while a streaming command (`arm events`) runs: then it
// aborts the stream, which exits 0; a second Ctrl+C still forces the exit.
const interrupt = new AbortController();
let streaming = false;
process.on("SIGINT", () => {
  if (!streaming || interrupt.signal.aborted) process.exit(130);
  interrupt.abort();
});

// Set exitCode rather than calling process.exit() so buffered stdout is flushed when piped.
process.exitCode = await run(process.argv.slice(2), {
  stdout: (text) => process.stdout.write(`${text}\n`),
  stderr: (text) => process.stderr.write(`${text}\n`),
  env: process.env,
  signal: interrupt.signal,
  openBrowser,
  interactive: process.stderr.isTTY === true,
  onStreaming: () => {
    streaming = true;
  },
});
