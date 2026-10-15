# Wwise-free audio format verification

The bundled `vorbis.dll` identifies itself as Xiph libVorbis 1.3.7. Its quality 0.4
mono and stereo setup codebooks match both bundled Wwise codebook libraries at
8, 16, 22.05, 24, 32, 44.1 and 48 kHz. Matching compares decoded Huffman lengths
and lookup values, rather than the ordered/sparse bit representation. Across
the wider quality sweep, 300 setup conversions succeeded; independently
expanding all 168 Legendary Edition setup variants reproduced their original
setup bodies bit for bit. Higher quality settings at low sample rates do not
always match the original trilogy codebook library, so the encoder uses 0.4.

Surround encoding disables channel coupling using libVorbis's documented
`OV_ECTL_COUPLING_SET` control before finalizing its setup. This avoids the
six-channel coupled mode's extra codebooks, which are missing from both game
libraries at 44.1/48 kHz. The uncoupled setup sweep passes for one through eight
channels at 8, 16, 22.05, 24, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz in both
libraries (176 combinations, with normal coupled stereo retained). High input
sample rates can therefore remain unchanged when the user selects As Input.

Wwise's multichannel PCM and Vorbis media use Microsoft/WAV speaker order. The
uncoupled encoder preserves that order directly; applying the general Ogg
Vorbis speaker permutation would incorrectly move channels. Explicit
WAVEFORMATEXTENSIBLE speaker masks are preserved. Plain WAV inputs use standard
mono, stereo, 3.0, quad, 5.0, 5.1, 7.0 and 7.1 masks by channel count. Regression
tests encode a distinct frequency in every channel and verify each frequency
returns in the same decoded channel, in addition to checking the media mask.
Extensible PCM and floating-point WAV files are read directly, avoiding NAudio's
ACM conversion path for surround input. The adapter owns and disposes its WAV
source. Explicit-mask tests verify all three supported games while also using
the As Input sample-rate path.

## Reference media

The two embedded files in
`LegendaryExplorer/LegendaryExplorer/WwiseTestData/LEX Test LE2/GeneratedSoundBanks/Windows/Test_Bank.bnk`
are mono 44.1 kHz Vorbis with block exponents 8/11 and 201-byte compact setups.

| Source ID | PCM frames | Packet overlap sum | End padding | First seek delta (frames, bytes) |
| --- | ---: | ---: | ---: | --- |
| 194050468 | 29403 | 29888 | 485 | 16576, 3439 |
| 461437153 | 17436 | 17984 | 548 | 16960, 3622 |

The overlap sum adds `(previous block size + current block size) / 4` after the
first audio packet. End padding is the overlap sum minus actual PCM frames.
Seek entries are little-endian unsigned 16-bit frame and byte deltas, emitted
after at least 16384 accumulated frames. Their byte base is the compact setup's
length prefix. The loop start is the setup size including its prefix; the loop
end is the total setup/audio size excluding the seek table. These formulas
reproduce both reference files exactly.

The setup cache hash is **FNV-1**, with initial value 2166136261 and multiplier
16777619, over the compact setup payload without its length prefix. Multiplication
precedes XOR. This reproduces `0xB3DEA448` in both bank references, and the hashes
of eight additional embedded files extracted from the ME3/LE3 test packages.

| Reference setup | x86 decoder bytes | x64 decoder bytes | Setup hash |
| --- | ---: | ---: | --- |
| Test bank, mono 44.1 kHz, 201-byte setup | 18180 | 18636 | B3DEA448 |
| ME3 WwiseAudio.pcc, mono 32 kHz, 174-byte setup | 15492 | 15892 | AD0D3729 |
| LE3 WwiseAudio.pcc, mono 44.1 kHz, 186-byte setup | 16192 | 16616 | FC772C73 |

ME3 runtime files use a 66-byte `fmt ` chunk, extension flag `0x18`, and mono
channel mask 4. LE files use the same chunk length, flag zero, and mono channel
configuration `0x4101`. ME3 authoring output instead splits its 24-byte `fmt `
and 42-byte `vorb` chunks; the existing import transformation merges them.

## Native runtime verification

Wwise 2019.1.6.7110's actual `WwiseSoundEngine.dll` was used as an independent
diagnostic decoder through `AK::SoundEngine::DecodeBank`. It is not required by
LEX or its tests. Single-media banks containing the user's imported stereo
44.1 kHz audio and six known-working game streams were tested with that runtime.

The initial imported file played in vgmstream but failed native decoding because
the encoder appended a RIFF alignment byte after an odd-sized final `data` chunk.
Wwise requires the final chunk to end at the end of the media file. Removing that
byte and updating the RIFF size made the native decoder return `AK_Success` and
produce all 9,149,808 stereo frames with a nonzero signal. Adding such a byte to
three known-working streams independently reproduced the native decoder failure.
The encoder now omits final `data` padding, and regression tests cover odd-sized
media for all three supported games, including the ME3 import transformation.

## Native soundbank loading and streaming

The media-only decoder did not exercise soundbank hierarchy loading. A second
runtime check used the actual LE3 Init bank from `WwiseAudio.pcc`, export 427.
The older, working `James_Rom` bank loaded successfully, while the generated
`NewBank` failed `LoadBank` with result 15 before opening any streamed audio.

The generated hierarchy listed its ActorMixer before its Sound children. Wwise
resolves the mixer's child IDs during loading, so these children must already
exist. Reordering the same raw hierarchy items to put the Sounds before the
ActorMixer made loading succeed. No header, source, effect, routing, or decoder
allocation fields needed to change. The builder now emits children first;
serialized-order regression tests cover both LE2 and LE3.

With the game's Init bank and the reordered current bank, the native runtime
successfully loaded the bank, prepared the event, and played both event variants
through a real streaming file hook serving the current AFC. Seeking to 120
seconds continued playback; seeking near the end completed with a normal
EndOfEvent callback. A control using zero `InMemoryMediaSize` in the older bank
also loaded, prepared, played, and sought successfully. The zero streaming field
is therefore retained. These checks exercise native bank loading and streaming;
the game's own level and sequence execution still needs an in-game check.

Fresh output from the corrected encoder also passed native decoding for all 12
LE2/LE3 diagnostic cases: mono and stereo at 24 and 44.1 kHz, six channels at
48 kHz, and a four-millisecond mono clip at 24 kHz. The bundled vgmstream needs
one readable lookahead byte beyond the RIFF container for the equivalent very
short ME3 clip. The preview/extraction helper appends that byte only to its
consumed temporary decoder input, leaving exported and imported media canonical.

## Decoder allocation reserves

Decoder arena requirements depend on setup complexity and cannot be inferred
accurately from channels and block size alone. The current 512 KiB fields are
deliberately conservative reserves, substantially above the observed reference
requirements. They are not an exact allocation calculation. The native Wwise
runtime successfully decoded the repaired user media with these unchanged
reserves and setup metadata, including streamed playback and seeking in the
subsequent bank-loading diagnostic. The game's own level and sequence execution
still requires an in-game check.

## Primary references

- [Xiph Vorbis 1.3.7 ABI](https://github.com/xiph/vorbis/blob/v1.3.7/include/vorbis/codec.h)
- [Xiph encoder setup and coupling controls](https://github.com/xiph/vorbis/blob/v1.3.7/include/vorbis/vorbisenc.h)
- [Xiph Vorbis setup specification](https://xiph.org/vorbis/doc/Vorbis_I_spec.html)
- [Audiokinetic channel configuration and WAV ordering](https://www.audiokinetic.com/files/?get=2015.1.9_5624%2FWwise_UserGuide_en.pdf)
- [vgmstream Wwise media parsing](https://github.com/vgmstream/vgmstream/blob/master/src/meta/wwise.c)
- [vgmstream Wwise Vorbis reconstruction](https://github.com/vgmstream/vgmstream/blob/master/src/coding/vorbis_custom_utils_wwise.c)
- [ww2ogg packed codebook format](https://github.com/hcs64/ww2ogg/blob/master/src/codebook.cpp)

An independent [wav2wem implementation](https://github.com/pas2k/wav2wem) corroborates
the seek and padding field interpretations. Its approximate allocation scaling
does not reproduce the reference files above; no source code from that GPL
implementation was copied into the product implementation.
