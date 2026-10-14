const HID = require('node-hid');
const {
  VID,
  USAGE_PAGE,
  USAGE,
  CONFIG_REPORT_ID,
  PAYLOAD_SIZE,
  DATA_OFFSET,
  WIRELESS_CHUNK_SIZE,
  CONFIG_SIZE,
  DEVICE_INFO_SIZE,
  DEFAULT_TIMEOUT_MS,
  COMMANDS,
  READ_ONLY_COMMANDS,
  SECTOR_SIZES
} = require('./g7-constants');

function listG7Devices() {
  return HID.devices().filter((device) => {
    return device.vendorId === VID &&
      device.usagePage === USAGE_PAGE &&
      device.usage === USAGE;
  });
}

function buildReadPayload(command, length = 0, offset = 0) {
  if (!READ_ONLY_COMMANDS.has(command)) {
    throw new Error(`Refusing to build non-read-only command: 0x${command.toString(16)}`);
  }

  const payload = Buffer.alloc(PAYLOAD_SIZE);
  payload[2] = command;
  payload[3] = length;
  payload[4] = offset & 0xff;
  payload[5] = (offset >> 8) & 0xff;
  return payload;
}

function waitForInputReport(device, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      device.removeListener('data', onData);
      device.removeListener('error', onError);
      reject(new Error('Timed out waiting for HID input report'));
    }, timeoutMs);

    const onData = (report) => {
      clearTimeout(timer);
      device.removeListener('error', onError);
      resolve(report);
    };

    const onError = (error) => {
      clearTimeout(timer);
      device.removeListener('data', onData);
      reject(error);
    };

    device.once('data', onData);
    device.once('error', onError);
  });
}

class G7Device {
  constructor(device, descriptor = {}) {
    this.device = device;
    this.descriptor = descriptor;
  }

  static open(path) {
    const descriptors = listG7Devices();
    const descriptor = path
      ? descriptors.find((item) => item.path === path)
      : descriptors[0];

    if (!descriptor) {
      throw new Error('No G7 HID vendor collection found');
    }

    const device = new HID.HID(descriptor.path);
    device.setNonBlocking(1);
    return new G7Device(device, descriptor);
  }

  close() {
    if (this.device) {
      this.device.close();
      this.device = null;
    }
  }

  async sendReadCommand(command, length = 0, offset = 0, timeoutMs = DEFAULT_TIMEOUT_MS) {
    if (!this.device) {
      throw new Error('Device is closed');
    }

    const payload = buildReadPayload(command, length, offset);
    const reportPromise = waitForInputReport(this.device, timeoutMs);
    this.device.write(Buffer.concat([Buffer.from([CONFIG_REPORT_ID]), payload]));
    const report = await reportPromise;

    if (report[0] !== CONFIG_REPORT_ID) {
      throw new Error(`Unexpected report ID: ${report[0]}`);
    }

    return {
      command,
      length: report[3],
      offset: report[4] | (report[5] << 8),
      report,
      payload: report.slice(1),
      data: report.slice(1 + DATA_OFFSET)
    };
  }

  async identify() {
    const response = await this.sendReadCommand(COMMANDS.identify, 16);
    const [magic1, magic2, cid] = response.data;

    if (magic1 !== 0xaa || magic2 !== 0x55) {
      throw new Error(`Invalid identify signature: ${magic1.toString(16)} ${magic2.toString(16)}`);
    }

    return {
      magic: ['0xAA', '0x55'],
      cid,
      productId: this.descriptor.productId,
      path: this.descriptor.path
    };
  }

  async readStatus() {
    const response = await this.sendReadCommand(COMMANDS.status, 24);
    return response.data;
  }

  async readBattery() {
    await this.readStatus();
    const response = await this.sendReadCommand(COMMANDS.battery, 6);
    let battery = response.data[0];

    if (this.descriptor.productId === 0x22a4) {
      battery = 100;
    }

    return battery;
  }

  async readSector(command, total = SECTOR_SIZES[command]) {
    if (!SECTOR_SIZES[command]) {
      throw new Error(`Unknown sector command: 0x${command.toString(16)}`);
    }

    const chunks = [];

    for (let offset = 0; offset < total; offset += WIRELESS_CHUNK_SIZE) {
      const length = Math.min(WIRELESS_CHUNK_SIZE, total - offset);
      const response = await this.sendReadCommand(command, length, offset);
      chunks.push(response.data.slice(0, length));
    }

    return Buffer.concat(chunks);
  }

  async readAll() {
    const identity = await this.identify();
    const battery = await this.readBattery();
    const deviceInfo = await this.readSector(COMMANDS.deviceInfo, DEVICE_INFO_SIZE);
    const defaultKeys = await this.readSector(COMMANDS.readDefaultKeys, CONFIG_SIZE);
    const currentKeys = await this.readSector(COMMANDS.readCurrentKeys, CONFIG_SIZE);
    const config = await this.readSector(COMMANDS.readConfig, CONFIG_SIZE);

    return {
      identity,
      battery,
      deviceInfo,
      defaultKeys,
      currentKeys,
      config,
      parsedConfig: parseConfig(config),
      parsedDefaultKeys: parseKeyMatrix(defaultKeys),
      parsedCurrentKeys: parseKeyMatrix(currentKeys)
    };
  }
}

function decodeL4165BDpi(raw) {
  if (raw <= 159) {
    return (raw + 1) * 100;
  }
  return 16000 + (raw - 159) * 200;
}

function parseConfig(config) {
  const pollRateValues = [125, 250, 500, 1000];
  const liftDistanceValues = ['low', 'medium', 'high'];
  const gears = [];

  for (let index = 0; index < 6; index++) {
    const base = 14 + index * 9;
    const raw = config[base + 2];
    const color = '#' + [base + 6, base + 7, base + 8]
      .map((offset) => config[offset].toString(16).padStart(2, '0'))
      .join('')
      .toUpperCase();

    gears.push({
      index,
      enabled: config[base] === 1,
      raw,
      dpi: decodeL4165BDpi(raw),
      color
    });
  }

  return {
    profile: config[0],
    pollRate: pollRateValues[config[11]] ?? null,
    currentDpiIndex: config[12],
    liftDistance: liftDistanceValues[config[13]] ?? null,
    gears
  };
}

function parseKeyMatrix(matrix) {
  const entries = [];

  for (let offset = 0; offset + 2 < matrix.length; offset += 3) {
    const triplet = matrix.slice(offset, offset + 3);
    if (triplet[0] === 0 && triplet[1] === 0 && triplet[2] === 0) {
      continue;
    }

    entries.push({
      offset,
      type: triplet[0],
      parameter1: triplet[1],
      parameter2: triplet[2],
      hex: Array.from(triplet)
        .map((byte) => byte.toString(16).padStart(2, '0').toUpperCase())
        .join(' ')
    });
  }

  return entries;
}

module.exports = {
  G7Device,
  listG7Devices,
  buildReadPayload,
  parseConfig,
  parseKeyMatrix,
  decodeL4165BDpi
};
