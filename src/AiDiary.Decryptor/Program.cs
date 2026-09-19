using System.Text.Json;
using AiDiary.Crypto;

namespace AiDiary.Decryptor;

/// <summary>
/// Reference decryptor. The archive outlives the application: given a
/// recovery key and a directory containing recovery.dat, keys.json and the
/// .md.enc objects, this writes plain .md files.
///
/// Usage:
///   ai-diary-decryptor --key KEY --dir DIR [--out OUT]
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var key = ReadOption(args, "--key", "--key=")
                ?? Environment.GetEnvironmentVariable("DIARY_RECOVERY_KEY")
                ?? throw new InvalidOperationException("Missing --key. Pass your 26-character recovery key.");
            var dir = ReadOption(args, "--dir", "--dir=")
                ?? throw new InvalidOperationException("Missing --dir. Pass the directory containing the encrypted archive.");
            var outDir = ReadOption(args, "--out", "--out=")
                ?? Path.Combine(dir, "decrypted");

            Directory.CreateDirectory(outDir);

            var recoveryDatPath = Path.Combine(dir, "recovery.dat");
            var keysPath = Path.Combine(dir, "keys.json");
            if (!File.Exists(recoveryDatPath) || !File.Exists(keysPath))
            {
                throw new InvalidOperationException(
                    "The directory must contain recovery.dat and keys.json (both come from the encrypted export).");
            }

            var dat = JsonSerializer.Deserialize<RecoveryDat>(File.ReadAllText(recoveryDatPath))
                ?? throw new InvalidOperationException("recovery.dat is malformed.");
            var keys = JsonSerializer.Deserialize<KeysFile>(File.ReadAllText(keysPath))
                ?? throw new InvalidOperationException("keys.json is malformed.");

            var seed = RecoveryKey.Parse(key);
            var kek = RecoveryKey.DeriveKek(seed, dat.UserId);

            byte[] ukek;
            try
            {
                ukek = AesGcmCrypto.Open(kek, Convert.FromBase64String(dat.RecoveryWrappedUkek));
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                Console.Error.WriteLine("The recovery key does not match recovery.dat for this account.");
                return 2;
            }

            var count = 0;
            foreach (var note in keys.Notes)
            {
                var inputPath = Path.Combine(dir, note.Filename);
                if (!File.Exists(inputPath))
                {
                    Console.Error.WriteLine($"Skipping {note.Filename}: file not found in {dir}.");
                    continue;
                }
                var file = File.ReadAllBytes(inputPath);
                var dek = AesGcmCrypto.Open(ukek, Convert.FromBase64String(note.WrappedDek));
                var plaintext = FileEnvelope.Open(file, dek);

                var outputName = note.Filename.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? note.Filename[..^4]
                    : note.Filename + ".md";
                var outputPath = Path.Combine(outDir, outputName);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outDir);
                File.WriteAllBytes(outputPath, plaintext);
                Console.WriteLine($"Wrote {outputPath}");
                count++;
            }
            Console.WriteLine($"Decrypted {count} note(s) into {outDir}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name, string prefix)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                return args[i + 1];
            }
            if (args[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return args[i][prefix.Length..];
            }
        }
        return null;
    }
}
