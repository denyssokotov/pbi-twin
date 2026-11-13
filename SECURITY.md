# Security policy

PbiTwin's purpose is to keep confidential data from leaking. We treat the following as security issues:

- any way PbiTwin sends data over the network or connects to anything other than `localhost`;
- original values written to disk (twin files, trust report, logs, temporary files);
- original values that survive in a twin without the leak scan reporting them;
- a way to recover original values from a twin without the seed.

## Reporting

Please report privately via GitHub: **Security → Report a vulnerability** on this repository.
Do not attach real customer data; describe the model shape or use the ContosoPII sample instead.

We aim to acknowledge reports within a week.

## Supported versions

Only the latest release receives fixes.
