using System.IO;
using System.Text;

namespace DbClone.UI.Services;

/// <summary>Writes a replacement file beside the destination and swaps it into place atomically.</summary>
internal static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void Quarantine(string path)
    {
        if (!File.Exists(path))
            return;

        var quarantined = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        File.Move(path, quarantined);
    }
}
