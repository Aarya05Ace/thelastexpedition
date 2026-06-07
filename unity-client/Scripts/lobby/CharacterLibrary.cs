// CharacterLibrary.cs — THE LOST EXPEDITION lobby: the ONLY thing wired in the Inspector.
//
// A [Serializable] data holder mapping catalog ModelKey strings -> survivalist prefabs, plus the
// Patron's suit prefab. No asset paths are hardcoded in code: resolution is by ModelKey string at
// runtime against LobbyCharacterCatalog.ModelKey. The LobbyBootstrap exposes one public
// CharacterLibrary field, so the whole manual setup is: fill 4 survivalists + the suit.

using System;
using UnityEngine;

[Serializable]
public class CharacterLibrary
{
    // One catalog ModelKey -> a plain rigged display prefab (no controller).
    [Serializable]
    public struct ModelEntry
    {
        [Tooltip("The catalog ModelKey, e.g. survivalist_medic / survivalist_scout / ...")]
        public string modelKey;
        [Tooltip("The 'Survivalist (N)' prefab (the PLAIN model, NOT FPS_/PlayerArmature/HDRP/URP).")]
        public GameObject prefab;
    }

    [Tooltip("Map ModelKey -> prefab. Fill the 4 survivalists in catalog CharacterId order 0..3.")]
    public ModelEntry[] characters = Array.Empty<ModelEntry>();

    [Tooltip("genSuit.fbx, the Patron / Suit display mesh.")]
    public GameObject suitPrefab;

    // Linear scan (5 entries, trivial). The Inspector-wired list wins; if it's empty/unmapped (e.g. a
    // build where nobody filled it, or the editor auto-wire didn't run) fall back to the BUILD-SAFE
    // SurvivalistModels.ResolvePrefab (Resources primary) so the lineup shows REAL skins instead of
    // capsules. Returns null only if even the Resources copy is missing (caller then uses a capsule).
    public GameObject Resolve(string modelKey)
    {
        if (string.IsNullOrEmpty(modelKey)) return SurvivalistModels.ResolvePrefab(modelKey);
        if (characters != null)
            for (int i = 0; i < characters.Length; i++)
                if (characters[i].modelKey == modelKey && characters[i].prefab != null)
                    return characters[i].prefab;
        // Not in the Inspector list -> build-safe Resources fallback (maps the same class keys).
        return SurvivalistModels.ResolvePrefab(modelKey);
    }
}
