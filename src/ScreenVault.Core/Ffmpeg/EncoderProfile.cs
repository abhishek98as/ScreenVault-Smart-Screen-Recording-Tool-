namespace ScreenVault.Core.Ffmpeg;

public sealed record EncoderProfile(
    string Name,
    string FilterChainSuffix,
    IReadOnlyList<string> EncoderArgs)
{
    public static int GetQValue(Settings.VideoQuality quality) => quality switch
    {
        Settings.VideoQuality.Small => 32,
        Settings.VideoQuality.High => 23,
        _ => 28 // Balanced
    };

    public static EncoderProfile Create(string profileName, Settings.VideoQuality quality, int frameRate)
    {
        var q = GetQValue(quality);
        var g = Math.Max(2, 2 * frameRate);

        return profileName.ToLowerInvariant() switch
        {
            "nvenc-d3d11" => new EncoderProfile(
                "nvenc-d3d11",
                string.Empty,
                [
                    "-c:v", "h264_nvenc",
                    "-preset", "p4",
                    "-rc", "vbr",
                    "-cq", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-b:v", "0",
                    "-maxrate", "8M",
                    "-bufsize", "16M",
                    "-profile:v", "high",
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            "amf-d3d11" => new EncoderProfile(
                "amf-d3d11",
                string.Empty,
                [
                    "-c:v", "h264_amf",
                    "-quality", "balanced",
                    "-rc", "cqp",
                    "-qp_i", (q - 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-qp_p", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            "qsv-hwmap" => new EncoderProfile(
                "qsv-hwmap",
                ",hwmap=derive_device=qsv,format=qsv",
                [
                    "-c:v", "h264_qsv",
                    "-preset", "medium",
                    "-global_quality", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            "nvenc-sysmem" => new EncoderProfile(
                "nvenc-sysmem",
                ",hwdownload,format=bgra",
                [
                    "-c:v", "h264_nvenc",
                    "-preset", "p4",
                    "-rc", "vbr",
                    "-cq", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-b:v", "0",
                    "-maxrate", "8M",
                    "-bufsize", "16M",
                    "-profile:v", "high",
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            "amf-sysmem" => new EncoderProfile(
                "amf-sysmem",
                ",hwdownload,format=bgra",
                [
                    "-c:v", "h264_amf",
                    "-quality", "balanced",
                    "-rc", "cqp",
                    "-qp_i", (q - 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-qp_p", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            "qsv-sysmem" => new EncoderProfile(
                "qsv-sysmem",
                ",hwdownload,format=nv12",
                [
                    "-c:v", "h264_qsv",
                    "-preset", "medium",
                    "-global_quality", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ]),

            _ => new EncoderProfile(
                "x264",
                ",hwdownload,format=bgra",
                [
                    "-c:v", "libx264",
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-crf", q.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p",
                    "-g", g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ])
        };
    }
}
