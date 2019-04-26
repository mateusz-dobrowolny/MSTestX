﻿/// A lot of this class is based on implementation from SharpAdbClient (https://github.com/quamotion/madb)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MSTestX.Console.Adb
{
    // Good command-reference overview here: https://android.googlesource.com/platform/system/core/+/master/adb/SERVICES.TXT
    internal class AdbClient
    {
        private int port;
        private int APILevel = 0;

        public AdbClient(int port=5037)
        {
            this.port = port;
        }

        public async Task TurnOnDisplayAsync(string deviceId)
        {
            await SendShellCommandAsync("input keyevent 26", deviceId); //Unlock button
            await SendShellCommandAsync("input touchscreen swipe 930 880 930 380", deviceId); //Swipe up
        }

        public async Task UnlockAsync(string deviceId, string pin = null)
        {
            await SendShellCommandAsync("input text " + pin.ToString(), deviceId);
            await SendShellCommandAsync("input keyevent 66", deviceId); //Enter
        }

        public async Task<List<Device>> GetDevicesAsync()
        {
            var devicesData = await SendCommandAndReceiveAsync("host:devices-l", null).ConfigureAwait(false);
            string[] strDevices = devicesData?.Split('\n');
            List<Device> devices = new List<Device>();
            foreach (var item in strDevices)
            {
                var d = Device.FromDataString(item);
                if (d != null)
                    devices.Add(d);
            }
            return devices;
        }

        internal static Encoding Encoding { get; } = Encoding.GetEncoding("ISO-8859-1");

        public async Task<int> GetAPILevelAsync(string deviceId)
        {
            if (APILevel == 0)
            {
                var APILevelData = await SendCommandAndReceiveDataAsync("shell: getprop ro.build.version.sdk", deviceId).ConfigureAwait(false);
                int.TryParse(Encoding.ASCII.GetString(APILevelData), out APILevel);
            }
            return APILevel;
        }

        public async Task<int> GetProcessId(string apk_id, string deviceId)
        {
            var level = await GetAPILevelAsync(deviceId).ConfigureAwait(false);
            if (level >= 24)
            {
                var pid = await SendCommandAndReceiveDataAsync($"shell: pidof {apk_id}", deviceId);

                if (pid.Length > 0 && int.TryParse(Encoding.UTF8.GetString(pid), out int id))
                    return id;
            }
            else
            {
                var ps = Encoding.UTF8.GetString(await SendCommandAndReceiveDataAsync("shell: ps", deviceId));
                var processes = ps.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var process = processes.Where(p => p.Trim().EndsWith(apk_id)).FirstOrDefault();
                if(process != null)
                {
                    var data = process.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
                    if (data.Length > 1 && int.TryParse(data[1], out int id))
                        return id;
                }
            }
            return -1;
        }

        public Task SendShellCommandAsync(string command, string deviceId)
        {
            return SendCommandAsync("shell:" + command, deviceId);
        }

        public async Task SendCommandAsync(string command, string deviceId)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            Action<string> Send = (data) =>
            {
                string resultStr = string.Format("{0}{1}\n", data.Length.ToString("X4"), data);
                var buff = Encoding.GetBytes(resultStr);
                s.Send(buff, 0, buff.Length, System.Net.Sockets.SocketFlags.None);
            };
            byte[] buffer = new byte[4];
            int count = 0;
            string response;
            if (!string.IsNullOrEmpty(deviceId))
            {
                Send($"host:transport:" + deviceId);
                count = await s.ReceiveAsync(buffer, 0, 4, System.Net.Sockets.SocketFlags.None, CancellationToken.None).ConfigureAwait(true);
                response = Encoding.GetString(buffer, 0, count);
                if (response != "OKAY")
                {
                    var error = await s.ReadString().ConfigureAwait(false);
                    s.Dispose();
                    throw new Exception(error);
                }
            }
            Send(command);
            count = await s.ReceiveAsync(buffer, 0, 4, System.Net.Sockets.SocketFlags.None, CancellationToken.None).ConfigureAwait(true);
            response = Encoding.GetString(buffer, 0, count);
            if (response != "OKAY")
            {
                var error = await s.ReadString().ConfigureAwait(false);
                s.Dispose();
                throw new Exception($"ADB Error sending command '{command}': {error}");
            }
            //Disposing too fast causes the command to not execute
            var _ = s.ReceiveAsync(buffer, 0, 1, SocketFlags.None, default).ContinueWith(t => s.Dispose());
        }

        public async Task<string> SendCommandAndReceiveAsync(string command, string deviceId)
        {
            var data = await SendCommandAndReceiveDataAsync(command, deviceId).ConfigureAwait(false);
            string lenHex = Encoding.UTF8.GetString(data, 0, 4);
            int len = int.Parse(lenHex, NumberStyles.HexNumber);
            if (len == 0) return string.Empty;
            return Encoding.UTF8.GetString(data, 4, len);
        }

        public async Task<byte[]> SendCommandAndReceiveDataAsync(string command, string deviceId)
        {
            using (var s = await SendCommandAndStartReceiveDataAsync(command, deviceId))
            {
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    int count = -1;
                    byte[] buffer = new byte[4096];
                    while (count != 0)
                    {
                        count = await s.ReceiveAsync(buffer, 0, 4096, System.Net.Sockets.SocketFlags.None, CancellationToken.None).ConfigureAwait(true);
                        ms.Write(buffer, 0, count);
                    }
                    return ms.ToArray();
                }
            }
        }

        private async Task<Socket> SendCommandAndStartReceiveDataAsync(string command, string deviceId)
        {
            var s = await StartSendCommandAsync(deviceId);
            Action<string> Send = (data) =>
            {
                string resultStr = string.Format("{0}{1}\n", data.Length.ToString("X4"), data);
                var buff = Encoding.UTF8.GetBytes(resultStr);
                s.Send(buff, 0, buff.Length, System.Net.Sockets.SocketFlags.None);
            };

            Send(command);
            byte[] buffer = new byte[4];
            int count = await s.ReceiveAsync(buffer, 0, 4, System.Net.Sockets.SocketFlags.None, CancellationToken.None).ConfigureAwait(true);
            string response = Encoding.UTF8.GetString(buffer, 0, count);
            if (response != "OKAY")
            {
                var error = await s.ReadString().ConfigureAwait(false);
                throw new Exception(error);
            }
            return s;
        }

        public async Task<Socket> StartSendCommandAsync(string deviceId)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            Action<string> Send = (data) =>
            {
                string resultStr = string.Format("{0}{1}\n", data.Length.ToString("X4"), data);
                var buff = Encoding.UTF8.GetBytes(resultStr);
                s.Send(buff, 0, buff.Length, System.Net.Sockets.SocketFlags.None);
            };
            byte[] buffer = new byte[4];
            int count = 0;
            string response;
            if (!string.IsNullOrEmpty(deviceId))
            {
                Send($"host:transport:" + deviceId);
                count = await s.ReceiveAsync(buffer, 0, 4, System.Net.Sockets.SocketFlags.None, CancellationToken.None).ConfigureAwait(true);
                response = Encoding.UTF8.GetString(buffer, 0, count);
                if (response != "OKAY")
                {
                    var error = await s.ReadString().ConfigureAwait(false);
                    throw new Exception(error);
                }
            }
            return s;
        }

        public async Task InstallApk(string deviceId, string apkFilepath)
        {
            using (var file = System.IO.File.OpenRead(apkFilepath))
                await InstallApk(deviceId, file);
        }

        public async Task InstallApk(string deviceId, Stream apk)
        {
            if (apk == null)
            {
                throw new ArgumentNullException(nameof(apk));
            }

            if (!apk.CanRead || !apk.CanSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(apk), "The apk stream must be a readable and seekable stream");
            }
            string command = $"exec:cmd package 'install' -S {apk.Length}";
            using (var s = await SendCommandAndStartReceiveDataAsync(command, deviceId))
            {
                byte[] buffer = new byte[32 * 1024];
                int read = 0;

                while ((read = apk.Read(buffer, 0, buffer.Length)) > 0)
                {
                    s.Send(buffer, read, SocketFlags.None);
                }

                read = s.Receive(buffer);
                var value = Encoding.UTF8.GetString(buffer, 0, read);
                if (!string.Equals(value, "Success\n"))
                {
                    throw new Exception(value);
                }
            }
        }
    }
}
