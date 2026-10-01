# ffmpeg-lite — 精简版 ffmpeg.exe（2.1 MB）

本目录的 `ffmpeg.exe` 是从 FFmpeg 8.0 源码以 `--disable-everything` 编译的
精简版，只保留**响度扫描**所需的能力（动态音量功能用）：

- 解封装：mov(m4a/mp4) mp3 flac ogg opus wav aac ac3 dts ape wv tak matroska(mka) asf(wma) dsf aiff
- 解码：mp3/flac/aac/opus/vorbis/ape/wavpack/tak/ac3/eac3/dts/wma/alac/PCM/ADPCM/DSD
- 滤镜：loudnorm + aresample/aformat/anull/volume
- 输出：null muxer + pcm_s16le（`-f null NUL` 必需）
- 协议：file；无网络、无视频、无 x86asm、静态链接、LGPL 2.1

**播放不经过这个 exe**——播放和音效滤镜走 FFmpegInteropX 自带的
avcodec/avformat/avfilter dll。删除此 exe 只会让没有 ReplayGain 标签的歌
失去响度自动增益。

## 重新编译（MSYS2 ucrt64 工具链）

```bash
# 下载源码 https://ffmpeg.org/releases/ffmpeg-8.0.tar.xz 并解压
cd ffmpeg-8.0
export PATH="/e/mingw/msys2/ucrt64/bin:$PATH"
./configure \
  --disable-everything --disable-autodetect --disable-doc --disable-debug \
  --disable-network --disable-avdevice --disable-swscale \
  --disable-x86asm --enable-small \
  --enable-protocol=file \
  --enable-demuxer=mov,mp3,flac,ogg,opus,wav,aac,ac3,dts,ape,wv,tak,matroska,asf,dsf,aiff \
  --enable-decoder=mp1,mp1float,mp2,mp2float,mp3,mp3float,mp3adu,mp3adufloat,mp3on4,mp3on4float,flac,aac,aac_latm,opus,vorbis,ape,wavpack,tak,ac3,eac3,dca,wmav1,wmav2,alac,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s32le,pcm_u8,pcm_s8,pcm_f32le,pcm_f64le,pcm_alaw,pcm_mulaw,adpcm_ms,adpcm_ima_wav,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar \
  --enable-parser=mpegaudio,flac,aac,opus,vorbis,ac3,dca,tak,mlp \
  --enable-filter=aresample,aformat,anull,loudnorm,volume \
  --enable-muxer=null --enable-encoder=pcm_s16le \
  --extra-ldflags="-static"
mingw32-make -j ffmpeg.exe   # 无 make 时用 ucrt64 自带 mingw32-make
```

验证：`ffmpeg.exe -i <音频文件> -af loudnorm=I=-14:TP=-1.0:LRA=11:print_format=json -f null NUL`
输出 JSON 含 `input_i` 即可用（与应用 LoudnessCache.AnalyzeWithFfmpegAsync
的调用参数一致）。

新增音频格式支持时，把对应 demuxer/decoder/parser 加进 configure 清单重编，
否则该格式无法响度扫描（音效开启时该格式也无法走 FFmpeg 解码回退）。
