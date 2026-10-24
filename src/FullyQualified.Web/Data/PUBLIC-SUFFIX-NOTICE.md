# Mozilla Public Suffix List

`public_suffix_list.dat` is the unmodified Mozilla Public Suffix List downloaded from
https://publicsuffix.org/list/public_suffix_list.dat on 2026-10-24. Its upstream
version and commit are recorded in the file header. Copyright remains with its
upstream contributors. Licensed under the Mozilla Public License 2.0; see
`MPL-2.0.txt`. Matching includes ICANN and PRIVATE sections, wildcard rules and
exceptions. No domain query or result is stored alongside the rules.

Maintainers should periodically replace the file from the canonical URL, retain
its license/version header, update this retrieval date, and run ToolboxMailTests.
The application never downloads or updates this file at runtime.
