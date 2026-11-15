using System;
using System.Collections.Generic;
using Xunit;
using VardyParty.Streaming;

namespace VardyParty.Streaming.Tests;

public class VideoBitstreamSizeTests
{
    [Fact]
    public void TryRead_AnnexB_AvcBaseline1080()
    {
        // Arrange
        var annexB = VideoBitstreamFixtures.AnnexB(VideoBitstreamFixtures.AvcSps(1920, 1080));

        // Act
        var ok = VideoBitstreamSize.TryRead(annexB, out var size);

        // Assert
        Assert.True(ok);
        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
    }

    [Fact]
    public void TryRead_MpegTs_Avc720()
    {
        // Arrange
        var ts = VideoBitstreamFixtures.MpegTs(
            VideoBitstreamFixtures.AnnexB(VideoBitstreamFixtures.AvcSps(1280, 720)));

        // Act
        var ok = VideoBitstreamSize.TryRead(ts, out var size);

        // Assert
        Assert.True(ok);
        Assert.Equal(1280, size.Width);
        Assert.Equal(720, size.Height);
    }

    [Fact]
    public void TryRead_MpegTs_FindsSpsSplitAcrossPackets()
    {
        // Arrange
        var ts = VideoBitstreamFixtures.MpegTs(
            VideoBitstreamFixtures.AnnexB(VideoBitstreamFixtures.AvcSps(1920, 1080)),
            splitStartCode: true);

        // Act
        var ok = VideoBitstreamSize.TryRead(ts, out var size);

        // Assert
        Assert.True(ok);
        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
    }

    [Fact]
    public void TryRead_Fmp4_AvcCBox()
    {
        // Arrange
        var init = VideoBitstreamFixtures.AvcCBox(VideoBitstreamFixtures.AvcSps(1920, 1080));

        // Act
        var ok = VideoBitstreamSize.TryRead(init, out var size);

        // Assert
        Assert.True(ok);
        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
    }

    [Fact]
    public void TryRead_HtmlPrefix_ReturnsFalse()
    {
        // Arrange
        var html = "<html><body>503</body></html>"u8.ToArray();

        // Act
        var ok = VideoBitstreamSize.TryRead(html, out var size);

        // Assert
        Assert.False(ok);
        Assert.Equal(0, size.Width);
        Assert.Equal(0, size.Height);
    }

    [Fact]
    public void TryRead_Empty_ReturnsFalse()
    {
        // Arrange
        // Act
        var ok = VideoBitstreamSize.TryRead(ReadOnlySpan<byte>.Empty, out _);

        // Assert
        Assert.False(ok);
    }
}

/// <summary>Builds minimal H.264 SPS / TS / avcC prefixes for parser tests.</summary>
internal static class VideoBitstreamFixtures
{
    public static byte[] AvcSps(int width, int height)
    {
        // Baseline SPS with 4:2:0-style crop units (crop_unit_x=2, crop_unit_y=2).
        // Width = (pic_width_in_mbs_minus1+1)*16 - 2*(left+right)
        // Height = (pic_height_in_map_units_minus1+1)*16 - 2*(top+bottom)  (frame_mbs_only)
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Even width and height required.");

        var widthMbs = (width + 15) / 16;
        var heightMap = (height + 15) / 16;
        var cropRight = ((widthMbs * 16) - width) / 2;
        var cropBottom = ((heightMap * 16) - height) / 2;

        var bits = new BitWriter();
        bits.WriteBits(66, 8); // profile_idc baseline
        bits.WriteBits(0, 8);  // constraints
        bits.WriteBits(30, 8); // level_idc
        bits.WriteUe(0);       // seq_parameter_set_id
        bits.WriteUe(0);       // log2_max_frame_num_minus4
        bits.WriteUe(0);       // pic_order_cnt_type
        bits.WriteUe(0);       // log2_max_pic_order_cnt_lsb_minus4
        bits.WriteUe(1);       // max_num_ref_frames
        bits.WriteBit(false);  // gaps_in_frame_num_value_allowed_flag
        bits.WriteUe(widthMbs - 1);
        bits.WriteUe(heightMap - 1);
        bits.WriteBit(true);   // frame_mbs_only_flag
        bits.WriteBit(true);   // direct_8x8_inference_flag
        var crop = cropRight > 0 || cropBottom > 0;
        bits.WriteBit(crop);
        if (crop)
        {
            bits.WriteUe(0);
            bits.WriteUe(cropRight);
            bits.WriteUe(0);
            bits.WriteUe(cropBottom);
        }

        bits.WriteBit(false); // vui_parameters_present_flag
        bits.WriteBit(true);  // rbsp_stop_one_bit
        bits.ByteAlign();

        var rbsp = bits.ToArray();
        var nal = new byte[1 + rbsp.Length];
        nal[0] = 0x67; // nal_ref_idc=3, type=7
        Buffer.BlockCopy(rbsp, 0, nal, 1, rbsp.Length);
        return nal;
    }

    public static byte[] AnnexB(byte[] nal)
    {
        var body = new byte[4 + nal.Length];
        body[3] = 1;
        Buffer.BlockCopy(nal, 0, body, 4, nal.Length);
        return body;
    }

    public static byte[] AvcCBox(byte[] sps)
    {
        var recordLength = 6 + 2 + sps.Length;
        var box = new byte[8 + recordLength];
        var size = box.Length;
        box[0] = (byte)(size >> 24);
        box[1] = (byte)(size >> 16);
        box[2] = (byte)(size >> 8);
        box[3] = (byte)size;
        box[4] = (byte)'a';
        box[5] = (byte)'v';
        box[6] = (byte)'c';
        box[7] = (byte)'C';
        box[8] = 1; // configurationVersion
        box[9] = 66;
        box[10] = 0;
        box[11] = 30;
        box[12] = 0xFF; // lengthSizeMinusOne
        box[13] = 0xE1; // 1 SPS
        box[14] = (byte)(sps.Length >> 8);
        box[15] = (byte)sps.Length;
        Buffer.BlockCopy(sps, 0, box, 16, sps.Length);
        return box;
    }

    public static byte[] MpegTs(byte[] annexB, bool splitStartCode = false)
    {
        // PES: start code + stream_id + length + flags + 5-byte PTS (dummy).
        var pesHeader = new byte[] { 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05, 0x21, 0x00, 0x01, 0x00, 0x01 };
        var pes = new byte[pesHeader.Length + annexB.Length];
        Buffer.BlockCopy(pesHeader, 0, pes, 0, pesHeader.Length);
        Buffer.BlockCopy(annexB, 0, pes, pesHeader.Length, annexB.Length);

        const int packet = 188;
        const int pid = 0x100;
        var packets = new List<byte>();
        var offset = 0;
        var continuity = 0;
        var first = true;
        while (offset < pes.Length)
        {
            var pkt = new byte[packet];
            pkt[0] = 0x47;
            pkt[1] = (byte)((first ? 0x40 : 0x00) | (pid >> 8));
            pkt[2] = (byte)(pid & 0xFF);
            var payloadRoom = packet - 4;

            if (first && splitStartCode)
            {
                // Leave 3 bytes at the end of the first payload so 00 00 00 | 01 splits.
                var force = Math.Min(pes.Length, payloadRoom - 3);
                var stuffing = payloadRoom - force;
                pkt[3] = (byte)(0x30 | continuity); // adaptation + payload
                pkt[4] = (byte)(stuffing - 1);
                if (stuffing > 1)
                    pkt[5] = 0x00;
                Buffer.BlockCopy(pes, 0, pkt, 4 + stuffing, force);
                offset = force;
            }
            else
            {
                var take = Math.Min(pes.Length - offset, payloadRoom);
                var stuffing = payloadRoom - take;
                if (stuffing > 0)
                {
                    pkt[3] = (byte)(0x30 | continuity);
                    pkt[4] = (byte)(stuffing - 1);
                    if (stuffing > 1)
                        pkt[5] = 0x00;
                    Buffer.BlockCopy(pes, offset, pkt, 4 + stuffing, take);
                }
                else
                {
                    pkt[3] = (byte)(0x10 | continuity);
                    Buffer.BlockCopy(pes, offset, pkt, 4, take);
                }

                offset += take;
            }

            continuity = (continuity + 1) & 0xF;
            first = false;
            packets.AddRange(pkt);
        }

        return packets.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitInByte;
        private byte _current;

        public void WriteBit(bool value)
        {
            _current = (byte)((_current << 1) | (value ? 1 : 0));
            _bitInByte++;
            if (_bitInByte == 8)
            {
                _bytes.Add(_current);
                _current = 0;
                _bitInByte = 0;
            }
        }

        public void WriteBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                WriteBit(((value >> i) & 1) != 0);
        }

        public void WriteUe(int value)
        {
            var n = value + 1;
            var leading = 0;
            var tmp = n;
            while (tmp > 1)
            {
                tmp >>= 1;
                leading++;
            }

            for (var i = 0; i < leading; i++)
                WriteBit(false);
            WriteBits(n, leading + 1);
        }

        public void ByteAlign()
        {
            while (_bitInByte != 0)
                WriteBit(false);
        }

        public byte[] ToArray()
        {
            ByteAlign();
            return [.. _bytes];
        }
    }
}
