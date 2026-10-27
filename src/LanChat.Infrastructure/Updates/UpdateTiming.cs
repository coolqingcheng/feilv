using System.Diagnostics;
using System.Globalization;

namespace LanChat.Infrastructure.Updates;

internal sealed class UpdateTiming(string directory, string fileName = "update-timing.log")
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public async Task RecordAsync(string phase)
    {
        try
        {
            var line = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " | "
                + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms | " + phase + Environment.NewLine;
            await File.AppendAllTextAsync(Path.Combine(directory, fileName), line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
