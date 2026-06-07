// CharacterCarousel.cs - THE LOST EXPEDITION lobby: the PODIUM STAGE (PART B + PART A gate + PART C scope).
//
// Renders a straight, camera-FACING ROW of PARTY_SLOTS=6 glowing platforms by the campfire. Each
// occupied slot = a live survivalist model (capsule fallback) standing on a CharacterPodium disc, with
// a 3-row floating world-space label (crown / name / READY pill). Empty slots = a dim podium + a
// glowing '+'. The LOCAL player cycles their OWN character (HUD < > arrows + A/D here) -> SetCharacter;
// the local model swaps live; the authoritative LobbyMember.OnUpdate re-syncs it.
//
// PART C: the lineup is SCOPED to the local member's party - only LobbyMember rows whose PartyId equals
// myPartyId are shown (a freshly-authed unpartied '' member still shows itself via the '' bucket).
//
// PART A: nothing renders during Connecting/Auth - SetActive(false) hides the rendered roots. The
// pre-auth 'You' ghost path is DELETED (PART A guarantees the local member row exists by Lobby phase).
//
// Models are keyed by Identity and REUSED across rebuilds (re-instantiated only if CharacterId changed)
// to avoid instantiate/GC churn. Capsule fallback PER member if a prefab is missing; a catalog
// insert/update also triggers a rebuild (capsules upgrade to real models once ModelKey resolves).
//
// The character caption panel (DisplayName/Archetype/Blurb + arrows) lives in LobbyHud; this class
// exposes DisplayName/Archetype/Blurb + Cycle(±1) + Locked for the HUD to drive.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.HighDefinition;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class CharacterCarousel : MonoBehaviour
{
    public const int PARTY_SLOTS = 6;

    CharacterLibrary library;
    Camera lobbyCam;
    Canvas canvas;
    Vector3 lineupCenter;
    Vector3 fireWorld;
    float spacing;
    System.Func<float, float, float> groundY;

    class Slot
    {
        public GameObject go;            // the model (null for an empty slot)
        public uint characterId;
        public LobbyUI.WorldLabelHandle crown;   // crown row
        public LobbyUI.WorldLabelHandle name;     // name row
        public LobbyUI.WorldLabelHandle pill;     // READY / NOT READY row
        public LobbyUI.WorldLabelHandle plus;     // glowing '+' for empty slots
        public CharacterPodium podium;
        public Vector3 foot;
        public bool isLocal;
        public bool occupied;
        public GameObject heroLights;    // key + rim cinematic rig, LOCAL selected slot only
    }

    // Fixed array of PARTY_SLOTS podiums (some empty).
    Slot[] grid;

    // Ordered list of catalog CharacterIds (ascending). index -> CharacterId (for local cycling).
    readonly List<uint> order = new();
    uint localDisplayedId;
    bool optimisticLocal;   // a Cycle() pick is pending; don't clobber it from the authoritative row yet
    bool locked;            // local member IsReady -> cycling disabled
    bool wired;
    bool tornDown;
    bool activeRendered = true;

    public void Init(CharacterLibrary lib, Camera cam, Canvas cv, Vector3 center, Vector3 fire,
                     float lineupSpacing, float lineupArcDeg, System.Func<float, float, float> groundYFn)
    {
        library = lib;
        lobbyCam = cam;
        canvas = cv;
        lineupCenter = center;
        fireWorld = fire;
        // Spacing trimmed so 6 podiums fit the lobbyCam frustum (per perf note - was 1.5, ~7.5m wide).
        spacing = Mathf.Min(lineupSpacing, 1.1f);
        groundY = groundYFn;
    }

    // PART A: toggle the rendered roots. Nothing visible during Connecting/Auth.
    public void SetActive(bool on)
    {
        activeRendered = on;
        if (grid != null)
        {
            foreach (var s in grid)
            {
                if (s == null) continue;
                if (s.go != null) s.go.SetActive(on);
                SetGroup(s.crown, on); SetGroup(s.name, on); SetGroup(s.pill, on); SetGroup(s.plus, on);
            }
        }
        if (on) RebuildLineup();
    }

    static void SetGroup(LobbyUI.WorldLabelHandle h, bool on) { if (h.group != null && !on) h.group.alpha = 0f; }

    // Wire catalog + member callbacks inside the LOBBY subscription's OnApplied (deferred to auth).
    public void WireSubscription()
    {
        if (wired) return;
        wired = true;

        EnsureGrid();
        RebuildOrder();   // catalog is backfilled by now

        var db = GameManager.Conn.Db;
        db.LobbyCharacterCatalog.OnInsert += (ctx, c) => { RebuildOrder(); RebuildLineup(); };
        db.LobbyCharacterCatalog.OnUpdate += (ctx, _old, c) => { RebuildOrder(); RebuildLineup(); };

        db.LobbyMember.OnInsert += (ctx, m) => RebuildLineup();
        db.LobbyMember.OnUpdate += (ctx, _old, m) => RebuildLineup();
        db.LobbyMember.OnDelete += (ctx, m) => RebuildLineup();

        RebuildLineup();
    }

    void RebuildOrder()
    {
        order.Clear();
        foreach (var c in GameManager.Conn.Db.LobbyCharacterCatalog.Iter().OrderBy(c => c.CharacterId))
            order.Add(c.CharacterId);
    }

    // ---- public API for the HUD caption strip + arrows ----
    public string DisplayName => CatalogField(localDisplayedId, c => c.DisplayName, $"Character {localDisplayedId}");
    public string Archetype  => CatalogField(localDisplayedId, c => c.Archetype, "");
    public string Blurb      => CatalogField(localDisplayedId, c => c.Blurb, "");
    public bool Locked => locked;

    string CatalogField(uint id, System.Func<LobbyCharacterCatalog, string> pick, string fallback)
    {
        if (GameManager.Conn == null) return fallback;
        var cat = GameManager.Conn.Db.LobbyCharacterCatalog.CharacterId.Find(id);
        return cat != null ? pick(cat) : fallback;
    }

    // Cycle the LOCAL player's pick by ±1; optimistic local swap, authoritative OnUpdate re-syncs.
    public void Cycle(int delta)
    {
        if (locked || order.Count == 0 || GameManager.Conn == null) return;
        int idx = order.IndexOf(localDisplayedId);
        if (idx < 0) idx = 0;
        idx = ((idx + delta) % order.Count + order.Count) % order.Count;
        uint target = order[idx];

        localDisplayedId = target;
        optimisticLocal = true;
        GameManager.Conn.Reducers.SetCharacter(target);
        RebuildLineup();
        OnSelectionChanged?.Invoke();
    }

    public System.Action OnSelectionChanged;

    // My party code (PART C). "" = unpartied.
    string MyPartyId()
    {
        if (GameManager.Conn == null) return "";
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        return me != null ? me.PartyId : "";
    }

    void EnsureGrid()
    {
        if (grid != null) return;
        grid = new Slot[PARTY_SLOTS];
        for (int i = 0; i < PARTY_SLOTS; i++)
        {
            var s = new Slot { foot = SlotPosition(i, PARTY_SLOTS) };
            s.podium = new CharacterPodium();
            s.podium.Build(null, s.foot, groundY, false);
            s.crown = LobbyUI.WorldLabel(canvas, 15, LobbyUI.Ember, new Vector2(240f, 22f));
            s.name  = LobbyUI.WorldLabel(canvas, 24, LobbyUI.AshText, new Vector2(280f, 32f));
            s.pill  = LobbyUI.WorldLabel(canvas, 13, LobbyUI.NotReadyGrey, new Vector2(200f, 22f));
            s.plus  = LobbyUI.WorldLabel(canvas, 40, LobbyUI.EmberDim, new Vector2(80f, 80f));
            s.plus.fg.text = "+"; s.plus.shadow.text = "+";

            // AAA typography: route the floating labels through Barlow (Bold name, Medium crown/pill).
            SkinWorldLabel(s.name, StorySequencer.Weight.Bold);
            SkinWorldLabel(s.crown, StorySequencer.Weight.Medium);
            SkinWorldLabel(s.pill, StorySequencer.Weight.Medium);
            SkinWorldLabel(s.plus, StorySequencer.Weight.Light);

            grid[i] = s;
        }
    }

    // ---- the lineup ----
    void RebuildLineup()
    {
        if (tornDown || !activeRendered || canvas == null || GameManager.Conn == null) return;
        EnsureGrid();

        string myParty = MyPartyId();

        // SCOPE to my party (PART C). Fallback: if I am unpartied ('') the '' filter still shows me.
        var members = GameManager.Conn.Db.LobbyMember.Iter()
            .Where(m => m.PartyId == myParty)
            .OrderBy(m => m.Slot)
            .ToList();

        // Sync local lock + displayed id from the authoritative local row.
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me != null)
        {
            locked = me.IsReady;
            if (optimisticLocal && me.CharacterId == localDisplayedId) optimisticLocal = false;
            if (!optimisticLocal) localDisplayedId = me.CharacterId;
        }

        int n = Mathf.Min(members.Count, PARTY_SLOTS);
        int startSlot = (PARTY_SLOTS - n) / 2;   // center the occupied podiums so a solo player isn't shoved to the edge
        for (int i = 0; i < PARTY_SLOTS; i++)
        {
            var s = grid[i];
            int memberIdx = i - startSlot;
            if (memberIdx >= 0 && memberIdx < n)
            {
                var m = members[memberIdx];
                bool isLocal = GameManager.IsLocal(m.Identity);
                uint wantId = isLocal ? localDisplayedId : m.CharacterId;
                FillSlot(s, wantId, isLocal, m);
            }
            else
            {
                EmptySlot(s);
            }
        }
        OnSelectionChanged?.Invoke();
    }

    // Straight ROW of PARTY_SLOTS along the camera-right axis, centered on lineupCenter, all facing cam.
    Vector3 SlotPosition(int index, int count)
    {
        // Lateral axis = perpendicular to (fire - center), in the ground plane (== screen-right-ish).
        Vector3 toFire = fireWorld - lineupCenter; toFire.y = 0f;
        if (toFire.sqrMagnitude < 0.001f) toFire = Vector3.forward;
        toFire.Normalize();
        Vector3 lateral = Vector3.Cross(Vector3.up, toFire); // right-hand perpendicular

        float t = count > 1 ? (index - (count - 1) * 0.5f) : 0f;   // -.. 0 .. +
        Vector3 flat = lineupCenter + lateral * (t * spacing);     // STRAIGHT row (no bow)
        flat.y = groundY != null ? groundY(flat.x, flat.z) : lineupCenter.y;
        return flat;
    }

    void FillSlot(Slot s, uint wantId, bool isLocal, LobbyMember m)
    {
        s.occupied = true;
        s.isLocal = isLocal;
        s.podium.SetEmpty(false);
        s.podium.SetEmphasis(isLocal);

        // (re)build the model if the character changed or the slot was empty.
        if (s.go == null || s.characterId != wantId)
        {
            if (s.go != null) Destroy(s.go);
            s.go = SpawnModel(wantId, s.foot, s.podium.TopY);
            s.characterId = wantId;
        }
        s.go.SetActive(activeRendered);

        // Local pick brighter + scaled 1.06. Set scale FIRST so the bounds-based seating below accounts
        // for the scaled body height (otherwise a 1.06x model's feet would float ~6% off the disc).
        s.go.transform.localScale = isLocal ? Vector3.one * 1.06f : Vector3.one;

        // Face the camera (Y-only turn, doesn't change vertical extent) then ground the feet on the disc.
        FaceCamera(s.go.transform);
        SeatModel(s.go, s.foot, s.podium.TopY);

        // Cinematic key + rim rig on the SELECTED (local) model only - the podium key is a soft warm
        // base, this adds a brighter warm key from camera-front-left and a cool rim from behind so the
        // hero silhouette pops. Parented to the model so it's destroyed with it on a character swap.
        if (isLocal) EnsureHeroLights(s);
        else ClearHeroLights(s);

        // Labels: crown / name / READY pill.
        string nm = !string.IsNullOrEmpty(m.Nickname) ? m.Nickname : m.Username;
        SetLabel(s.name, nm, isLocal ? LobbyUI.EmberSoft : LobbyUI.AshText);

        // Lead marker as letter-spaced text (no glyph - renders reliably in Barlow).
        string crownTxt = m.IsExpeditionLead ? Spaced("LEAD") : "";
        SetLabel(s.crown, crownTxt, LobbyUI.Ember);

        SetLabel(s.pill, m.IsReady ? Spaced("READY") : Spaced("NOT READY"),
                 m.IsReady ? LobbyUI.ReadyGreen : LobbyUI.NotReadyGrey);

        // Hide the '+' on an occupied slot.
        if (s.plus.group != null) s.plus.group.alpha = 0f;
    }

    void EmptySlot(Slot s)
    {
        s.occupied = false;
        s.isLocal = false;
        s.podium.SetEmpty(true);
        ClearHeroLights(s);
        if (s.go != null) { Destroy(s.go); s.go = null; s.characterId = uint.MaxValue; }
        SetLabel(s.crown, "", LobbyUI.Ember);
        SetLabel(s.name, "", LobbyUI.AshText);
        SetLabel(s.pill, "", LobbyUI.NotReadyGrey);
        // '+' becomes visible via the per-frame label loop (group alpha set there).
    }

    static void SetLabel(LobbyUI.WorldLabelHandle h, string text, Color col)
    {
        if (h.fg == null) return;
        h.fg.text = text; h.shadow.text = text;
        h.fg.color = col;
    }

    // Route a world-space label (both fg + shadow Text) through a Barlow weight.
    static void SkinWorldLabel(LobbyUI.WorldLabelHandle h, StorySequencer.Weight w)
    {
        if (h.fg != null) StorySequencer.Apply(h.fg, w);
        if (h.shadow != null) StorySequencer.Apply(h.shadow, w);
    }

    // Thin-space (U+2009 via U+0020 here for legacy-font safety) letter-spacing for the small pills.
    static string Spaced(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i < s.Length - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    void FaceCamera(Transform t)
    {
        if (lobbyCam == null) return;
        Vector3 toCam = lobbyCam.transform.position - t.position; toCam.y = 0f;
        if (toCam.sqrMagnitude <= 0.001f) return;
        toCam.Normalize();

        // 3/4 HERO POSE: instead of facing the cam dead-on (mannequin), blend 25% of a turn toward the
        // fire side so the body presents at a confident three-quarter angle. Falls back to straight-on
        // if the turn vector degenerates (e.g. model standing on the fire axis).
        Vector3 awayFromFire = (t.position - fireWorld); awayFromFire.y = 0f;
        Vector3 heroDir = toCam;
        if (awayFromFire.sqrMagnitude > 0.001f)
        {
            Vector3 blended = Vector3.Lerp(toCam, awayFromFire.normalized, 0.25f);
            if (blended.sqrMagnitude > 0.001f) heroDir = blended.normalized;
        }
        t.rotation = Quaternion.LookRotation(heroDir);
    }

    // Cinematic 2-light rig (warm KEY from camera-front-left + cool RIM from behind) on the local hero.
    // Built once per (re)spawn; the lobby cam is static so world-space placement computed here stays
    // correct. Parented to the model so a character swap (which Destroys the model) takes the rig with it.
    void EnsureHeroLights(Slot s)
    {
        if (s.go == null) return;
        if (s.heroLights != null && s.heroLights.transform.parent == s.go.transform) return; // already built on this model
        ClearHeroLights(s);

        // Chest-height aim point on the model, in world space.
        Vector3 chest = new Vector3(s.foot.x, s.podium.TopY + 1.3f, s.foot.z);

        var rig = new GameObject("HeroLights");
        rig.transform.SetParent(s.go.transform, true);   // worldPositionStays - local rotation is irrelevant, lights are aimed in world space
        s.heroLights = rig;

        // Camera-relative basis (flattened to the ground plane) so the rig reads from the viewer's POV.
        Vector3 camPos = lobbyCam != null ? lobbyCam.transform.position : chest + new Vector3(0f, 0.5f, -3f);
        Vector3 toCam = camPos - chest; toCam.y = 0f;
        if (toCam.sqrMagnitude < 0.001f) toCam = Vector3.back;
        toCam.Normalize();
        Vector3 camRight = Vector3.Cross(Vector3.up, toCam); // screen-left/right axis

        // KEY: warm, from camera side + a touch left + above, close in. Brightest source on the face.
        Vector3 keyPos = chest + toCam * 1.6f - camRight * 0.9f + Vector3.up * 0.8f;
        AddHeroLight(rig.transform, "HeroKey", keyPos, chest, LightType.Spot,
                     new Color(1f, 0.85f, 0.62f), range: 5f, spotAngle: 55f, intensity: 900f);

        // RIM: cool, from BEHIND the model (opposite the camera) + right + high, to edge-light the silhouette.
        Vector3 rimPos = chest - toCam * 1.4f + camRight * 1.1f + Vector3.up * 1.3f;
        AddHeroLight(rig.transform, "HeroRim", rimPos, chest, LightType.Spot,
                     new Color(0.45f, 0.68f, 1f), range: 5f, spotAngle: 50f, intensity: 700f);
    }

    // HDRP light: AddComponent<Light>() + AddComponent<HDAdditionalLightData>(), hd.intensity = candela.
    // (No AddHDLight, no LightUnit - those APIs do not exist and break the assembly.)
    static void AddHeroLight(Transform parent, string name, Vector3 pos, Vector3 aimAt, LightType type,
                             Color color, float range, float spotAngle, float intensity)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, true);
        go.transform.position = pos;
        Vector3 dir = aimAt - pos;
        if (dir.sqrMagnitude > 0.0001f) go.transform.rotation = Quaternion.LookRotation(dir.normalized);

        var l = go.AddComponent<Light>();
        l.type = type;
        l.color = color;
        l.range = range;
        if (type == LightType.Spot) l.spotAngle = spotAngle;
        var hd = go.AddComponent<HDAdditionalLightData>();
        hd.intensity = intensity;   // RAW CANDELA via the legacyLight passthrough - NOT lumens
    }

    void ClearHeroLights(Slot s)
    {
        if (s.heroLights != null) { Destroy(s.heroLights); s.heroLights = null; }
    }

    GameObject SpawnModel(uint characterId, Vector3 foot, float topY)
    {
        var cat = GameManager.Conn.Db.LobbyCharacterCatalog.CharacterId.Find(characterId);
        GameObject prefab = (cat != null && library != null) ? library.Resolve(cat.ModelKey) : null;

        GameObject go;
        if (prefab != null)
        {
            go = Instantiate(prefab, foot, Quaternion.identity);
            go.name = $"Lineup_{(cat != null ? cat.ModelKey : characterId.ToString())}";
            // BUILD-SAFE HDRP fix (Resources HDRP set / HDRP/Lit re-shade fallback) so the lineup skin
            // never renders solid white in a player build. Pack prefabs ship Built-in-shader materials.
            SurvivalistModels.FixHdrp(go);
            // Rig as a non-local model: adds an Animator on the shared Locomotion controller, so the
            // lineup model plays the idle pose (Speed stays 0 - CharacterLocomotion sees no movement).
            // localControl:false -> a CapsuleCollider is added, harmless on a podium model.
            CharacterRig.Apply(go, false);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var col = go.GetComponent<Collider>(); if (col) Destroy(col);
            go.name = $"Lineup_placeholder_{characterId}";
        }
        SeatModel(go, foot, topY);
        return go;
    }

    // Seat the model so its FEET (lowest renderer point) rest exactly on the disc top (topY) - no
    // floating, no clipping into the podium. The model's root pivot is often at the hips (mocap rigs)
    // or otherwise offset, so we measure the real bounds rather than guess: place the root at topY,
    // read the combined world-space renderer bounds, then lift/drop by the gap to its lowest point.
    void SeatModel(GameObject go, Vector3 foot, float topY)
    {
        go.transform.position = new Vector3(foot.x, topY, foot.z);

        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return;   // capsule fallback has its renderer; safety only

        bool have = false;
        Bounds b = new Bounds();
        foreach (var r in rends)
        {
            if (r == null) continue;
            if (!have) { b = r.bounds; have = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!have) return;

        // Gap between the disc top and the model's current lowest point; correct the root by it.
        float feetGap = topY - b.min.y;
        go.transform.position += Vector3.up * feetGap;
    }

    // HDRP material fixup now lives in SurvivalistModels.FixHdrp (BUILD-SAFE: Resources HDRP set / HDRP/Lit
    // re-shade fallback), called from SpawnModel above so the lineup skins also work in a player build. The
    // old editor-only FixHdrpMaterials helper was removed in favour of that shared, build-safe path.

    void Update()
    {
        if (tornDown || !activeRendered) return;

        // Legacy A/D cycling of the LOCAL pick (uGUI arrows live in the HUD).
        if (!locked)
        {
            if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) Cycle(+1);
            if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) Cycle(-1);
        }

        var cam = lobbyCam;
        if (cam == null || grid == null) return;

        // Reposition the 3-row label stack + '+' over each podium each frame.
        foreach (var s in grid)
        {
            if (s == null) continue;
            Vector3 head = new Vector3(s.foot.x, s.podium.TopY, s.foot.z);
            if (s.occupied)
            {
                PositionLabel(s.name,  head + Vector3.up * 2.05f, cam);
                PositionLabel(s.crown, head + Vector3.up * 2.45f, cam);
                PositionLabel(s.pill,  head + Vector3.up * 1.75f, cam);
                if (s.plus.group != null) s.plus.group.alpha = 0f;
            }
            else
            {
                PositionLabel(s.plus, head + Vector3.up * 1.1f, cam);
                if (s.name.group  != null) s.name.group.alpha  = 0f;
                if (s.crown.group != null) s.crown.group.alpha = 0f;
                if (s.pill.group  != null) s.pill.group.alpha  = 0f;
            }
        }
    }

    void PositionLabel(LobbyUI.WorldLabelHandle label, Vector3 worldPos, Camera cam)
    {
        if (label.group == null) return;
        Vector3 sp = cam.WorldToScreenPoint(worldPos);
        if (sp.z <= 0f) { label.group.alpha = 0f; return; }
        label.group.alpha = 1f;
        label.rect.position = new Vector3(sp.x, sp.y, 0f);
    }

    public void Teardown()
    {
        tornDown = true;
        if (grid != null)
        {
            foreach (var s in grid)
            {
                if (s == null) continue;
                if (s.go != null) Destroy(s.go);
                if (s.podium != null) s.podium.Destroy();
                DestroyLabel(s.crown); DestroyLabel(s.name); DestroyLabel(s.pill); DestroyLabel(s.plus);
            }
            grid = null;
        }
    }

    static void DestroyLabel(LobbyUI.WorldLabelHandle h) { if (h.rect != null) Object.Destroy(h.rect.gameObject); }
}
