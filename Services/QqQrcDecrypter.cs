using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicPlayer.Services;

/// <summary>
/// Decrypts QQ Music QRC lyrics (the encrypted format returned by
/// c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg).
///
/// The response is an XML document whose <content>/<contentts>/<contentroma>
/// elements contain hex-encoded, 3DES(ECB)+zlib compressed data. The inner
/// decrypted XML exposes the lyrics via its <Lyric_1 LyricContent="..."> node.
/// Algorithm verified against the live QQ service (key !@#)(*$%123ZXC!@!@#)(NHL).
/// </summary>
public static class QqQrcDecrypter
{
    private static readonly byte[] QqKey = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    /// <summary>
    /// Decrypt a single hex-encoded QRC blob. Returns the original string if it
    /// is not hex (QQ returns plaintext occasionally), matching upstream behavior.
    /// </summary>
    public static string Decrypt(string encrypted)
    {
        if (!IsHex(encrypted))
            return encrypted;

        var bytes = HexToBytes(encrypted);
        byte[] data = new byte[bytes.Length];

        byte[][][] schedule = new byte[3][][];
        for (int i = 0; i < 3; i++)
        {
            schedule[i] = new byte[16][];
            for (int j = 0; j < 16; j++)
                schedule[i][j] = new byte[6];
        }
        DesHelper.TripleDesKeySetup(QqKey, schedule, DesHelper.Decrypt);

        // 3DES works on whole 8-byte blocks. The service normally returns an
        // aligned payload, but a truncated/corrupt response must not turn into
        // an IndexOutOfRangeException (it would be swallowed upstream and look
        // like "no lyrics"), so decrypt only whole blocks and pass the rest through.
        int aligned = bytes.Length - (bytes.Length % 8);
        for (int i = 0; i < aligned; i += 8)
        {
            var temp = new byte[8];
            DesHelper.TripleDesCrypt(bytes[i..], temp, schedule);
            for (int j = 0; j < 8; j++)
                data[i + j] = temp[j];
        }
        for (int i = aligned; i < bytes.Length; i++)
            data[i] = bytes[i];

        using var compressed = new MemoryStream(data);
        using var decompressed = new MemoryStream();
        using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        zlib.CopyTo(decompressed);
        return Encoding.UTF8.GetString(decompressed.ToArray());
    }

    /// <summary>
    /// Parse a full QRC response and return (original, translation, romaji).
    /// Each field may be null when the service has no such version.
    /// </summary>
    public static (string? Lyric, string? Trans, string? Roma) ParseQrc(string qrcXml)
    {
        qrcXml = qrcXml.Replace("<!--", "").Replace("-->", "");

        string? DecodeTag(string tag)
        {
            try
            {
                // The wrapper response is NOT well-formed XML: it contains an
                // unclosed <command-lable-*> header, so XmlDocument.LoadXml on
                // the whole payload always throws. Extract the CDATA payload
                // with a regex instead.
                var m = Regex.Match(
                    qrcXml,
                    "<" + tag + "\\b[^>]*>\\s*<!\\[CDATA\\[(.*?)\\]\\]>\\s*</" + tag + ">",
                    RegexOptions.Singleline);
                if (!m.Success)
                    return null;

                var raw = m.Groups[1].Value.Trim();
                if (raw.Length == 0)
                    return null;

                var dec = Decrypt(raw);
                return ToLrc(ExtractLyricContent(dec));
            }
            catch
            {
                return null;
            }
        }

        return (DecodeTag("content"), DecodeTag("contentts"), DecodeTag("contentroma"));
    }

    /// <summary>
    /// Pull the lyric text out of the decrypted inner QRC document.
    /// The inner document is read with a regex (not XmlDocument) on purpose:
    /// XML attribute-value normalization would collapse the newlines that
    /// separate LRC timestamp lines, destroying the lyric structure.
    /// </summary>
    private static string ExtractLyricContent(string decrypted)
    {
        if (string.IsNullOrEmpty(decrypted))
            return decrypted;

        var m = Regex.Match(
            decrypted,
            "<Lyric_1\\b[^>]*?LyricContent\\s*=\\s*\"([^\"]*)\"",
            RegexOptions.Singleline);
        if (!m.Success)
        {
            // Already plain LRC, or an unrecognised shape — return as-is.
            return decrypted;
        }

        // Decode the XML entities (&amp; &lt; &gt; &quot; &apos;) that were
        // escaped inside the attribute value.
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    /// <summary>
    /// Convert decrypted QRC text into standard LRC, which is what the rest of
    /// the app parses. QRC encodes word-level timing:
    ///     [startMs,durationMs]word(start,dur) word(start,dur)...
    /// while LRC needs:
    ///     [mm:ss.xx]text
    /// Lines that are already LRC/metadata ([ti:..], [ar:..]) pass through.
    /// </summary>
    public static string ToLrc(string qrc)
    {
        if (string.IsNullOrEmpty(qrc))
            return qrc;

        var sb = new StringBuilder();
        foreach (var rawLine in qrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var m = Regex.Match(line, @"^\[\s*(\d+)\s*,\s*(\d+)\s*\](.*)$");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out var startMs))
            {
                // Metadata or already-LRC line — keep verbatim.
                sb.Append(line).Append('\n');
                continue;
            }

            sb.Append('[').Append(FormatLrcTime(startMs)).Append(']')
              .Append(WordsToText(m.Groups[3].Value)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Join the word fragments of a QRC line body, dropping the per-word
    /// "(start,duration)" annotations. "secret(0,155) (155,155)base(310,155)"
    /// becomes "secret base".
    /// </summary>
    private static string WordsToText(string body)
    {
        var segs = Regex.Matches(body, @"([^()]*)\(\s*-?\d+\s*,\s*-?\d+\s*\)");
        if (segs.Count == 0)
            return body.Trim();

        var sb = new StringBuilder();
        foreach (Match s in segs)
            sb.Append(s.Groups[1].Value);
        return sb.ToString().Trim();
    }

    private static string FormatLrcTime(long ms)
    {
        if (ms < 0) ms = 0;
        long mm = ms / 60000;
        long ss = (ms % 60000) / 1000;
        long xx = (ms % 1000) / 10;
        return $"{mm:D2}:{ss:D2}.{xx:D2}";
    }

    private static byte[] HexToBytes(string hex)
    {
        int len = hex.Length;
        var bytes = new byte[len / 2];
        for (int i = 0; i < len; i += 2)
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        return bytes;
    }

    private static bool IsHex(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || (s.Length % 2) != 0)
            return false;
        foreach (var c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Custom 3DES (DES-EDE) implementation matching QQ Music's cipher.</summary>
    private static class DesHelper
    {
        public const uint Encrypt = 1;
        public const uint Decrypt = 0;

        private static uint BitNum(byte[] a, int b, int c) =>
            ((uint)((a[b / 32 * 4 + 3 - b % 32 / 8] >> (7 - b % 8)) & 0x01) << c);
        private static byte BitNumIntR(uint a, int b, int c) =>
            (byte)((((a) >> (31 - b)) & 0x00000001) << c);
        private static uint BitNumIntL(uint a, int b, int c) =>
            (((a << b) & 0x80000000) >> c);
        private static uint SboxBit(byte a) =>
            (uint)((a & 0x20) | ((a & 0x1f) >> 1) | ((a & 0x01) << 4));

        private static readonly byte[] Sbox1 = {14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6, 2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13};
        private static readonly byte[] Sbox2 = {15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9};
        private static readonly byte[] Sbox3 = {10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12};
        private static readonly byte[] Sbox4 = {7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14};
        private static readonly byte[] Sbox5 = {2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3};
        private static readonly byte[] Sbox6 = {12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13};
        private static readonly byte[] Sbox7 = {4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12};
        private static readonly byte[] Sbox8 = {13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11};

        public static void TripleDesKeySetup(byte[] key, byte[][][] schedule, uint mode)
        {
            if (mode == Encrypt)
            {
                KeySchedule(key[0..], schedule[0], mode);
                KeySchedule(key[8..], schedule[1], Decrypt);
                KeySchedule(key[16..], schedule[2], mode);
            }
            else
            {
                KeySchedule(key[0..], schedule[2], mode);
                KeySchedule(key[8..], schedule[1], Encrypt);
                KeySchedule(key[16..], schedule[0], mode);
            }
        }

        public static void TripleDesCrypt(byte[] input, byte[] output, byte[][][] key)
        {
            Crypt(input, output, key[0]);
            Crypt(output, output, key[1]);
            Crypt(output, output, key[2]);
        }

        private static void KeySchedule(byte[] key, byte[][] schedule, uint mode)
        {
            uint i, j, toGen, C, D;
            uint[] keyRndShift = {1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1};
            uint[] keyPermC = {56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35};
            uint[] keyPermD = {62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3};
            uint[] keyCompression = {13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31};

            for (i = 0, j = 31, C = 0; i < 28; ++i, --j)
                C |= BitNum(key, (int)keyPermC[i], (int)j);
            for (i = 0, j = 31, D = 0; i < 28; ++i, --j)
                D |= BitNum(key, (int)keyPermD[i], (int)j);

            for (i = 0; i < 16; ++i)
            {
                C = ((C << (int)keyRndShift[i]) | (C >> (28 - (int)keyRndShift[i]))) & 0xfffffff0;
                D = ((D << (int)keyRndShift[i]) | (D >> (28 - (int)keyRndShift[i]))) & 0xfffffff0;

                toGen = (mode == Decrypt) ? (15 - i) : i;

                for (j = 0; j < 6; ++j) schedule[toGen][j] = 0;
                for (j = 0; j < 24; ++j)
                    schedule[toGen][j / 8] |= BitNumIntR(C, (int)keyCompression[j], (int)(7 - j % 8));
                for (; j < 48; ++j)
                    schedule[toGen][j / 8] |= BitNumIntR(D, (int)keyCompression[j] - 27, (int)(7 - j % 8));
            }
        }

        private static void Ip(uint[] state, byte[] input)
        {
            state[0] = BitNum(input,57,31)|BitNum(input,49,30)|BitNum(input,41,29)|BitNum(input,33,28)|BitNum(input,25,27)|BitNum(input,17,26)|BitNum(input,9,25)|BitNum(input,1,24)|BitNum(input,59,23)|BitNum(input,51,22)|BitNum(input,43,21)|BitNum(input,35,20)|BitNum(input,27,19)|BitNum(input,19,18)|BitNum(input,11,17)|BitNum(input,3,16)|BitNum(input,61,15)|BitNum(input,53,14)|BitNum(input,45,13)|BitNum(input,37,12)|BitNum(input,29,11)|BitNum(input,21,10)|BitNum(input,13,9)|BitNum(input,5,8)|BitNum(input,63,7)|BitNum(input,55,6)|BitNum(input,47,5)|BitNum(input,39,4)|BitNum(input,31,3)|BitNum(input,23,2)|BitNum(input,15,1)|BitNum(input,7,0);
            state[1] = BitNum(input,56,31)|BitNum(input,48,30)|BitNum(input,40,29)|BitNum(input,32,28)|BitNum(input,24,27)|BitNum(input,16,26)|BitNum(input,8,25)|BitNum(input,0,24)|BitNum(input,58,23)|BitNum(input,50,22)|BitNum(input,42,21)|BitNum(input,34,20)|BitNum(input,26,19)|BitNum(input,18,18)|BitNum(input,10,17)|BitNum(input,2,16)|BitNum(input,60,15)|BitNum(input,52,14)|BitNum(input,44,13)|BitNum(input,36,12)|BitNum(input,28,11)|BitNum(input,20,10)|BitNum(input,12,9)|BitNum(input,4,8)|BitNum(input,62,7)|BitNum(input,54,6)|BitNum(input,46,5)|BitNum(input,38,4)|BitNum(input,30,3)|BitNum(input,22,2)|BitNum(input,14,1)|BitNum(input,6,0);
        }

        private static void InvIp(uint[] state, byte[] input)
        {
            input[3] = (byte)(BitNumIntR(state[1],7,7)|BitNumIntR(state[0],7,6)|BitNumIntR(state[1],15,5)|BitNumIntR(state[0],15,4)|BitNumIntR(state[1],23,3)|BitNumIntR(state[0],23,2)|BitNumIntR(state[1],31,1)|BitNumIntR(state[0],31,0));
            input[2] = (byte)(BitNumIntR(state[1],6,7)|BitNumIntR(state[0],6,6)|BitNumIntR(state[1],14,5)|BitNumIntR(state[0],14,4)|BitNumIntR(state[1],22,3)|BitNumIntR(state[0],22,2)|BitNumIntR(state[1],30,1)|BitNumIntR(state[0],30,0));
            input[1] = (byte)(BitNumIntR(state[1],5,7)|BitNumIntR(state[0],5,6)|BitNumIntR(state[1],13,5)|BitNumIntR(state[0],13,4)|BitNumIntR(state[1],21,3)|BitNumIntR(state[0],21,2)|BitNumIntR(state[1],29,1)|BitNumIntR(state[0],29,0));
            input[0] = (byte)(BitNumIntR(state[1],4,7)|BitNumIntR(state[0],4,6)|BitNumIntR(state[1],12,5)|BitNumIntR(state[0],12,4)|BitNumIntR(state[1],20,3)|BitNumIntR(state[0],20,2)|BitNumIntR(state[1],28,1)|BitNumIntR(state[0],28,0));
            input[7] = (byte)(BitNumIntR(state[1],3,7)|BitNumIntR(state[0],3,6)|BitNumIntR(state[1],11,5)|BitNumIntR(state[0],11,4)|BitNumIntR(state[1],19,3)|BitNumIntR(state[0],19,2)|BitNumIntR(state[1],27,1)|BitNumIntR(state[0],27,0));
            input[6] = (byte)(BitNumIntR(state[1],2,7)|BitNumIntR(state[0],2,6)|BitNumIntR(state[1],10,5)|BitNumIntR(state[0],10,4)|BitNumIntR(state[1],18,3)|BitNumIntR(state[0],18,2)|BitNumIntR(state[1],26,1)|BitNumIntR(state[0],26,0));
            input[5] = (byte)(BitNumIntR(state[1],1,7)|BitNumIntR(state[0],1,6)|BitNumIntR(state[1],9,5)|BitNumIntR(state[0],9,4)|BitNumIntR(state[1],17,3)|BitNumIntR(state[0],17,2)|BitNumIntR(state[1],25,1)|BitNumIntR(state[0],25,0));
            input[4] = (byte)(BitNumIntR(state[1],0,7)|BitNumIntR(state[0],0,6)|BitNumIntR(state[1],8,5)|BitNumIntR(state[0],8,4)|BitNumIntR(state[1],16,3)|BitNumIntR(state[0],16,2)|BitNumIntR(state[1],24,1)|BitNumIntR(state[0],24,0));
        }

        private static uint F(uint state, byte[] key)
        {
            byte[] lrg = new byte[6];
            uint t1, t2;
            t1 = BitNumIntL(state,31,0)|((state&0xf0000000)>>1)|BitNumIntL(state,4,5)|BitNumIntL(state,3,6)|((state&0x0f000000)>>3)|BitNumIntL(state,8,11)|BitNumIntL(state,7,12)|((state&0x00f00000)>>5)|BitNumIntL(state,12,17)|BitNumIntL(state,11,18)|((state&0x000f0000)>>7)|BitNumIntL(state,16,23);
            t2 = BitNumIntL(state,15,0)|((state&0x0000f000)<<15)|BitNumIntL(state,20,5)|BitNumIntL(state,19,6)|((state&0x00000f00)<<13)|BitNumIntL(state,24,11)|BitNumIntL(state,23,12)|((state&0x000000f0)<<11)|BitNumIntL(state,28,17)|BitNumIntL(state,27,18)|((state&0x0000000f)<<9)|BitNumIntL(state,0,23);
            lrg[0]=(byte)((t1>>24)&0xff);
            lrg[1]=(byte)((t1>>16)&0xff);
            lrg[2]=(byte)((t1>>8)&0xff);
            lrg[3]=(byte)((t2>>24)&0xff);
            lrg[4]=(byte)((t2>>16)&0xff);
            lrg[5]=(byte)((t2>>8)&0xff);
            lrg[0]^=key[0]; lrg[1]^=key[1]; lrg[2]^=key[2]; lrg[3]^=key[3]; lrg[4]^=key[4]; lrg[5]^=key[5];

            int b0 = (int)SboxBit((byte)(lrg[0]>>2));
            int b1 = (int)SboxBit((byte)(((lrg[0]&0x03)<<4)|(lrg[1]>>4)));
            int b2 = (int)SboxBit((byte)(((lrg[1]&0x0f)<<2)|(lrg[2]>>6)));
            int b3 = (int)SboxBit((byte)(lrg[2]&0x3f));
            int b4 = (int)SboxBit((byte)(lrg[3]>>2));
            int b5 = (int)SboxBit((byte)(((lrg[3]&0x03)<<4)|(lrg[4]>>4)));
            int b6 = (int)SboxBit((byte)(((lrg[4]&0x0f)<<2)|(lrg[5]>>6)));
            int b7 = (int)SboxBit((byte)(lrg[5]&0x3f));

            uint s = ((uint)Sbox1[b0] << 28) |
                     ((uint)Sbox2[b1] << 24) |
                     ((uint)Sbox3[b2] << 20) |
                     ((uint)Sbox4[b3] << 16) |
                     ((uint)Sbox5[b4] << 12) |
                     ((uint)Sbox6[b5] << 8)  |
                     ((uint)Sbox7[b6] << 4)  |
                     (uint)  Sbox8[b7];

            s = BitNumIntL(s,15,0)|BitNumIntL(s,6,1)|BitNumIntL(s,19,2)|BitNumIntL(s,20,3)|BitNumIntL(s,28,4)|BitNumIntL(s,11,5)|BitNumIntL(s,27,6)|BitNumIntL(s,16,7)|BitNumIntL(s,0,8)|BitNumIntL(s,14,9)|BitNumIntL(s,22,10)|BitNumIntL(s,25,11)|BitNumIntL(s,4,12)|BitNumIntL(s,17,13)|BitNumIntL(s,30,14)|BitNumIntL(s,9,15)|BitNumIntL(s,1,16)|BitNumIntL(s,7,17)|BitNumIntL(s,23,18)|BitNumIntL(s,13,19)|BitNumIntL(s,31,20)|BitNumIntL(s,26,21)|BitNumIntL(s,2,22)|BitNumIntL(s,8,23)|BitNumIntL(s,18,24)|BitNumIntL(s,12,25)|BitNumIntL(s,29,26)|BitNumIntL(s,5,27)|BitNumIntL(s,21,28)|BitNumIntL(s,10,29)|BitNumIntL(s,3,30)|BitNumIntL(s,24,31);
            return s;
        }

        private static void Crypt(byte[] input, byte[] output, byte[][] key)
        {
            uint[] state = new uint[2];
            uint idx, t;
            Ip(state, input);
            for (idx = 0; idx < 15; ++idx)
            {
                t = state[1];
                state[1] = F(state[1], key[(int)idx]) ^ state[0];
                state[0] = t;
            }
            state[0] = F(state[1], key[15]) ^ state[0];
            InvIp(state, output);
        }
    }
}
