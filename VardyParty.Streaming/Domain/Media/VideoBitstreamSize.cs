namespace VardyParty.Streaming;

/// <summary>
/// Width and height parsed from a video bitstream prefix (H.264 / HEVC SPS),
/// not from playlist <c>RESOLUTION</c> metadata.
/// </summary>
public readonly record struct VideoPictureSize(int Width, int Height);

/// <summary>
/// Reads the real picture size from the start of an HLS media segment or
/// init (<c>EXT-X-MAP</c>) body: MPEG-TS, fMP4 / <c>avcC</c> / <c>hvcC</c>,
/// or a PNG/WebP-wrapped TS after unwrap. Never throws.
/// </summary>
public static class VideoBitstreamSize
{
    public static bool TryRead(ReadOnlySpan<byte> prefix, out VideoPictureSize size)
    {
        size = default;
        try
        {
            if (prefix.IsEmpty)
                return false;

            var kind = SegmentPayload.Classify(prefix);
            if (kind == SegmentPayloadKind.WrappedTs
                && SegmentPayload.TryUnwrap(prefix, out var ts)
                && ts.Length > 0)
            {
                return TryReadMpegTs(ts, out size);
            }

            return kind switch
            {
                SegmentPayloadKind.MpegTs => TryReadMpegTs(prefix, out size),
                SegmentPayloadKind.Fmp4 => TryReadFmp4(prefix, out size),
                SegmentPayloadKind.WrappedTs => false,
                _ => TryReadAnnexB(prefix, out size) || TryReadFmp4(prefix, out size)
            };
        }
        catch
        {
            size = default;
            return false;
        }
    }

    private static bool TryReadMpegTs(ReadOnlySpan<byte> body, out VideoPictureSize size)
    {
        size = default;
        var start = 0;
        while (start < body.Length && body[start] != 0x47)
            start++;

        // Concatenate TS payloads so start codes split across packets can be found.
        var payload = new byte[Math.Min(body.Length, SegmentPayload.ProbeBytes)];
        var length = 0;
        for (var offset = start; offset + 188 <= body.Length; offset += 188)
        {
            if (body[offset] != 0x47)
            {
                // Resync
                var next = offset + 1;
                while (next + 188 <= body.Length && body[next] != 0x47)
                    next++;
                if (next + 188 > body.Length)
                    break;
                offset = next;
            }

            var header = (body[offset + 1] << 8) | body[offset + 2];
            var adaptation = (body[offset + 3] >> 4) & 0x3;
            var payloadStart = offset + 4;
            if ((adaptation & 0x2) != 0)
            {
                if (payloadStart >= offset + 188)
                    continue;
                var afLength = body[payloadStart];
                payloadStart += 1 + afLength;
            }

            if ((adaptation & 0x1) == 0 || payloadStart >= offset + 188)
                continue;

            var take = (offset + 188) - payloadStart;
            if (length + take > payload.Length)
                take = payload.Length - length;
            if (take <= 0)
                break;
            body.Slice(payloadStart, take).CopyTo(payload.AsSpan(length, take));
            length += take;
            if (length >= payload.Length)
                break;
        }

        return length > 0 && TryReadAnnexB(payload.AsSpan(0, length), out size);
    }

    private static bool TryReadFmp4(ReadOnlySpan<byte> body, out VideoPictureSize size)
    {
        size = default;
        if (TryReadDecoderConfigBox(body, "avcC"u8, out var avcC) && TryReadAvcC(avcC, out size))
            return true;
        if (TryReadDecoderConfigBox(body, "hvcC"u8, out var hvcC) && TryReadHvcC(hvcC, out size))
            return true;
        // Some media segments carry an IDR (and SPS) in mdat without an init.
        return TryReadAnnexB(body, out size);
    }

    private static bool TryReadDecoderConfigBox(ReadOnlySpan<byte> body, ReadOnlySpan<byte> fourcc, out ReadOnlySpan<byte> record)
    {
        record = default;
        for (var i = 0; i + 8 <= body.Length; i++)
        {
            if (body[i + 4] != fourcc[0] || body[i + 5] != fourcc[1]
                || body[i + 6] != fourcc[2] || body[i + 7] != fourcc[3])
                continue;

            var size = ReadUInt32(body, i);
            if (size < 8)
                continue;
            var end = i + (int)size;
            if (end > body.Length)
                continue;
            record = body.Slice(i + 8, (int)size - 8);
            if (record.Length > 0 && record[0] == 1)
                return true;
        }

        return false;
    }

    private static bool TryReadAvcC(ReadOnlySpan<byte> record, out VideoPictureSize size)
    {
        size = default;
        if (record.Length < 7 || record[0] != 1)
            return false;

        var numSps = record[5] & 0x1F;
        var offset = 6;
        for (var i = 0; i < numSps; i++)
        {
            if (offset + 2 > record.Length)
                return false;
            var nalLength = (record[offset] << 8) | record[offset + 1];
            offset += 2;
            if (nalLength <= 0 || offset + nalLength > record.Length)
                return false;
            if (TryParseAvcSps(record.Slice(offset, nalLength), out size))
                return true;
            offset += nalLength;
        }

        return false;
    }

    private static bool TryReadHvcC(ReadOnlySpan<byte> record, out VideoPictureSize size)
    {
        size = default;
        // configurationVersion + profile_tier_level + fields; numOfArrays is at 22.
        if (record.Length < 23 || record[0] != 1)
            return false;

        var numArrays = record[22];
        var offset = 23;
        for (var a = 0; a < numArrays; a++)
        {
            if (offset + 3 > record.Length)
                return false;
            var nalType = record[offset] & 0x3F;
            var numNalus = (record[offset + 1] << 8) | record[offset + 2];
            offset += 3;
            for (var n = 0; n < numNalus; n++)
            {
                if (offset + 2 > record.Length)
                    return false;
                var nalLength = (record[offset] << 8) | record[offset + 1];
                offset += 2;
                if (nalLength <= 0 || offset + nalLength > record.Length)
                    return false;
                if (nalType == 33 && TryParseHevcSps(record.Slice(offset, nalLength), out size))
                    return true;
                offset += nalLength;
            }
        }

        return false;
    }

    private static bool TryReadAnnexB(ReadOnlySpan<byte> body, out VideoPictureSize size)
    {
        size = default;
        var i = 0;
        while (i < body.Length)
        {
            var start = FindStartCode(body, i, out var scSize);
            if (start < 0)
                return false;

            var nalStart = start + scSize;
            var next = FindStartCode(body, nalStart, out _);
            var nalEnd = next < 0 ? body.Length : next;
            var nal = body.Slice(nalStart, nalEnd - nalStart);
            if (nal.Length > 0)
            {
                var b0 = nal[0];
                // H.264: forbidden_zero_bit + nal_ref_idc + nal_unit_type
                if ((b0 & 0x80) == 0)
                {
                    var type = b0 & 0x1F;
                    if (type == 7 && TryParseAvcSps(nal, out size))
                        return true;
                }

                // HEVC: 2-byte header, type in bits 1..6 of the first byte.
                if (nal.Length > 2)
                {
                    var hevcType = (b0 >> 1) & 0x3F;
                    if (hevcType == 33 && TryParseHevcSps(nal, out size))
                        return true;
                }
            }

            i = nalEnd;
        }

        return false;
    }

    private static int FindStartCode(ReadOnlySpan<byte> body, int from, out int size)
    {
        size = 0;
        for (var i = from; i + 3 < body.Length; i++)
        {
            if (body[i] != 0 || body[i + 1] != 0)
                continue;
            if (body[i + 2] == 1)
            {
                size = 3;
                return i;
            }

            if (body[i + 2] == 0 && body[i + 3] == 1)
            {
                size = 4;
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseAvcSps(ReadOnlySpan<byte> nal, out VideoPictureSize size)
    {
        size = default;
        if (nal.Length < 5)
            return false;

        // Skip the NAL header (1 byte). The rest is RBSP.
        var reader = BitReader.FromRbsp(nal.Slice(1));
        if (reader is null)
            return false;

        var profileIdc = reader.ReadBits(8);
        reader.ReadBits(8); // constraint flags
        reader.ReadBits(8); // level_idc
        reader.ReadUe(); // seq_parameter_set_id

        var chromaFormatIdc = 1;
        var separateColourPlane = false;
        if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormatIdc = reader.ReadUe();
            if (chromaFormatIdc == 3)
                separateColourPlane = reader.ReadBit();
            reader.ReadUe(); // bit_depth_luma_minus8
            reader.ReadUe(); // bit_depth_chroma_minus8
            reader.ReadBit(); // qpprime_y_zero_transform_bypass_flag
            if (reader.ReadBit()) // seq_scaling_matrix_present_flag
            {
                var count = chromaFormatIdc != 3 ? 8 : 12;
                for (var i = 0; i < count; i++)
                {
                    if (reader.ReadBit())
                        reader.SkipScalingList(i < 6 ? 16 : 64);
                }
            }
        }

        reader.ReadUe(); // log2_max_frame_num_minus4
        var pocType = reader.ReadUe();
        if (pocType == 0)
        {
            reader.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            reader.ReadBit();
            reader.ReadSe();
            reader.ReadSe();
            var n = reader.ReadUe();
            for (var i = 0; i < n; i++)
                reader.ReadSe();
        }

        reader.ReadUe(); // max_num_ref_frames
        reader.ReadBit(); // gaps_in_frame_num_value_allowed_flag
        var picWidthInMbsMinus1 = reader.ReadUe();
        var picHeightInMapUnitsMinus1 = reader.ReadUe();
        var frameMbsOnly = reader.ReadBit();
        if (!frameMbsOnly)
            reader.ReadBit(); // mb_adaptive_frame_field_flag
        reader.ReadBit(); // direct_8x8_inference_flag

        var cropLeft = 0;
        var cropRight = 0;
        var cropTop = 0;
        var cropBottom = 0;
        if (reader.ReadBit())
        {
            cropLeft = reader.ReadUe();
            cropRight = reader.ReadUe();
            cropTop = reader.ReadUe();
            cropBottom = reader.ReadUe();
        }

        if (reader.Overflow)
            return false;

        var width = (picWidthInMbsMinus1 + 1) * 16;
        var height = (2 - (frameMbsOnly ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16;
        int cropUnitX;
        int cropUnitY;
        if (separateColourPlane || chromaFormatIdc == 0)
        {
            cropUnitX = 1;
            cropUnitY = 2 - (frameMbsOnly ? 1 : 0);
        }
        else
        {
            var subW = chromaFormatIdc is 1 or 2 ? 2 : 1;
            var subH = chromaFormatIdc == 1 ? 2 : 1;
            cropUnitX = subW;
            cropUnitY = subH * (2 - (frameMbsOnly ? 1 : 0));
        }

        width -= cropUnitX * (cropLeft + cropRight);
        height -= cropUnitY * (cropTop + cropBottom);
        return TrySetSize(width, height, out size);
    }

    private static bool TryParseHevcSps(ReadOnlySpan<byte> nal, out VideoPictureSize size)
    {
        size = default;
        if (nal.Length < 6)
            return false;

        // 2-byte HEVC NAL header.
        var reader = BitReader.FromRbsp(nal.Slice(2));
        if (reader is null)
            return false;

        reader.ReadBits(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = reader.ReadBits(3);
        reader.ReadBit(); // sps_temporal_id_nesting_flag
        reader.SkipHevcProfileTierLevel(maxSubLayersMinus1);
        reader.ReadUe(); // sps_seq_parameter_set_id
        var chromaFormatIdc = reader.ReadUe();
        if (chromaFormatIdc == 3)
            reader.ReadBit();
        var width = reader.ReadUe();
        var height = reader.ReadUe();
        if (reader.ReadBit()) // conformance_window_flag
        {
            var left = reader.ReadUe();
            var right = reader.ReadUe();
            var top = reader.ReadUe();
            var bottom = reader.ReadUe();
            var subW = chromaFormatIdc is 1 or 2 ? 2 : 1;
            var subH = chromaFormatIdc == 1 ? 2 : 1;
            width -= subW * (left + right);
            height -= subH * (top + bottom);
        }

        if (reader.Overflow)
            return false;
        return TrySetSize(width, height, out size);
    }

    private static bool TrySetSize(int width, int height, out VideoPictureSize size)
    {
        if (width is >= 16 and <= 7680 && height is >= 16 and <= 4320)
        {
            size = new VideoPictureSize(width, height);
            return true;
        }

        size = default;
        return false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> body, int offset) =>
        ((uint)body[offset] << 24) | ((uint)body[offset + 1] << 16)
        | ((uint)body[offset + 2] << 8) | body[offset + 3];

    private sealed class BitReader
    {
        private readonly byte[] _bytes;
        private int _bitOffset;

        private BitReader(byte[] bytes) => _bytes = bytes;

        public bool Overflow { get; private set; }

        public static BitReader? FromRbsp(ReadOnlySpan<byte> rbspWithEmulation)
        {
            if (rbspWithEmulation.IsEmpty)
                return null;

            // Remove emulation-prevention bytes (0x03 after 0x0000).
            var dest = new byte[rbspWithEmulation.Length];
            var n = 0;
            var zeros = 0;
            for (var i = 0; i < rbspWithEmulation.Length; i++)
            {
                var b = rbspWithEmulation[i];
                if (zeros >= 2 && b == 0x03)
                {
                    zeros = 0;
                    continue;
                }

                dest[n++] = b;
                zeros = b == 0 ? zeros + 1 : 0;
            }

            Array.Resize(ref dest, n);
            return new BitReader(dest);
        }

        public bool ReadBit()
        {
            var byteIndex = _bitOffset >> 3;
            if (byteIndex >= _bytes.Length)
            {
                Overflow = true;
                return false;
            }

            var bit = 7 - (_bitOffset & 7);
            _bitOffset++;
            return ((_bytes[byteIndex] >> bit) & 1) != 0;
        }

        public int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (ReadBit() ? 1 : 0);
            return value;
        }

        public int ReadUe()
        {
            var leading = 0;
            while (!ReadBit())
            {
                leading++;
                if (leading > 31 || Overflow)
                {
                    Overflow = true;
                    return 0;
                }
            }

            return leading == 0 ? 0 : ((1 << leading) - 1) + ReadBits(leading);
        }

        public int ReadSe()
        {
            var n = ReadUe();
            return (n & 1) == 0 ? -(n >> 1) : (n + 1) >> 1;
        }

        public void SkipScalingList(int size)
        {
            var lastScale = 8;
            var nextScale = 8;
            for (var i = 0; i < size; i++)
            {
                if (nextScale != 0)
                {
                    var delta = ReadSe();
                    nextScale = (lastScale + delta + 256) % 256;
                }

                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }

        public void SkipHevcProfileTierLevel(int maxSubLayersMinus1)
        {
            // general profile / tier / level: 88 bits + 8-bit level
            ReadBits(88);
            ReadBits(8);
            var profilePresent = new bool[Math.Max(maxSubLayersMinus1, 0)];
            var levelPresent = new bool[Math.Max(maxSubLayersMinus1, 0)];
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = ReadBit();
                levelPresent[i] = ReadBit();
            }

            if (maxSubLayersMinus1 > 0)
            {
                for (var i = maxSubLayersMinus1; i < 8; i++)
                    ReadBits(2);
            }

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i])
                    ReadBits(88);
                if (levelPresent[i])
                    ReadBits(8);
            }
        }
    }
}
