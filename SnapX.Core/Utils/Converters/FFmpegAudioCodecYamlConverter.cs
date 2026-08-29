// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Utils;
using SnapX.Core.ScreenCapture;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SnapX.Core.Utils.Converters;

/// <summary>
/// Reads legacy audio-codec names from YAML configurations. The old AAC
/// member was named <c>libvoaacenc</c> (an encoder that no longer exists in
/// modern FFmpeg), while the actual command has always used the built-in
/// <c>aac</c> encoder. Keeping the replacement name invisible to existing
/// configs avoids a load failure and rewrites the file on the next save.
/// </summary>
public class FFmpegAudioCodecYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(FFmpegAudioCodec);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer deserializer)
    {
        var scalar = parser.Consume<Scalar>();
        string value = scalar.Value;
        if (value.Equals("libvoaacenc", StringComparison.OrdinalIgnoreCase))
        {
            return FFmpegAudioCodec.aac;
        }

        if (Enum.TryParse<FFmpegAudioCodec>(value, ignoreCase: true, out FFmpegAudioCodec codec))
        {
            return codec;
        }

        // A config written by another fork or a newer SnapX may contain an
        // encoder this build cannot represent. Refusing to load the whole
        // settings file would prevent every other setting from working, so
        // keep the record usable and fall back to the codec the recorder
        // can definitely emit. The warning is intentionally best-effort.
        DebugHelper.WriteLine($"Unknown FFmpeg audio codec '{value}' in configuration; using aac.");
        return FFmpegAudioCodec.aac;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        emitter.Emit(new Scalar(value?.ToString() ?? FFmpegAudioCodec.aac.ToString()));
    }
}
