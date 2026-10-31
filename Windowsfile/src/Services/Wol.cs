using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace NetPanel.Services
{
    /// <summary>
    /// Wake-on-LAN：发魔术包（6 个 FF + MAC ×16）到 UDP 9 / 7 广播。
    /// 必须由 Windows 侧发：minipc 的 LAN 网段是广播域，包能到设备网卡。
    /// </summary>
    public static class Wol
    {
        public static (bool Ok, string Msg) Send(string mac, string ipHint = "")
        {
            var macBytes = ParseMac(mac);
            if (macBytes == null) return (false, "MAC 格式不对：" + mac);

            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

            var targets = new List<IPAddress>();
            if (IPAddress.TryParse(ipHint, out var ip) && ip.GetAddressBytes().Length == 4)
            {
                var b = ip.GetAddressBytes();
                b[3] = 255;
                targets.Add(new IPAddress(b));      // 先按设备所在网段广播
            }
            targets.Add(IPAddress.Broadcast);       // 再发一次全局广播

            int sent = 0;
            foreach (var t in targets)
                foreach (var port in new[] { 9, 7 })
                {
                    try
                    {
                        using var udp = new UdpClient { EnableBroadcast = true };
                        udp.Send(packet, packet.Length, new IPEndPoint(t, port));
                        sent++;
                    }
                    catch { /* 某个网卡不通就跳过 */ }
                }

            var macCn = string.Join(":", Array.ConvertAll(macBytes, x => x.ToString("X2")));
            return sent > 0
                ? (true, $"已发送魔术包 → {macCn}")
                : (false, "魔术包没发出去（本机没有可广播的网卡？）");
        }

        private static byte[]? ParseMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return null;
            var s = mac.Replace("-", "").Replace(":", "").Replace(".", "").Trim();
            if (s.Length != 12) return null;
            var b = new byte[6];
            for (int i = 0; i < 6; i++)
                if (!byte.TryParse(s.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                    return null;
            return b;
        }
    }
}
