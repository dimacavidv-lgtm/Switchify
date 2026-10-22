// Conversión de vídeo y audio con Media Foundation (Windows.Media.Transcoding), sin programas externos:
// usa los códecs que trae Windows (H.264, AAC, MP3, WMV/WMA, FLAC, WAV) y la aceleración por hardware.

using System;
using System.IO;
using System.Linq;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

static class MediaOps
{
    public static readonly string[] VideoExts = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".3gp", ".webm" };
    // (OGG/Opus no: Windows no trae su decodificador y todas las conversiones fallarían.)
    public static readonly string[] AudioExts = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma" };

    // Avisa del progreso (texto corto para el icono de la bandeja); null = terminado.
    public static Action<string> Progress;

    public static bool IsVideo(string path)
    {
        return File.Exists(path) && VideoExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    public static bool IsAudio(string path)
    {
        return File.Exists(path) && AudioExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    // Convierte `input` al formato `target` (".mp4", ".mp3", ...). Con `compress`, MP4 más ligero.
    public static string Convert(string input, string target, bool compress)
    {
        string output = Paths.Beside(input, compress ? " comprimido" : "", target);
        var profile = Profile(target);

        var inFile = WinRt.Wait(StorageFile.GetFileFromPathAsync(input));
        if (compress) Lighten(profile, inFile);

        var folder = WinRt.Wait(StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)));
        var outFile = WinRt.Wait(folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.FailIfExists));
        bool ok = false;
        try
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = WinRt.Wait(transcoder.PrepareFileTranscodeAsync(inFile, outFile, profile));
            if (!prepared.CanTranscode) throw new NotSupportedException(Reason(prepared.FailureReason));

            string name = Path.GetFileName(input);
            var op = prepared.TranscodeAsync();
            op.Progress = new AsyncActionProgressHandler<double>((info, percent) =>
            {
                if (Progress != null) Progress(Short(name) + " " + (int)percent + " %");
            });
            WinRt.Wait(op);
            // Comprimir no debe dejar un archivo más grande que el original.
            if (compress && new FileInfo(output).Length >= new FileInfo(input).Length)
                throw new InvalidOperationException("Este vídeo ya está muy comprimido: no se puede reducir más sin perder calidad.");
            ok = true;
            return output;
        }
        finally
        {
            if (Progress != null) Progress(null);
            // Si falla, no se deja un archivo a medias.
            if (!ok) { try { WinRt.Wait(outFile.DeleteAsync()); } catch { } }
        }
    }

    static MediaEncodingProfile Profile(string target)
    {
        switch (target)
        {
            // "Auto": misma resolución y fotogramas que el original.
            case ".mp4": return MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            case ".wmv": return MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto);
            case ".mp3": return MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);
            case ".m4a": return MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            case ".wav": return MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            case ".flac": return MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High);
            case ".wma": return MediaEncodingProfile.CreateWma(AudioEncodingQuality.High);
            default: throw new NotSupportedException("Formato no soportado: " + target);
        }
    }

    // Vídeo más ligero: misma resolución, pero ~40 % de la tasa de bits original (entre 0,8 y 4 Mbps)
    // y audio a 128 kbps. El perfil se rellena con los datos del propio archivo: en modo "automático"
    // Windows ignora la tasa de bits pedida con algunos contenedores (MKV, WebM) y el resultado pesaba más.
    static void Lighten(MediaEncodingProfile profile, StorageFile input)
    {
        MediaEncodingProfile source = null;
        try { source = WinRt.Wait(MediaEncodingProfile.CreateFromFileAsync(input)); }
        catch { }

        // Tasa de bits real: la que declara el archivo y la que sale de tamaño ÷ duración; la menor de las dos
        // (en formatos muy eficientes, como WebM, la declarada no sirve y el resultado salía más grande).
        uint declared = source != null && source.Video != null ? source.Video.Bitrate : 0, measured = 0;
        try
        {
            var props = WinRt.Wait(input.Properties.GetVideoPropertiesAsync());
            double seconds = props.Duration.TotalSeconds;
            ulong size = WinRt.Wait(input.GetBasicPropertiesAsync()).Size;
            if (seconds > 0) measured = (uint)Math.Min(uint.MaxValue, size * 8 / seconds);
        }
        catch { }
        uint bitrate = declared > 0 && measured > 0 ? Math.Min(declared, measured) : Math.Max(declared, measured);
        if (bitrate == 0) bitrate = 6000000;

        if (profile.Video != null)
        {
            if (source != null && source.Video != null && source.Video.Width > 0 && source.Video.Height > 0)
            {
                profile.Video.Width = source.Video.Width;
                profile.Video.Height = source.Video.Height;
                if (source.Video.FrameRate.Numerator > 0 && source.Video.FrameRate.Denominator > 0)
                {
                    profile.Video.FrameRate.Numerator = source.Video.FrameRate.Numerator;
                    profile.Video.FrameRate.Denominator = source.Video.FrameRate.Denominator;
                }
                profile.Video.PixelAspectRatio.Numerator = 1;
                profile.Video.PixelAspectRatio.Denominator = 1;
            }
            profile.Video.Bitrate = (uint)Math.Max(500000, Math.Min(4000000, bitrate * 0.4));
        }
        if (profile.Audio != null) profile.Audio.Bitrate = 128000;
    }

    static string Reason(TranscodeFailureReason reason)
    {
        switch (reason)
        {
            case TranscodeFailureReason.CodecNotFound:
                return "Windows no tiene el códec para este archivo (prueba a instalar las extensiones de vídeo de Microsoft Store).";
            case TranscodeFailureReason.InvalidProfile:
                return "Este archivo no se puede convertir a ese formato.";
            default:
                return "Windows no ha podido convertir este archivo.";
        }
    }

    static string Short(string name)
    {
        return name.Length <= 28 ? name : name.Substring(0, 12) + "…" + name.Substring(name.Length - 12);
    }
}
