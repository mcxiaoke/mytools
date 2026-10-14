const test = require('node:test');
const assert = require('node:assert/strict');

const {
  COMMANDS,
  READ_ONLY_COMMANDS,
  WRITE_COMMANDS
} = require('../src/g7-constants');
const {
  buildReadPayload,
  parseConfig,
  parseKeyMatrix,
  decodeL4165BDpi
} = require('../src/g7-device');

test('read-only command allowlist contains G7 read commands', () => {
  for (const command of [
    COMMANDS.identify,
    COMMANDS.status,
    COMMANDS.battery,
    COMMANDS.deviceInfo,
    COMMANDS.readConfig,
    COMMANDS.readDefaultKeys,
    COMMANDS.readCurrentKeys
  ]) {
    assert.equal(READ_ONLY_COMMANDS.has(command), true);
  }
});

test('write commands are not in the read-only allowlist', () => {
  for (const command of Object.values(WRITE_COMMANDS)) {
    assert.equal(READ_ONLY_COMMANDS.has(command), false);
  }
});

test('buildReadPayload creates a valid read frame', () => {
  const payload = buildReadPayload(COMMANDS.readCurrentKeys, 24, 0);
  assert.equal(payload.length, 63);
  assert.equal(payload[0], 0);
  assert.equal(payload[1], 0);
  assert.equal(payload[2], COMMANDS.readCurrentKeys);
  assert.equal(payload[3], 24);
  assert.equal(payload[4], 0);
  assert.equal(payload[5], 0);
  assert.equal(payload[6], 0);
});

test('buildReadPayload rejects write commands', () => {
  for (const command of Object.values(WRITE_COMMANDS)) {
    assert.throws(() => buildReadPayload(command), /non-read-only command/);
  }
});

test('L4165B DPI decoding matches official ranges', () => {
  assert.equal(decodeL4165BDpi(7), 800);
  assert.equal(decodeL4165BDpi(15), 1600);
  assert.equal(decodeL4165BDpi(159), 16000);
  assert.equal(decodeL4165BDpi(160), 16200);
  assert.equal(decodeL4165BDpi(199), 24000);
});

test('key matrix parser returns non-zero three-byte entries', () => {
  const matrix = Buffer.from([
    0x10, 0x01, 0x00,
    0x20, 0x10, 0x35,
    0x00, 0x00, 0x00
  ]);
  const entries = parseKeyMatrix(matrix);
  assert.equal(entries.length, 2);
  assert.equal(entries[0].hex, '10 01 00');
  assert.equal(entries[1].hex, '20 10 35');
});

test('config parser maps known fields', () => {
  const config = Buffer.alloc(128);
  config[11] = 3;
  config[12] = 2;
  config[13] = 0;

  const base = 14 + 2 * 9;
  config[base] = 1;
  config[base + 2] = 0x17;
  config[base + 6] = 0x00;
  config[base + 7] = 0x00;
  config[base + 8] = 0xff;

  const parsed = parseConfig(config);
  assert.equal(parsed.pollRate, 1000);
  assert.equal(parsed.currentDpiIndex, 2);
  assert.equal(parsed.liftDistance, 'low');
  assert.equal(parsed.gears[2].enabled, true);
  assert.equal(parsed.gears[2].dpi, 2400);
  assert.equal(parsed.gears[2].color, '#0000FF');
});
