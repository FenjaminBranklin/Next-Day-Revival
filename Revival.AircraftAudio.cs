using UnityEngine;

namespace NextDayRevival
{
    internal static class AircraftAudio
    {
        /// <summary>Destruction only, on every peer before fire/crash effects.
        /// Existing aircraft loops are propulsion (including the Mi-8 carrier).
        /// Disable them so a late native Start/Play cannot revive the sound.
        /// One-shot crash voices and newly spawned fire remain audible.</summary>
        internal static void StopEngines(GameObject go)
        {
            if (go == null) return;
            An2Visual plane = go.GetComponent<An2Visual>();
            if (plane != null) plane.StopEngine();
            AudioSource[] sources = go.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null || !source.loop) continue;
                source.Stop();
                source.playOnAwake = false;
                source.mute = true;
                source.enabled = false;
            }
        }
    }
}
