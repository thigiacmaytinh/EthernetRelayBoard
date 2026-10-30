using System;

namespace RelayControlNetFx
{
    /// <summary>
    /// Y&Q 4/8-channel TCP/IP simple binary relay protocol (manual V2.1, section 5.7).
    /// OPEN means energize (ON); CLOSE means de-energize (OFF).
    /// </summary>
    internal static class RelayProtocol
    {
        public const int MaximumChannel = 8;
        public const int AllChannels = 9;

        public static byte[] CreateCommand(int channel, bool open)
        {
            if (channel < 1 || channel > AllChannels)
            {
                throw new ArgumentOutOfRangeException("channel", "Channel must be 1 through 9 (9 means ALL).");
            }

            byte command = open ? (byte)(0xF0 + channel) : (byte)channel;
            return new byte[] { 0x00, command, 0xFF };
        }

        public static string ToHex(byte[] data, int length)
        {
            return BitConverter.ToString(data, 0, length).Replace('-', ' ');
        }
    }
}
