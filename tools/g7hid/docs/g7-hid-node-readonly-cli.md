# Node Read-only CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Node.js CLI that can enumerate the G7, identify it, read battery, and read device/key/config sectors without sending any write command.

**Architecture:** Keep HID transport, protocol commands, formatting, and CLI parsing in focused CommonJS modules. The transport layer exposes only `sendReadCommand` and explicitly disallows write/transaction commands; the CLI composes read-only operations and prints JSON or hexadecimal dumps.

**Tech Stack:** Node.js, `node-hid`, built-in `node:test` for pure transport tests.

---

## File Structure

- Create `src/g7-constants.js`: fixed VID, usage, report IDs, command constants, sector aliases, and read-only allowlist.
- Create `src/format.js`: hex bytes, hexdump, and JSON-safe device formatting.
- Create `src/g7-device.js`: device enumeration, opening, report building, report reading, identify, battery, and sector reads.
- Create `src/cli.js`: command-line argument parsing and command handlers.
- Create `test/g7-device.test.js`: tests for frame construction and read-only command enforcement.
- Modify `package.json`: add `node-hid` dependency and CLI scripts.

## Task 1: Constants and pure formatters

**Files:**
- Create: `src/g7-constants.js`
- Create: `src/format.js`
- Create: `test/g7-device.test.js`

- [ ] **Step 1: Add constants**

Use these exports:

```js
const VID = 0x320f;
const USAGE_PAGE = 0xff1c;
const USAGE = 0x92;
const CONFIG_REPORT_ID = 4;
const FIRMWARE_REPORT_ID = 5;
const PAYLOAD_SIZE = 63;
const WIRELESS_CHUNK_SIZE = 24;
const WIRED_CHUNK_SIZE = 56;
const CONFIG_SIZE = 128;
const DEVICE_INFO_SIZE = 34;
const COMMANDS = {
  deviceInfo: 0x03,
  readConfig: 0x05,
  readDefaultKeys: 0x07,
  readCurrentKeys: 0x08,
  status: 0x19,
  battery: 0x1a,
  identify: 0x30
};
const READ_ONLY_COMMANDS = new Set(Object.values(COMMANDS));
const SECTOR_ALIASES = {
  'device-info': COMMANDS.deviceInfo,
  info: COMMANDS.deviceInfo,
  config: COMMANDS.readConfig,
  'func-config': COMMANDS.readConfig,
  'default-keys': COMMANDS.readDefaultKeys,
  'current-keys': COMMANDS.readCurrentKeys,
  keys: COMMANDS.readCurrentKeys
};
```

- [ ] **Step 2: Add formatters**

Implement `toHex(bytes)`, `toHexDump(bytes, base = 0)`, and `toJsonDevice(device)`.

- [ ] **Step 3: Add tests**

Test that read-only allowlist contains `0x30`, `0x19`, `0x1a`, `0x03`, `0x05`, `0x07`, and `0x08`, and does not contain `0x01`, `0x02`, `0x06`, or `0x09`.

## Task 2: HID transport and protocol operations

**Files:**
- Create: `src/g7-device.js`
- Modify: `test/g7-device.test.js`

- [ ] **Step 1: Implement enumeration and opening**

`listG7Devices()` returns all `node-hid` devices matching VID, usage page, and usage. `openG7Device(path)` opens the supplied path or the first matching device.

- [ ] **Step 2: Implement report frame**

`buildReadPayload(command, length = 0, offset = 0)` returns a 63-byte Buffer with:

```text
00 00 command length offsetLow offsetHigh 00 data...
```

Throw unless the command is in `READ_ONLY_COMMANDS`.

- [ ] **Step 3: Implement request/response**

`sendReadCommand(command, length, offset)` writes Report ID 4 plus the payload and waits for the next Input Report ID 4.

- [ ] **Step 4: Implement protocol operations**

Implement:

- `identify()` using command `0x30`, length `16`; parse data bytes `AA 55 cid`.
- `readStatus()` using command `0x19`, length `24`.
- `readBattery()` using `0x19` then `0x1a`, length `6`; parse data byte 0.
- `readSector(command, total)` using 24-byte chunks and concatenate.
- `readAll()` returning identify, battery, device info, default keys, current keys, and config.

- [ ] **Step 5: Add tests**

Test frame offsets and ensure write commands `0x01`, `0x02`, `0x06`, and `0x09` throw before any HID call.

## Task 3: CLI

**Files:**
- Create: `src/cli.js`
- Modify: `package.json`

- [ ] **Step 1: Add CLI commands**

Support:

```text
list
info [--json]
read <sector> [--json] [--path <path>]
```

Sector names: `device-info`, `config`, `default-keys`, `current-keys`.

- [ ] **Step 2: Add package scripts**

Add:

```json
{
  "g7": "node src/cli.js",
  "g7:list": "node src/cli.js list",
  "g7:info": "node src/cli.js info"
}
```

- [ ] **Step 3: Install dependency**

Run: `npm install node-hid`.

## Task 4: Verification

- [ ] **Step 1: Run unit tests**

Run: `npm test`.
Expected: all tests pass.

- [ ] **Step 2: Run live list**

Run: `npm run g7:list`.
Expected: one G7 collection with VID `12815`, usage page `65308`, usage `146`.

- [ ] **Step 3: Run live info**

Run: `npm run g7:info`.
Expected: CID `12`, battery percentage, and successful reads of all sectors.

- [ ] **Step 4: Confirm no write commands exist in CLI path**

Search CLI and device module for `write(`. The only direct write should be the internal HID output used to send a read request; there must be no command `0x01`, `0x02`, `0x06`, or `0x09`.

## Self-Review

- Spec coverage: enumeration, identification, battery, device info, default keys, current keys, config, JSON output, and hex output are covered.
- Read-only constraint: write commands are absent from commands and the frame builder rejects them.
- Type consistency: sector names consistently map to command constants; `readSector` returns a Buffer.
