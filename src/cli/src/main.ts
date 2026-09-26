#!/usr/bin/env bun
import { run } from "./cli";

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
  onStreaming: () => {
    streaming = true;
  },
});
