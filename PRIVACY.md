# Privacy Note

FiveM Diagnostics is local-first by default.

## What stays local

- sessions
- incident exports
- imported artifacts
- generated ETL traces
- settings

The app does not upload data to a cloud service by default.

## What can be collected

- local machine metadata such as OS version, CPU, GPU, memory and display refresh rate
- local process metrics for FiveM and competing processes
- OBS runtime stats when OBS is available
- active TCP endpoints and UDP local ports for the active FiveM/GTA process
- optional imported artifacts such as net stats, profiler files, logs and ETL traces

## Export defaults

Exports redact sensitive fields by default.

Default redaction behavior:

- remote endpoint IP addresses are replaced with `[redacted]`
- artifact file paths are reduced to file names
- attached artifacts are excluded unless explicitly enabled

## When to include sensitive data

Only enable sensitive export fields when:

- you trust the recipient
- the recipient needs raw endpoint or path data for deeper diagnostics
- you understand that imported artifacts may contain usernames, local paths or server details

## Operational guidance

- review imported logs before attaching them to an export
- prefer the default redacted export first
- only keep ETL traces as long as needed for troubleshooting
