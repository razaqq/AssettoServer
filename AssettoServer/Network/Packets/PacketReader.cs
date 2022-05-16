﻿using AssettoServer.Network.Packets.Incoming;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssettoServer.Network.Packets
{
    public struct PacketReader
    {
        public readonly Stream? Stream;
        public Memory<byte> Buffer { get; private set; }
        public int ReadPosition { get; private set; }

        private bool _readPacket;

        public PacketReader(Stream? stream, Memory<byte> buffer)
        {
            Stream = stream;
            Buffer = buffer;

            _readPacket = false;
            ReadPosition = 0;
        }

        public string ReadASCIIString(bool bigLength = false)
        {
            short stringLength = bigLength ? Read<short>() : Read<byte>();
            
            var ret = string.Create(stringLength, this, (span, self) => Encoding.ASCII.GetChars(self.Buffer.Slice(self.ReadPosition, span.Length).Span, span));
            ReadPosition += stringLength;

            return ret;
        }

        public string ReadUTF32String()
        {
            byte stringLength = Read<byte>();

            var ret = string.Create(stringLength, this, (span, self) => Encoding.UTF32.GetChars(self.Buffer.Slice(self.ReadPosition, span.Length * 4).Span, span));
            ReadPosition += stringLength * 4;

            return ret;
        }

        public T Read<T>() where T : unmanaged
        {
            T result = MemoryMarshal.Read<T>(Buffer.Slice(ReadPosition).Span);
            ReadPosition += Marshal.SizeOf(typeof(T).IsEnum ? Enum.GetUnderlyingType(typeof(T)) : typeof(T));

            return result;
        }

        public void ReadBytes(Memory<byte> buffer)
        {
            Buffer.Slice(ReadPosition, buffer.Length).CopyTo(buffer);
            ReadPosition += buffer.Length;
        }

        public TPacket ReadPacket<TPacket>() where TPacket : struct, IIncomingNetworkPacket
        {
            TPacket packet = default;
            packet.FromReader(this);

            return packet;
        }

        public async ValueTask<int> ReadPacketAsync(CancellationToken cancellationToken = default)
        {
            if (_readPacket)
                return 0;

            _readPacket = true;
            await ReadBytesInternalAsync(Buffer.Slice(0, 2), cancellationToken);

            int packetSize = MemoryMarshal.Read<ushort>(Buffer.Span);
            if (packetSize > Buffer.Length)
            {
                Buffer.Span.Clear();
                return 0;
            }

            Buffer = Buffer.Slice(0, packetSize);

            await ReadBytesInternalAsync(Buffer, cancellationToken);

            return packetSize;
        }

        internal void SliceBuffer(int newSize)
        {
            Buffer = Buffer.Slice(0, newSize);
        }

        private async ValueTask ReadBytesInternalAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Stream == null)
                throw new ArgumentNullException(nameof(Stream));

            int totalBytesRead = 0;
            int bytesRead;
            int bufferLength = buffer.Length;
            
            while ((bytesRead = await Stream.ReadAsync(buffer.Slice(totalBytesRead), cancellationToken)) > 0 && (totalBytesRead += bytesRead) < bufferLength) { }
        }
    }
}
