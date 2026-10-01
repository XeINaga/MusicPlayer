// QrcNative.dll — QQ Music QRC 3DES core, ported 1:1 from
// Services/QqQrcDecrypter.cs (DesHelper). The managed version is kept as a
// fallback and as the reference implementation for the consistency test.
//
// Built by the csproj "BuildQrcNative" target (MinGW g++, -shared -static)
// into QrcNative.dll next to the app exe. Exported entry points:
//   qrc_decrypt_blocks(in, out, len) — 3DES-ECB decrypt whole 8-byte blocks
//       with QQ's key; trailing bytes (<8) are copied through. Returns 0 on
//       success, -1 on bad arguments.
//   qrc_decrypt_hex_to_raw? — no: hex decoding and zlib stay on the managed
//       side (System.IO.Compression already wraps the native zlib), so the
//       P/Invoke boundary only carries the block-cipher hot loop.

#include <cstdint>
#include <cstring>

namespace {

// QQ Music's fixed key (same literal as the managed side). The trailing NUL
// is not part of the key — only bytes [0..23] are used by the schedule.
constexpr char kQqKeyLiteral[] = "!@#)(*$%123ZXC!@!@#)(NHL";
static_assert(sizeof(kQqKeyLiteral) == 25, "key must be 24 chars + NUL");

struct Schedule { uint8_t sub[16][6]; };

Schedule g_sched[3];
bool g_ready = false;

inline uint32_t BitNum(const uint8_t* a, int b, int c)
{
    return static_cast<uint32_t>((a[b / 32 * 4 + 3 - b % 32 / 8] >> (7 - b % 8)) & 0x01) << c;
}
inline uint8_t BitNumIntR(uint32_t a, int b, int c)
{
    return static_cast<uint8_t>(((a >> (31 - b)) & 0x00000001u) << c);
}
inline uint32_t BitNumIntL(uint32_t a, int b, int c)
{
    return ((a << b) & 0x80000000u) >> c;
}
inline uint32_t SboxBit(uint8_t a)
{
    return static_cast<uint32_t>((a & 0x20) | ((a & 0x1f) >> 1) | ((a & 0x01) << 4));
}

const uint8_t Sbox1[64] = {14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6, 2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13};
const uint8_t Sbox2[64] = {15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9};
const uint8_t Sbox3[64] = {10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12};
const uint8_t Sbox4[64] = {7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14};
const uint8_t Sbox5[64] = {2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3};
const uint8_t Sbox6[64] = {12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13};
const uint8_t Sbox7[64] = {4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12};
const uint8_t Sbox8[64] = {13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11};

constexpr uint32_t Encrypt = 1;
constexpr uint32_t Decrypt = 0;

void KeySchedule(const uint8_t* key, Schedule& sched, uint32_t mode)
{
    static const uint32_t keyRndShift[16] = {1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1};
    static const uint32_t keyPermC[28] = {56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35};
    static const uint32_t keyPermD[28] = {62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3};
    static const uint32_t keyCompression[48] = {13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31};

    uint32_t C = 0, D = 0;
    for (uint32_t i = 0, j = 31; i < 28; ++i, --j)
        C |= BitNum(key, static_cast<int>(keyPermC[i]), static_cast<int>(j));
    for (uint32_t i = 0, j = 31; i < 28; ++i, --j)
        D |= BitNum(key, static_cast<int>(keyPermD[i]), static_cast<int>(j));

    for (uint32_t i = 0; i < 16; ++i)
    {
        C = ((C << keyRndShift[i]) | (C >> (28 - keyRndShift[i]))) & 0xfffffff0u;
        D = ((D << keyRndShift[i]) | (D >> (28 - keyRndShift[i]))) & 0xfffffff0u;

        const uint32_t toGen = (mode == Decrypt) ? (15 - i) : i;

        for (uint32_t j = 0; j < 6; ++j) sched.sub[toGen][j] = 0;
        uint32_t j = 0;
        for (; j < 24; ++j)
            sched.sub[toGen][j / 8] |= BitNumIntR(C, static_cast<int>(keyCompression[j]), static_cast<int>(7 - j % 8));
        for (; j < 48; ++j)
            sched.sub[toGen][j / 8] |= BitNumIntR(D, static_cast<int>(keyCompression[j]) - 27, static_cast<int>(7 - j % 8));
    }
}

void Ip(uint32_t* state, const uint8_t* input)
{
    state[0] = BitNum(input,57,31)|BitNum(input,49,30)|BitNum(input,41,29)|BitNum(input,33,28)|BitNum(input,25,27)|BitNum(input,17,26)|BitNum(input,9,25)|BitNum(input,1,24)|BitNum(input,59,23)|BitNum(input,51,22)|BitNum(input,43,21)|BitNum(input,35,20)|BitNum(input,27,19)|BitNum(input,19,18)|BitNum(input,11,17)|BitNum(input,3,16)|BitNum(input,61,15)|BitNum(input,53,14)|BitNum(input,45,13)|BitNum(input,37,12)|BitNum(input,29,11)|BitNum(input,21,10)|BitNum(input,13,9)|BitNum(input,5,8)|BitNum(input,63,7)|BitNum(input,55,6)|BitNum(input,47,5)|BitNum(input,39,4)|BitNum(input,31,3)|BitNum(input,23,2)|BitNum(input,15,1)|BitNum(input,7,0);
    state[1] = BitNum(input,56,31)|BitNum(input,48,30)|BitNum(input,40,29)|BitNum(input,32,28)|BitNum(input,24,27)|BitNum(input,16,26)|BitNum(input,8,25)|BitNum(input,0,24)|BitNum(input,58,23)|BitNum(input,50,22)|BitNum(input,42,21)|BitNum(input,34,20)|BitNum(input,26,19)|BitNum(input,18,18)|BitNum(input,10,17)|BitNum(input,2,16)|BitNum(input,60,15)|BitNum(input,52,14)|BitNum(input,44,13)|BitNum(input,36,12)|BitNum(input,28,11)|BitNum(input,20,10)|BitNum(input,12,9)|BitNum(input,4,8)|BitNum(input,62,7)|BitNum(input,54,6)|BitNum(input,46,5)|BitNum(input,38,4)|BitNum(input,30,3)|BitNum(input,22,2)|BitNum(input,14,1)|BitNum(input,6,0);
}

void InvIp(const uint32_t* state, uint8_t* input)
{
    input[3] = (uint8_t)(BitNumIntR(state[1],7,7)|BitNumIntR(state[0],7,6)|BitNumIntR(state[1],15,5)|BitNumIntR(state[0],15,4)|BitNumIntR(state[1],23,3)|BitNumIntR(state[0],23,2)|BitNumIntR(state[1],31,1)|BitNumIntR(state[0],31,0));
    input[2] = (uint8_t)(BitNumIntR(state[1],6,7)|BitNumIntR(state[0],6,6)|BitNumIntR(state[1],14,5)|BitNumIntR(state[0],14,4)|BitNumIntR(state[1],22,3)|BitNumIntR(state[0],22,2)|BitNumIntR(state[1],30,1)|BitNumIntR(state[0],30,0));
    input[1] = (uint8_t)(BitNumIntR(state[1],5,7)|BitNumIntR(state[0],5,6)|BitNumIntR(state[1],13,5)|BitNumIntR(state[0],13,4)|BitNumIntR(state[1],21,3)|BitNumIntR(state[0],21,2)|BitNumIntR(state[1],29,1)|BitNumIntR(state[0],29,0));
    input[0] = (uint8_t)(BitNumIntR(state[1],4,7)|BitNumIntR(state[0],4,6)|BitNumIntR(state[1],12,5)|BitNumIntR(state[0],12,4)|BitNumIntR(state[1],20,3)|BitNumIntR(state[0],20,2)|BitNumIntR(state[1],28,1)|BitNumIntR(state[0],28,0));
    input[7] = (uint8_t)(BitNumIntR(state[1],3,7)|BitNumIntR(state[0],3,6)|BitNumIntR(state[1],11,5)|BitNumIntR(state[0],11,4)|BitNumIntR(state[1],19,3)|BitNumIntR(state[0],19,2)|BitNumIntR(state[1],27,1)|BitNumIntR(state[0],27,0));
    input[6] = (uint8_t)(BitNumIntR(state[1],2,7)|BitNumIntR(state[0],2,6)|BitNumIntR(state[1],10,5)|BitNumIntR(state[0],10,4)|BitNumIntR(state[1],18,3)|BitNumIntR(state[0],18,2)|BitNumIntR(state[1],26,1)|BitNumIntR(state[0],26,0));
    input[5] = (uint8_t)(BitNumIntR(state[1],1,7)|BitNumIntR(state[0],1,6)|BitNumIntR(state[1],9,5)|BitNumIntR(state[0],9,4)|BitNumIntR(state[1],17,3)|BitNumIntR(state[0],17,2)|BitNumIntR(state[1],25,1)|BitNumIntR(state[0],25,0));
    input[4] = (uint8_t)(BitNumIntR(state[1],0,7)|BitNumIntR(state[0],0,6)|BitNumIntR(state[1],8,5)|BitNumIntR(state[0],8,4)|BitNumIntR(state[1],16,3)|BitNumIntR(state[0],16,2)|BitNumIntR(state[1],24,1)|BitNumIntR(state[0],24,0));
}

uint32_t F(uint32_t state, const uint8_t* key)
{
    uint8_t lrg[6];
    const uint32_t t1 = BitNumIntL(state,31,0)|((state&0xf0000000)>>1)|BitNumIntL(state,4,5)|BitNumIntL(state,3,6)|((state&0x0f000000)>>3)|BitNumIntL(state,8,11)|BitNumIntL(state,7,12)|((state&0x00f00000)>>5)|BitNumIntL(state,12,17)|BitNumIntL(state,11,18)|((state&0x000f0000)>>7)|BitNumIntL(state,16,23);
    const uint32_t t2 = BitNumIntL(state,15,0)|((state&0x0000f000)<<15)|BitNumIntL(state,20,5)|BitNumIntL(state,19,6)|((state&0x00000f00)<<13)|BitNumIntL(state,24,11)|BitNumIntL(state,23,12)|((state&0x000000f0)<<11)|BitNumIntL(state,28,17)|BitNumIntL(state,27,18)|((state&0x0000000f)<<9)|BitNumIntL(state,0,23);
    lrg[0]=(uint8_t)((t1>>24)&0xff);
    lrg[1]=(uint8_t)((t1>>16)&0xff);
    lrg[2]=(uint8_t)((t1>>8)&0xff);
    lrg[3]=(uint8_t)((t2>>24)&0xff);
    lrg[4]=(uint8_t)((t2>>16)&0xff);
    lrg[5]=(uint8_t)((t2>>8)&0xff);
    lrg[0]^=key[0]; lrg[1]^=key[1]; lrg[2]^=key[2]; lrg[3]^=key[3]; lrg[4]^=key[4]; lrg[5]^=key[5];

    const int b0 = (int)SboxBit((uint8_t)(lrg[0]>>2));
    const int b1 = (int)SboxBit((uint8_t)(((lrg[0]&0x03)<<4)|(lrg[1]>>4)));
    const int b2 = (int)SboxBit((uint8_t)(((lrg[1]&0x0f)<<2)|(lrg[2]>>6)));
    const int b3 = (int)SboxBit((uint8_t)(lrg[2]&0x3f));
    const int b4 = (int)SboxBit((uint8_t)(lrg[3]>>2));
    const int b5 = (int)SboxBit((uint8_t)(((lrg[3]&0x03)<<4)|(lrg[4]>>4)));
    const int b6 = (int)SboxBit((uint8_t)(((lrg[4]&0x0f)<<2)|(lrg[5]>>6)));
    const int b7 = (int)SboxBit((uint8_t)(lrg[5]&0x3f));

    uint32_t s = ((uint32_t)Sbox1[b0] << 28) |
                 ((uint32_t)Sbox2[b1] << 24) |
                 ((uint32_t)Sbox3[b2] << 20) |
                 ((uint32_t)Sbox4[b3] << 16) |
                 ((uint32_t)Sbox5[b4] << 12) |
                 ((uint32_t)Sbox6[b5] << 8)  |
                 ((uint32_t)Sbox7[b6] << 4)  |
                 (uint32_t)  Sbox8[b7];

    s = BitNumIntL(s,15,0)|BitNumIntL(s,6,1)|BitNumIntL(s,19,2)|BitNumIntL(s,20,3)|BitNumIntL(s,28,4)|BitNumIntL(s,11,5)|BitNumIntL(s,27,6)|BitNumIntL(s,16,7)|BitNumIntL(s,0,8)|BitNumIntL(s,14,9)|BitNumIntL(s,22,10)|BitNumIntL(s,25,11)|BitNumIntL(s,4,12)|BitNumIntL(s,17,13)|BitNumIntL(s,30,14)|BitNumIntL(s,9,15)|BitNumIntL(s,1,16)|BitNumIntL(s,7,17)|BitNumIntL(s,23,18)|BitNumIntL(s,13,19)|BitNumIntL(s,31,20)|BitNumIntL(s,26,21)|BitNumIntL(s,2,22)|BitNumIntL(s,8,23)|BitNumIntL(s,18,24)|BitNumIntL(s,12,25)|BitNumIntL(s,29,26)|BitNumIntL(s,5,27)|BitNumIntL(s,21,28)|BitNumIntL(s,10,29)|BitNumIntL(s,3,30)|BitNumIntL(s,24,31);
    return s;
}

void Crypt(const uint8_t* input, uint8_t* output, const Schedule& sched)
{
    uint32_t state[2];
    Ip(state, input);
    for (uint32_t idx = 0; idx < 15; ++idx)
    {
        const uint32_t t = state[1];
        state[1] = F(state[1], sched.sub[idx]) ^ state[0];
        state[0] = t;
    }
    state[0] = F(state[1], sched.sub[15]) ^ state[0];
    InvIp(state, output);
}

void EnsureSchedule()
{
    if (g_ready)
        return;
    // mode semantics copied from the managed TripleDesKeySetup (Decrypt path).
    const uint8_t* key = reinterpret_cast<const uint8_t*>(kQqKeyLiteral);
    KeySchedule(key + 0,  g_sched[2], Decrypt);
    KeySchedule(key + 8,  g_sched[1], Encrypt);
    KeySchedule(key + 16, g_sched[0], Decrypt);
    g_ready = true;
}

} // namespace

extern "C" __declspec(dllexport)
int qrc_decrypt_blocks(const uint8_t* in, uint8_t* out, int len)
{
    if (!in || !out || len < 0)
        return -1;

    EnsureSchedule();

    const int aligned = len - (len % 8);
    for (int i = 0; i < aligned; i += 8)
    {
        Crypt(in + i, out + i, g_sched[0]);
        Crypt(out + i, out + i, g_sched[1]);
        Crypt(out + i, out + i, g_sched[2]);
    }
    // Truncated/corrupt payloads: trailing bytes pass through, same as managed.
    for (int i = aligned; i < len; ++i)
        out[i] = in[i];
    return 0;
}
