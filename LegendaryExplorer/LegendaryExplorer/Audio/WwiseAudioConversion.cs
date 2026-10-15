using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Audio;

/// <summary>Shared audio conversion for import, replacement, and Soundplorer's conversion tools.</summary>
public static class WwiseAudioConversion
{
    public static Task<string> ConvertAsync(MEGame game, string fileOrFolderPath, WwiseConversionSettingsPackage conversionSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileOrFolderPath);
        ArgumentNullException.ThrowIfNull(conversionSettings);
        int sampleRate = conversionSettings.TargetSamplerate;
        return Task.Run(() => Convert(game, fileOrFolderPath, sampleRate));
    }

    private static string Convert(MEGame game, string path, int sampleRate)
    {
        WwiseAudioEncoder.ValidateGame(game);
        bool singleFile = !Directory.Exists(path);
        string[] inputs;
        if (singleFile)
        {
            if (!AudioInputConverter.IsSupportedAudioFile(path))
                throw new NotSupportedException("Only WAV and MP3 files can be converted to game audio.");
            if (!File.Exists(path))
                throw new FileNotFoundException("The audio input file could not be found.", path);
            inputs = [Path.GetFullPath(path)];
        }
        else
        {
            inputs = Directory.EnumerateFiles(path).Where(AudioInputConverter.IsSupportedAudioFile)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (inputs.Length == 0)
            throw new InvalidOperationException("No WAV or MP3 files were found to convert.");
        var duplicate = inputs.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Multiple input files would produce the same output name '{duplicate.Key}'. Rename one of those files and try again.");

        string extension = game == MEGame.ME3 ? ".ogg" : ".wem";
        string output = Path.Combine(singleFile ? Path.GetDirectoryName(inputs[0])! : Path.GetFullPath(path), "Converted");
        string staging = Path.Combine(Path.GetTempPath(), $"LEX_AudioConversion_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            // Finish encoding before publishing files. A failed input leaves previous conversions intact.
            foreach (string input in inputs)
                WwiseAudioEncoder.ConvertAudioFile(game, input,
                    Path.Combine(staging, Path.GetFileNameWithoutExtension(input) + extension), sampleRate);
            Directory.CreateDirectory(output);
            foreach (string file in Directory.EnumerateFiles(staging))
                File.Copy(file, Path.Combine(output, Path.GetFileName(file)), overwrite: true);
            return singleFile ? Path.Combine(output, Path.GetFileNameWithoutExtension(inputs[0]) + extension) : output;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
