// CharacterPodium.cs — THE LOST EXPEDITION lobby: ONE glowing platform per lineup slot (PART B).
//
// Plain (non-MonoBehaviour) helper, owned per-slot by CharacterCarousel.Slot. Builds:
//   * a flat emissive DISC (a squashed Cylinder primitive) the survivalist stands on, seated on the
//     real terrain via the shared GroundY raycast,
//   * a slightly larger thin RING cylinder behind it for a glow edge,
//   * ONE warm Point KEY light per podium.
//
// HDRP LIGHT RULE (review-critical): HDAdditionalLightData.intensity is RAW CANDELA via the [Obsolete]
// legacyLight passthrough — NOT lumens. Set ~250 candela (local pick ~450), NOT 10000/13000, or HDRP
// auto-exposure clips to white with 6 podiums + the campfire. NO AddHDLight, NO LightUnit. The real
// glow comes from the disc emissive + Bloom (per technical-preferences), the light is just a soft key.
//
// Material: HDRP/Lit with _EmissiveColor + _UseEmissiveIntensity + _EmissiveIntensity, all guarded by
// mat.HasProperty, falling back to HDRP/Unlit _BaseColor (same pattern as the campfire-particle
// fallback) so a missing property never throws.

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Vector3 = UnityEngine.Vector3;

public class CharacterPodium
{
    GameObject root;        // parent for disc + ring + light
    GameObject disc;
    GameObject ring;
    Light keyLight;
    HDAdditionalLightData keyHd;
    Material discMat;
    Material ringMat;

    System.Func<float, float, float> groundY;
    Vector3 footPos;        // where the model's FEET sit (top of the disc)
    bool emphasized;
    bool empty;

    static readonly Color Ember     = new Color(1.00f, 0.55f, 0.18f);
    static readonly Color EmberDim   = new Color(0.55f, 0.27f, 0.10f);
    const float DiscThickness = 0.04f;
    const float DiscRadius    = 0.70f;   // localScale.xz ~1.4 on a unit-diameter cylinder

    // TopY = the world Y the model's feet stand on (disc top surface). Read AFTER Build.
    public float TopY { get; private set; }

    public void Build(Transform parent, Vector3 foot, System.Func<float, float, float> groundYFn, bool emphasizedSlot)
    {
        groundY = groundYFn;
        emphasized = emphasizedSlot;

        root = new GameObject("Podium");
        if (parent != null) root.transform.SetParent(parent, false);

        // ---- disc ----
        disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = "PodiumDisc";
        var dcol = disc.GetComponent<Collider>(); if (dcol != null) Object.Destroy(dcol);
        disc.transform.SetParent(root.transform, false);
        disc.transform.localScale = new Vector3(DiscRadius * 2f, DiscThickness, DiscRadius * 2f);
        discMat = MakeEmissiveMaterial(Ember, emphasized ? 7.5f : 5.0f, 0.18f);
        var drend = disc.GetComponent<Renderer>(); if (drend != null) drend.sharedMaterial = discMat;

        // ---- glow ring (slightly larger + thinner, sits just under the disc lip) ----
        ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "PodiumRing";
        var rcol = ring.GetComponent<Collider>(); if (rcol != null) Object.Destroy(rcol);
        ring.transform.SetParent(root.transform, false);
        ring.transform.localScale = new Vector3(DiscRadius * 2.5f, DiscThickness * 0.5f, DiscRadius * 2.5f);
        ringMat = MakeEmissiveMaterial(EmberDim, emphasized ? 9f : 6f, 0.10f);
        var rrend = ring.GetComponent<Renderer>(); if (rrend != null) rrend.sharedMaterial = ringMat;

        // ---- ONE warm point key light ----
        var lgo = new GameObject("PodiumKey");
        lgo.transform.SetParent(root.transform, false);
        keyLight = lgo.AddComponent<Light>();
        keyLight.type = LightType.Point;
        keyLight.color = new Color(1f, 0.6f, 0.25f);
        keyLight.range = 3f;
        keyHd = lgo.AddComponent<HDAdditionalLightData>();
        keyHd.intensity = emphasized ? 450f : 250f;   // RAW CANDELA — NOT lumens, NOT thousands

        SetPosition(foot);
    }

    // HDRP/Lit emissive material, guarded; falls back to HDRP/Unlit _BaseColor.
    static Material MakeEmissiveMaterial(Color rgb, float emissiveIntensity, float baseTint)
    {
        Shader lit = Shader.Find("HDRP/Lit");
        if (lit != null)
        {
            var mat = new Material(lit);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(rgb.r * baseTint, rgb.g * baseTint, rgb.b * baseTint, 1f));
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", rgb * Mathf.LinearToGammaSpace(1f));
            if (mat.HasProperty("_UseEmissiveIntensity")) mat.SetFloat("_UseEmissiveIntensity", 1f);
            if (mat.HasProperty("_EmissiveIntensity")) mat.SetFloat("_EmissiveIntensity", emissiveIntensity);
            if (mat.HasProperty("_EmissiveExposureWeight")) mat.SetFloat("_EmissiveExposureWeight", 0f);
            mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return mat;
        }
        // Fallback: HDRP/Unlit (or any unlit) tinted by _BaseColor.
        Shader unlit = Shader.Find("HDRP/Unlit");
        if (unlit == null) unlit = Shader.Find("Unlit/Color");
        var fmat = new Material(unlit != null ? unlit : Shader.Find("Sprites/Default"));
        if (fmat.HasProperty("_BaseColor")) fmat.SetColor("_BaseColor", rgb);
        if (fmat.HasProperty("_Color")) fmat.SetColor("_Color", rgb);
        return fmat;
    }

    // Seat the disc on the terrain; TopY = disc top surface (model feet stand here).
    public void SetPosition(Vector3 foot)
    {
        footPos = foot;
        if (root == null) return;
        float gy = groundY != null ? groundY(foot.x, foot.z) : foot.y;
        // The cylinder primitive is 2 units tall before scaling; after localScale.y = thickness its
        // half-height is thickness*1.0. Sit its CENTER so its TOP face is at gy + a hair.
        float halfDisc = DiscThickness; // cylinder half-height in local-unit terms == 1*scale.y
        Vector3 discCenter = new Vector3(foot.x, gy + halfDisc, foot.z);
        if (disc != null) disc.transform.position = discCenter;
        if (ring != null) ring.transform.position = new Vector3(foot.x, gy + halfDisc * 0.4f, foot.z);
        if (keyLight != null) keyLight.transform.position = new Vector3(foot.x, gy + 1.7f, foot.z);
        TopY = gy + halfDisc * 2f;   // top face of the disc
    }

    public void SetEmphasis(bool local)
    {
        emphasized = local;
        if (keyHd != null) keyHd.intensity = local ? 450f : 250f;
        if (discMat != null && discMat.HasProperty("_EmissiveIntensity")) discMat.SetFloat("_EmissiveIntensity", local ? 7.5f : 5.0f);
        if (ringMat != null && ringMat.HasProperty("_EmissiveIntensity")) ringMat.SetFloat("_EmissiveIntensity", local ? 9f : 6f);
    }

    // Empty slots dim the disc + drop the key so an empty platform reads as "open".
    public void SetEmpty(bool isEmpty)
    {
        if (empty == isEmpty) return;
        empty = isEmpty;
        if (discMat != null && discMat.HasProperty("_EmissiveIntensity")) discMat.SetFloat("_EmissiveIntensity", isEmpty ? 2.2f : (emphasized ? 7.5f : 5.0f));
        if (ringMat != null && ringMat.HasProperty("_EmissiveIntensity")) ringMat.SetFloat("_EmissiveIntensity", isEmpty ? 2.8f : (emphasized ? 9f : 6f));
        if (keyHd != null) keyHd.intensity = isEmpty ? 90f : (emphasized ? 450f : 250f);
    }

    public void Destroy()
    {
        if (root != null) Object.Destroy(root);
        if (discMat != null) Object.Destroy(discMat);
        if (ringMat != null) Object.Destroy(ringMat);
        root = null; disc = null; ring = null; keyLight = null; keyHd = null;
    }
}
