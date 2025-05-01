# TLV Snapshot Format

A **Type–Length–Value** (TLV) text format for serializing a directory tree—including empty folders and file contents—into a single text file (or split into chunks) that can be sent in an email body without binary attachments, Base64, or ZIP.

---

## Overview

- **Only folders and files** are represented.
- **Only path + content** per file; no timestamps, permissions, checksums in individual records.
- A **single header record** carries global metadata.
- **Preorder** traversal (directory, then its files, then subdirectories), lexicographically ascending.
- **UTF-8** normalization for all file contents (LF-only, no BOM).
- **Optional chunking** for very large repositories.
- **Final integrity check** via a single SHA3-512 hash over the entire TLV stream.

---

## File Structure

```
HDR <version> <repoName>
DIR <pathLen> <path>
FIL <pathLen> <contentLen> <path>
<raw file content bytes>\n
… (repeated for each file)
CHK <hexLength> <hexDigest>
```

Optionally, the snapshot can be split into N parts; each part is a standalone TLV file beginning with its own `HDR` and ending with its own `CHK`.

---

## Record Types

### 1. Header (`HDR`)
- **Purpose**: Global metadata for the snapshot.
- **Syntax**:
  ```
  HDR <version> <repoName>
  ```
- **Fields**:
  - `<version>`: Integer format version (e.g. `1`).
  - `<repoName>`: Name of the root directory.

### 2. Directory (`DIR`)
- **Purpose**: Declare a directory (even if empty).
- **Syntax**:
  ```
  DIR <pathLen> <path>
  ```
- **Fields**:
  - `<pathLen>`: Number of bytes in `<path>` (UTF-8).
  - `<path>`: Relative path from the repo root, using `/`.

### 3. File (`FIL`)
- **Purpose**: Embed a file’s content.
- **Syntax**:
  ```
  FIL <pathLen> <contentLen> <path>
  <raw content bytes>\n
  ```
- **Fields**:
  - `<pathLen>`: Number of bytes in `<path>` (UTF-8).
  - `<contentLen>`: Number of bytes in the file’s normalized UTF-8 content.
  - `<path>`: Relative path from the repo root.
  - Next line: exactly `<contentLen>` bytes of content (UTF-8, LF-only).

### 4. Checksum (`CHK`)
- **Purpose**: Verify integrity of the entire snapshot.
- **Syntax**:
  ```
  CHK <hexLength> <hexDigest>
  ```
- **Fields**:
  - `<hexLength>`: Number of characters in the hex digest (e.g. `128` for SHA3-512).
  - `<hexDigest>`: Hex-encoded SHA3-512 digest of all bytes *before* the `CHK` line.

---

## Traversal Order

Use a **preorder** walk:

1. Emit `DIR` for the current directory.
2. Emit all `FIL` records for files in that directory (sorted A→Z).
3. Recurse into each subdirectory (sorted A→Z).

---

## Text Normalization

- **Encoding**: Read each source file with BOM-aware UTF-8, UTF-16, or ANSI fallback.
- **BOM**: Strip any BOM.
- **Line Endings**: Normalize `\r\n` or `\r` → `\n`.
- **Output**: Write content as raw UTF-8 bytes.

---

## Path Validation

On import, for each `DIR` or `FIL` path:

1. Compute `full = Path.GetFullPath(Path.Combine(destRoot, path))`.
2. If `!full.StartsWith(destRoot, OrdinalIgnoreCase)`, abort (prevent traversal attacks).

---

## Chunking

To split very large repos, export into chunks of ≤ _C_ bytes:

- Each chunk file begins with its own `HDR <version> <repoName>` (no `CHK` until the end of that chunk).
- Directories and files included in that chunk follow the same TLV rules.
- Each chunk ends with its own `CHK` line.
- Recipients run `import` on each chunk—duplicate `DIR`/`HDR` records are skipped harmlessly.

---

## Import / Export CLI

```bash
# Export full snapshot
snapshot export <repoPath> <outFile>

# Import full snapshot
snapshot import <snapshotFile> <destPath>

# Export in chunks (default 25 MB per part)
snapshot export-chunks <repoPath> <outputBase> [--chunk-size <bytes>]

# Import multiple chunk files in order
snapshot import-chunks <part1> <part2> … <destPath>
```

---

## Example

Given:

```
myrepo/
├─ README.md        (29 bytes)
└─ src/
   ├─ main.cs       (83 bytes)
   └─ utils/        (empty)
```

Snapshot (`myrepo.snapshot`):

```text
HDR 1 myrepo
DIR 1 .
FIL 9 29 README.md
# My Project
This is a test.

DIR 3 src
FIL 11 83 src/main.cs
using System;
class Program { static void Main() { Console.WriteLine("Hello"); } }

DIR 8 src/utils
CHK 128 e3b0c44298fc1c149afbf4c8996fb924... (full SHA3-512 hex)
```

---

## Notes 

- Wrap the snapshot body in `-----BEGIN TLV SNAPSHOT-----` / `-----END TLV SNAPSHOT-----` **in your email template**, not inside the TLV file itself.
- Always normalize text to UTF-8 to avoid encoding issues.
- Use the `CHK` record to detect corruption or tampering.
- Skip duplicate `HDR`/`DIR` records when importing multiple chunks.
- Keep the format version in the header; bump it if you introduce incompatible changes.

---

_End of specification._
