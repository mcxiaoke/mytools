function toHex(bytes) {
  return Array.from(Buffer.from(bytes))
    .map((byte) => byte.toString(16).padStart(2, '0').toUpperCase())
    .join(' ');
}

function toHexDump(bytes, base = 0) {
  const buffer = Buffer.from(bytes);
  const lines = [];

  for (let offset = 0; offset < buffer.length; offset += 16) {
    const chunk = buffer.slice(offset, offset + 16);
    const address = (base + offset).toString(16).padStart(4, '0').toUpperCase();
    const hexPart = Array.from(chunk)
      .map((byte) => byte.toString(16).padStart(2, '0').toUpperCase())
      .join(' ')
      .padEnd(47, ' ');
    const asciiPart = Array.from(chunk)
      .map((byte) => byte >= 0x20 && byte <= 0x7e ? String.fromCharCode(byte) : '.')
      .join('');

    lines.push(`${address}: ${hexPart} ${asciiPart}`);
  }

  return lines.join('\n');
}

function toJsonDevice(device) {
  return {
    vendorId: device.vendorId,
    productId: device.productId,
    path: device.path,
    manufacturer: device.manufacturer,
    product: device.product,
    release: device.release,
    interface: device.interface,
    usagePage: device.usagePage,
    usage: device.usage
  };
}

function bufferToJson(bytes) {
  return {
    length: bytes.length,
    hex: toHex(bytes),
    bytes: Array.from(Buffer.from(bytes))
  };
}

module.exports = {
  toHex,
  toHexDump,
  toJsonDevice,
  bufferToJson
};
