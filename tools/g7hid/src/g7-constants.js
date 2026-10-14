const VID = 0x320f;
const USAGE_PAGE = 0xff1c;
const USAGE = 0x92;

const CONFIG_REPORT_ID = 4;
const FIRMWARE_REPORT_ID = 5;
const PAYLOAD_SIZE = 63;
const HEADER_SIZE = 7;
const DATA_OFFSET = 7;
const WIRELESS_CHUNK_SIZE = 24;
const WIRED_CHUNK_SIZE = 56;
const CONFIG_SIZE = 128;
const DEVICE_INFO_SIZE = 34;
const DEFAULT_TIMEOUT_MS = 2000;

const COMMANDS = {
  deviceInfo: 0x03,
  readConfig: 0x05,
  readDefaultKeys: 0x07,
  readCurrentKeys: 0x08,
  status: 0x19,
  battery: 0x1a,
  identify: 0x30
};

const WRITE_COMMANDS = {
  beginTransaction: 0x01,
  endTransaction: 0x02,
  writeConfig: 0x06,
  writeCurrentKeys: 0x09
};

const READ_ONLY_COMMANDS = new Set(Object.values(COMMANDS));

const SECTOR_ALIASES = {
  'device-info': COMMANDS.deviceInfo,
  info: COMMANDS.deviceInfo,
  config: COMMANDS.readConfig,
  'func-config': COMMANDS.readConfig,
  'function-config': COMMANDS.readConfig,
  'default-keys': COMMANDS.readDefaultKeys,
  'current-keys': COMMANDS.readCurrentKeys,
  keys: COMMANDS.readCurrentKeys
};

const SECTOR_SIZES = {
  [COMMANDS.deviceInfo]: DEVICE_INFO_SIZE,
  [COMMANDS.readConfig]: CONFIG_SIZE,
  [COMMANDS.readDefaultKeys]: CONFIG_SIZE,
  [COMMANDS.readCurrentKeys]: CONFIG_SIZE
};

module.exports = {
  VID,
  USAGE_PAGE,
  USAGE,
  CONFIG_REPORT_ID,
  FIRMWARE_REPORT_ID,
  PAYLOAD_SIZE,
  HEADER_SIZE,
  DATA_OFFSET,
  WIRELESS_CHUNK_SIZE,
  WIRED_CHUNK_SIZE,
  CONFIG_SIZE,
  DEVICE_INFO_SIZE,
  DEFAULT_TIMEOUT_MS,
  COMMANDS,
  WRITE_COMMANDS,
  READ_ONLY_COMMANDS,
  SECTOR_ALIASES,
  SECTOR_SIZES
};
