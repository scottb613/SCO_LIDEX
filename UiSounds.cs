// SCO LIDEX - interface sound dispatcher
// Copyright (C) Scott Brunner, Beast of Burden

using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace ORterr;

// All interface audio passes through this class. Stopping every player before
// starting the next one guarantees that rapid UI transitions never overlap.
internal static class UiSounds
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, SoundPlayer> Players = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime lastButtonPressUtc = DateTime.MinValue;

    internal static void PlayPress()
    {
        lock (SyncRoot)
        {
            lastButtonPressUtc = DateTime.UtcNow;
            PlayLocked("SCOpress.wav");
        }
    }

    internal static void PlayProgress()
    {
        lock (SyncRoot)
        {
            // A button can immediately cause an ordinary status transition.
            // Keep the button sound in that case instead of replacing it.
            if (DateTime.UtcNow - lastButtonPressUtc < TimeSpan.FromMilliseconds(300))
            {
                return;
            }

            PlayLocked("SCOpluck.wav");
        }
    }

    internal static void PlaySuccess() => Play("SCOsuccess.wav");

    internal static void PlayBuzz() => Play("SCObuzz.wav");

    internal static void PlayTic() => Play("SCOtic.wav");

    private static void Play(string fileName)
    {
        lock (SyncRoot)
        {
            PlayLocked(fileName);
        }
    }

    private static void PlayLocked(string fileName)
    {
        try
        {
            foreach (SoundPlayer player in Players.Values)
            {
                player.Stop();
            }

            if (!Players.TryGetValue(fileName, out SoundPlayer? selected))
            {
                string path = Path.Combine(AppContext.BaseDirectory, "content", fileName);
                if (!File.Exists(path))
                {
                    return;
                }

                selected = new SoundPlayer(path);
                Players[fileName] = selected;
            }

            selected.Play();
        }
        catch
        {
            // Interface audio is optional and must never interrupt route work.
        }
    }
}
