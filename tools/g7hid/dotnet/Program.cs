using System;
using System.Linq;
using HidSharp;
using HidSharp.Reports;

namespace g7hid {
    internal static class Program {
        private static int Main() {
            try {
                HidDevice device = DeviceList.Local
                    .GetHidDevices(vendorID: 0x320F)
                    .FirstOrDefault(IsG7VendorCollection);

                if (device == null) {
                    Console.Write(0);
                    return 0;
                }

                int battery;

                using (HidStream stream = device.Open()) {
                    stream.ReadTimeout = 2000;
                    stream.WriteTimeout = 2000;

                    if (device.ProductID == 0x22A4) {
                        battery = 100;
                    } else {
                        SendAndRead(stream, 0x19, 24);
                        byte[] response = SendAndRead(stream, 0x1A, 6);
                        battery = response[8];
                    }
                }

                if (battery < 0 || battery > 100) {
                    battery = 0;
                }

                Console.Write(battery);
                return 0;
            } catch {
                Console.Write(0);
                return 0;
            }
        }

        private static bool IsG7VendorCollection(HidDevice device) {
            if (device.ProductID != 0x226F && device.ProductID != 0x22A4) {
                return false;
            }

            if (device.GetMaxInputReportLength() != 64 ||
                device.GetMaxOutputReportLength() != 64) {
                return false;
            }

            try {
                ReportDescriptor descriptor = device.GetReportDescriptor();
                return descriptor.DeviceItems.Any(item =>
                    item.Usages.ContainsValue(0xFF1C0092));
            } catch {
                return false;
            }
        }

        private static byte[] SendAndRead(HidStream stream, byte command, byte length) {
            byte[] report = CreateReport(command, length);
            stream.Write(report);

            byte[] response = new byte[64];
            int total = 0;

            while (total < response.Length) {
                int read = stream.Read(response, total, response.Length - total);
                if (read <= 0) {
                    throw new InvalidOperationException();
                }
                total += read;
            }

            if (response[0] != 4 || response[3] != command) {
                throw new InvalidOperationException();
            }

            return response;
        }

        private static byte[] CreateReport(byte command, byte length) {
            byte[] report = new byte[64];
            report[0] = 4;
            report[3] = command;
            report[4] = length;
            return report;
        }
    }
}
