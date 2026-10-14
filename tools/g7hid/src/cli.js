#!/usr/bin/env node

const {
  G7Device,
  listG7Devices
} = require('./g7-device');
const {
  SECTOR_ALIASES,
  SECTOR_SIZES,
  COMMANDS
} = require('./g7-constants');
const {
  toHexDump,
  toJsonDevice,
  bufferToJson
} = require('./format');

function parseArgs(argv) {
  const options = {
    command: argv[0],
    positional: [],
    json: false,
    path: null,
    timeout: 2000
  };

  for (let index = 1; index < argv.length; index++) {
    const arg = argv[index];

    if (arg === '--json') {
      options.json = true;
    } else if (arg === '--path') {
      options.path = argv[++index];
    } else if (arg === '--timeout') {
      options.timeout = Number(argv[++index]);
    } else if (arg === '--help' || arg === '-h') {
      options.help = true;
    } else {
      options.positional.push(arg);
    }
  }

  return options;
}

function usage() {
  return [
    'Usage:',
    '  node src/cli.js list',
    '  node src/cli.js info [--json] [--path <path>]',
    '  node src/cli.js read <sector> [--json] [--path <path>]',
    '',
    'Sectors:',
    '  device-info',
    '  config',
    '  default-keys',
    '  current-keys',
    '',
    'Options:',
    '  --json             Print JSON instead of a human-readable view',
    '  --path <path>      Open a specific HID collection path',
    '  --timeout <ms>     Per-report timeout, default 2000ms'
  ].join('\n');
}

function printDevicesTable(devices) {
  if (!devices.length) {
    return 'No G7 HID vendor collection found.';
  }

  return devices.map((device, index) => {
    return [
      `#${index + 1}`,
      `VID: ${device.vendorId} (0x${device.vendorId.toString(16).toUpperCase()})`,
      `PID: ${device.productId} (0x${device.productId.toString(16).padStart(2, '0').toUpperCase()})`,
      `UsagePage: ${device.usagePage} (0x${device.usagePage.toString(16).toUpperCase()})`,
      `Usage: ${device.usage} (0x${device.usage.toString(16).toUpperCase()})`,
      `Product: ${device.product || ''}`,
      `Path: ${device.path}`
    ].join('\n');
  }).join('\n\n');
}

function serializeReadAll(data) {
  return {
    identity: data.identity,
    battery: data.battery,
    parsedConfig: data.parsedConfig,
    parsedDefaultKeys: data.parsedDefaultKeys,
    parsedCurrentKeys: data.parsedCurrentKeys,
    deviceInfo: bufferToJson(data.deviceInfo),
    defaultKeys: bufferToJson(data.defaultKeys),
    currentKeys: bufferToJson(data.currentKeys),
    config: bufferToJson(data.config)
  };
}

function formatInfo(data) {
  const config = data.parsedConfig;
  const lines = [
    'Device:',
    `  CID: ${data.identity.cid}`,
    `  PID: ${data.identity.productId}`,
    '',
    `Battery: ${data.battery}%`,
    '',
    'Config:',
    `  Poll rate: ${config.pollRate}Hz`,
    `  Current DPI gear: ${config.currentDpiIndex + 1}`,
    `  Lift distance: ${config.liftDistance}`,
    '',
    'DPI gears:'
  ];

  for (const gear of config.gears) {
    const current = gear.index === config.currentDpiIndex ? ' *' : '';
    lines.push(`  ${gear.index + 1}. ${gear.enabled ? 'enabled' : 'disabled'} ${gear.dpi} DPI ${gear.color}${current}`);
  }

  lines.push('', 'Current key entries:');
  for (const entry of data.parsedCurrentKeys) {
    lines.push(`  offset ${entry.offset}: ${entry.hex}`);
  }

  return lines.join('\n');
}

function resolveSector(name) {
  if (SECTOR_ALIASES[name] !== undefined) {
    return SECTOR_ALIASES[name];
  }

  if (/^0x[0-9a-f]+$/i.test(name)) {
    return Number(name);
  }

  if (/^[0-9a-f]{1,2}$/i.test(name)) {
    return Number.parseInt(name, 16);
  }

  return null;
}

async function run(options) {
  if (options.help || !options.command) {
    return usage();
  }

  if (options.command === 'list') {
    const devices = listG7Devices();
    if (options.json) {
      return JSON.stringify(devices.map(toJsonDevice), null, 2);
    }
    return printDevicesTable(devices);
  }

  if (options.command === 'info') {
    const device = G7Device.open(options.path);
    try {
      const data = await device.readAll();
      if (options.json) {
        return JSON.stringify(serializeReadAll(data), null, 2);
      }
      return formatInfo(data);
    } finally {
      device.close();
    }
  }

  if (options.command === 'read') {
    const sectorName = options.positional[0];
    const command = resolveSector(sectorName);

    if (command === null || !SECTOR_SIZES[command]) {
      throw new Error(`Unknown or unsupported read sector: ${sectorName}`);
    }

    const device = G7Device.open(options.path);
    try {
      const data = await device.readSector(command, SECTOR_SIZES[command]);
      if (options.json) {
        return JSON.stringify({
          sector: sectorName,
          command: `0x${command.toString(16).toUpperCase()}`,
          ...bufferToJson(data)
        }, null, 2);
      }
      return `Sector ${sectorName} (0x${command.toString(16).toUpperCase()})\n${toHexDump(data)}`;
    } finally {
      device.close();
    }
  }

  throw new Error(`Unknown command: ${options.command}`);
}

run(parseArgs(process.argv.slice(2)))
  .then((output) => {
    process.stdout.write(`${output}\n`);
  })
  .catch((error) => {
    process.stderr.write(`Error: ${error.message}\n`);
    process.exit(1);
  });
